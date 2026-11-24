using System.Threading.Channels;
using BitmexBot.Core;
using Microsoft.AspNetCore.SignalR;

namespace BitmexBot.Web;

/// <summary>Manual order or one raised by a strategy. Percentages are from the average entry price.</summary>
public sealed record TradeRequest(
    string Side, EntryOrderType Type, int Contracts, double? LimitPrice = null, int OffsetTicks = 0, bool PostOnly = true,
    double? TakeProfitPct = null, double? StopLossPct = null, int TimeoutSeconds = 0, string Source = "manual");

public sealed record TradeView(string Id, string Source, string Side, int Qty, int FilledQty, double? AvgEntry,
    double? TakeProfit, double? StopLoss, string State, string? Note, DateTimeOffset Created);

/// <summary>
/// Places entries and keeps a take profit and stop loss attached to whatever has filled. Exchanges don't all offer
/// linked one-cancels-other orders, so this does it: when one exit fills, the other is cancelled. Exchange events are
/// handled one at a time on a worker so partial fills can't race each other. Also enforces the risk limits.
/// </summary>
public sealed class OrderManager
{
    readonly TradingEngine _engine;
    readonly IExchange _ex;
    readonly RiskOptions _risk;
    readonly IHubContext<TradingHub> _hub;
    readonly ILogger _log;
    readonly Lock _gate = new();
    readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    readonly List<Trade> _trades = [];
    readonly Queue<DateTimeOffset> _recentOrders = new();
    DateOnly _day;
    double _dayStartEquity;
    long _seq;

    public bool AutoTrading { get; private set; }
    public string? AutoTradingOffReason { get; private set; } = "Off until you switch it on";

    public OrderManager(TradingEngine engine, IExchange exchange, RiskOptions risk, IHubContext<TradingHub> hub, ILogger log)
    {
        _engine = engine;
        _ex = exchange;
        _risk = risk;
        _hub = hub;
        _log = log;
        _ = Task.Run(async () =>
        {
            await foreach (var job in _work.Reader.ReadAllAsync())
            {
                try { await job(); }
                catch (Exception ex) { Report($"Order handling failed: {ex.Message}"); }
            }
        });
    }

    public IReadOnlyList<TradeView> Trades { get { lock (_gate) return _trades.Select(t => t.View()).Reverse().Take(100).ToList(); } }

    public void SetAutoTrading(bool on)
    {
        lock (_gate)
        {
            if (on && !_ex.CanTrade) throw new InvalidOperationException("The exchange has no API key configured.");
            AutoTrading = on;
            AutoTradingOffReason = on ? null : "Switched off";
        }
    }

    // ---------- placing ----------

    /// <summary>Called on the engine thread when a strategy with AutoTrade fires. Returns what happened, for the signal log.</summary>
    public string TrySubmitForStrategy(StrategyDefinition def, Signal signal)
    {
        var req = new TradeRequest(signal == Signal.Long ? "Buy" : "Sell", def.Order.Type, def.Order.Contracts, null, def.Order.OffsetTicks,
            def.Order.PostOnly, def.Order.TakeProfitPct, def.Order.StopLossPct,
            (int)(Timeframes.Size(def.Timeframe).TotalSeconds * Math.Max(1, def.Order.TimeoutBars)), def.Id);
        lock (_gate)
        {
            if (!AutoTrading) return $"alert only (auto trading off: {AutoTradingOffReason})";
            if (_trades.Any(t => t.Source == def.Id && t.State != "Closed")) return "skipped: this strategy already has a trade open";
            if (_engine.Market.PositionQty != 0) return "skipped: a position is already open";
            if (Validate(req) is { } problem) return $"blocked: {problem}";
            var trade = Register(req);
            _work.Writer.TryWrite(() => PlaceEntry(trade));
            return $"order sent ({req.Type} {req.Side} {req.Contracts})";
        }
    }

    /// <summary>Manual order from the UI. Throws with a readable message if a risk check or the exchange rejects it.</summary>
    public async Task<TradeView> SubmitManual(TradeRequest req)
    {
        Trade trade;
        lock (_gate)
        {
            if (Validate(req) is { } problem) throw new InvalidOperationException(problem);
            trade = Register(req);
        }
        var done = new TaskCompletionSource();
        _work.Writer.TryWrite(async () =>
        {
            try { await PlaceEntry(trade); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        });
        await done.Task;
        lock (_gate) return trade.View();
    }

    string? Validate(TradeRequest req)
    {
        if (!_ex.CanTrade) return "the exchange has no API key configured";
        if (req.Side is not ("Buy" or "Sell")) return "side must be Buy or Sell";
        var m = _engine.Market;
        if (req.Contracts <= 0) return "contracts must be positive";
        if (m.LotSize > 1 && req.Contracts % m.LotSize != 0) return $"contracts must be a multiple of {m.LotSize}";
        if (req.Contracts > _risk.MaxContracts) return $"order is larger than the {_risk.MaxContracts} contract limit";
        int after = m.PositionQty + (req.Side == "Buy" ? req.Contracts : -req.Contracts);
        if (Math.Abs(after) > _risk.MaxContracts) return $"position would exceed the {_risk.MaxContracts} contract limit";
        if (req.TakeProfitPct is <= 0 || req.StopLossPct is <= 0) return "take profit and stop loss must be above 0%";
        var now = DateTimeOffset.UtcNow;
        while (_recentOrders.Count > 0 && now - _recentOrders.Peek() > TimeSpan.FromMinutes(1)) _recentOrders.Dequeue();
        if (_recentOrders.Count >= _risk.MaxOrdersPerMinute) return $"more than {_risk.MaxOrdersPerMinute} orders in the last minute";
        if (req.Type == EntryOrderType.Limit && req.LimitPrice is null && m.BestBid is null && m.LastPrice is null) return "no price yet";
        return null;
    }

    Trade Register(TradeRequest req)
    {
        _recentOrders.Enqueue(DateTimeOffset.UtcNow);
        var t = new Trade($"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000:D5}{++_seq:D3}", req);
        _trades.Add(t);
        if (_trades.Count > 500) _trades.RemoveAll(x => x.State == "Closed" && _trades.IndexOf(x) < 100);
        return t;
    }

    async Task PlaceEntry(Trade t)
    {
        var m = _engine.Market;
        var r = t.Request;
        bool buy = r.Side == "Buy";
        double? price = null;
        if (r.Type == EntryOrderType.Limit)
        {
            double reference = r.LimitPrice ?? (buy ? m.BestBid : m.BestAsk) ?? m.LastPrice!.Value;
            price = RoundToTick(r.LimitPrice is null ? reference + (buy ? -1 : 1) * r.OffsetTicks * m.TickSize : reference, m.TickSize);
        }
        try
        {
            t.EntryOrderId = await _ex.PlaceOrderAsync(new OrderSpec(t.Id + "-E", r.Side, r.Contracts, r.Type == EntryOrderType.Limit ? "Limit" : "Market",
                price, PostOnly: r.Type == EntryOrderType.Limit && r.PostOnly));
            lock (_gate) if (r.TimeoutSeconds > 0) t.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(r.TimeoutSeconds);
        }
        catch (Exception ex)
        {
            Close(t, $"Entry rejected: {ex.Message}");
            throw;
        }
        Push();
    }

    // ---------- exchange events (called on the engine thread; work is queued) ----------

    public void OnExecution(ExecutionInfo e) => _work.Writer.TryWrite(() => HandleExecution(e));
    public void OnOrder(OrderInfo o) => _work.Writer.TryWrite(() => HandleOrder(o));
    public void OnPosition(int qty) { if (qty == 0) _work.Writer.TryWrite(HandleFlat); }

    public void OnMargin(double equityBtc)
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (today != _day) { _day = today; _dayStartEquity = equityBtc; }
            if (AutoTrading && _risk.DailyLossLimitPct > 0 && equityBtc < _dayStartEquity * (1 - _risk.DailyLossLimitPct / 100))
            {
                AutoTrading = false;
                AutoTradingOffReason = $"Daily loss limit of {_risk.DailyLossLimitPct}% reached";
                Report(AutoTradingOffReason);
            }
        }
    }

    public void OnTimer(DateTimeOffset now)
    {
        List<Trade> expired;
        lock (_gate) expired = _trades.Where(t => t.State == "Pending" && t.ExpiresAt is { } x && now > x && t.EntryOrderId is not null && !t.CancelSent).ToList();
        foreach (var t in expired)
        {
            t.CancelSent = true;
            _work.Writer.TryWrite(() => _ex.CancelAsync(t.EntryOrderId!));
        }
    }

    async Task HandleExecution(ExecutionInfo e)
    {
        Trade? t;
        string? owner = e.ClientId is { Length: > 2 } c ? c[..^2] : null;
        lock (_gate) t = _trades.FirstOrDefault(x => x.Id == owner && x.State != "Closed");
        if (t is null) return;

        if (e.ClientId == t.Id + "-E")
        {
            lock (_gate) { t.FilledQty = e.CumQty; t.AvgEntry = e.AvgPrice; t.State = "Open"; }
            await SyncExits(t);
        }
        else if (e.Status == "Filled" && (e.ClientId == t.Id + "-T" || e.ClientId == t.Id + "-S"))
        {
            bool tp = e.ClientId.EndsWith('T');
            string? other = tp ? t.SlOrderId : t.TpOrderId;
            Close(t, tp ? $"Take profit filled at {e.AvgPrice}" : $"Stop loss filled at {e.AvgPrice}");
            if (other is not null) await _ex.CancelAsync(other);
        }
        Push();
    }

    /// <summary>Creates the TP/SL for the filled quantity, or resizes them after another partial fill.</summary>
    async Task SyncExits(Trade t)
    {
        var r = t.Request;
        if (t.AvgEntry is not { } avg || (r.TakeProfitPct is null && r.StopLossPct is null)) return;
        bool longPos = r.Side == "Buy";
        string exitSide = longPos ? "Sell" : "Buy";
        double tick = _engine.Market.TickSize;

        if (r.TakeProfitPct is { } tpPct)
        {
            if (t.TpOrderId is null)
            {
                t.TakeProfit = RoundToTick(avg * (1 + (longPos ? 1 : -1) * tpPct / 100), tick);
                t.TpOrderId = await _ex.PlaceOrderAsync(new OrderSpec(t.Id + "-T", exitSide, t.FilledQty, "Limit", t.TakeProfit, ReduceOnly: true));
            }
            else await _ex.AmendQtyAsync(t.TpOrderId, t.FilledQty);
        }
        if (r.StopLossPct is { } slPct)
        {
            if (t.SlOrderId is null)
            {
                t.StopLoss = RoundToTick(avg * (1 - (longPos ? 1 : -1) * slPct / 100), tick);
                t.SlOrderId = await _ex.PlaceOrderAsync(new OrderSpec(t.Id + "-S", exitSide, t.FilledQty, "Stop", StopPrice: t.StopLoss, ReduceOnly: true));
            }
            else await _ex.AmendQtyAsync(t.SlOrderId, t.FilledQty);
        }
    }

    Task HandleOrder(OrderInfo o)
    {
        Trade? t;
        lock (_gate) t = _trades.FirstOrDefault(x => o.ClientId == x.Id + "-E" && x.State == "Pending");
        if (t is not null && o.Status is "Canceled" or "Rejected" && o.CumQty == 0)
        {
            Close(t, o.Status == "Rejected" ? "Entry rejected" : "Entry not filled (cancelled)");
            Push();
        }
        return Task.CompletedTask;
    }

    async Task HandleFlat()
    {
        List<Trade> open;
        lock (_gate) open = _trades.Where(t => t.State == "Open").ToList();
        foreach (var t in open)
        {
            Close(t, t.Note ?? "Position closed");
            foreach (var id in new[] { t.TpOrderId, t.SlOrderId }.OfType<string>())
                try { await _ex.CancelAsync(id); } catch (ExchangeException) { }   // already gone is fine
        }
        if (open.Count > 0) Push();
    }

    // ---------- emergency ----------

    /// <summary>Kill switch: auto trading off, every order cancelled, position market-closed.</summary>
    public async Task Kill()
    {
        lock (_gate) { AutoTrading = false; AutoTradingOffReason = "Kill switch used"; }
        await _ex.CancelAllAsync();
        await _ex.ClosePositionAsync();
        lock (_gate) foreach (var t in _trades.Where(t => t.State != "Closed")) { t.State = "Closed"; t.Note = "Kill switch"; }
        Report("Kill switch: orders cancelled and position closed");
        Push();
    }

    // ---------- helpers ----------

    void Close(Trade t, string note) { lock (_gate) { t.State = "Closed"; t.Note = note; } }

    void Push() => _ = _hub.Clients.All.SendAsync("trades", Trades);

    void Report(string message)
    {
        _log.LogWarning("{Message}", message);
        _ = _hub.Clients.All.SendAsync("log", new { time = DateTimeOffset.UtcNow, message });
    }

    static double RoundToTick(double price, double tick) => Math.Round(price / tick) * tick;

    sealed class Trade(string id, TradeRequest request)
    {
        public string Id { get; } = id;
        public TradeRequest Request { get; } = request;
        public string Source => Request.Source;
        public string State { get; set; } = "Pending";
        public string? Note { get; set; }
        public string? EntryOrderId { get; set; }
        public string? TpOrderId { get; set; }
        public string? SlOrderId { get; set; }
        public int FilledQty { get; set; }
        public double? AvgEntry { get; set; }
        public double? TakeProfit { get; set; }
        public double? StopLoss { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public bool CancelSent { get; set; }
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;

        public TradeView View() => new(Id, Source, Request.Side, Request.Contracts, FilledQty, AvgEntry, TakeProfit, StopLoss, State, Note, Created);
    }
}
