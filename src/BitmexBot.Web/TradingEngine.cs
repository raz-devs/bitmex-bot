using System.Diagnostics;
using System.Threading.Channels;
using BitmexBot.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace BitmexBot.Web;

/// <summary>Bar for the chart: <see cref="Time"/> is the bar's OPEN time in unix seconds, as Lightweight Charts expects.</summary>
public sealed record CandleDto(long Time, double Open, double High, double Low, double Close, double Volume)
{
    public static CandleDto From(Candle c, TimeSpan size) => new((c.Time - size).ToUnixTimeSeconds(), c.Open, c.High, c.Low, c.Close, c.Volume);
}

public sealed record SignalEvent(DateTimeOffset Time, string StrategyId, string Strategy, string Timeframe, string Side,
    double Price, bool OnClose, double LatencyMs, string Action);

public sealed record MarketView(double? LastPrice, double? BestBid, double? BestAsk, double TickSize, int LotSize, int PositionQty);

public sealed record StatusInfo(string Exchange, string Symbol, bool Connected, bool Testnet, bool CanTrade, bool AutoTrading,
    string? AutoTradingOffReason, double? LastPrice, double? MarkPrice, double? BestBid, double? BestAsk, double? BookImbalance,
    double? FundingRate, double FeedLagMs, double EvalMicros, long Trades);

/// <summary>
/// Receives normalized exchange events and processes them one at a time on a single worker, so nothing slow ever runs
/// on the exchange's receive loop. Builds candles for every watched timeframe from trades, runs strategies (on close,
/// or on every trade for "tick" strategies), keeps account state and pushes changes to the browser.
/// </summary>
public sealed class TradingEngine : BackgroundService, IExchangeSink
{
    const int MaxBars = 5000;

    readonly IExchange _ex;
    readonly StrategyStore _store;
    readonly IHubContext<TradingHub> _hub;
    readonly ILogger<TradingEngine> _log;
    readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>(new() { SingleReader = true });
    readonly Lock _gate = new();

    readonly Dictionary<string, TfState> _tfs = [];
    readonly List<Runner> _runners = [];
    readonly Dictionary<string, OrderInfo> _orders = [];
    readonly LinkedList<SignalEvent> _signals = new();

    bool _connected;
    double? _last, _mark, _bid, _ask, _imbalance, _funding;
    double _tick = 0.5;
    int _lot = 1;
    PositionInfo _position = new(0, null, null, 0, 0);
    MarginInfo? _margin;
    double _feedLagMs, _evalMicros;
    long _tradeCount;
    DateTimeOffset _lastStatusPush;
    volatile MarketView _market = new(null, null, null, 0.5, 1, 0);

    public OrderManager Orders { get; }

    public TradingEngine(IExchange exchange, IOptions<RiskOptions> risk, StrategyStore store, IHubContext<TradingHub> hub, ILoggerFactory logs)
    {
        _ex = exchange;
        _store = store;
        _hub = hub;
        _log = logs.CreateLogger<TradingEngine>();
        Orders = new OrderManager(this, exchange, risk.Value, hub, logs.CreateLogger<OrderManager>());
        _store.Changed += OnStrategyChanged;
    }

    /// <summary>Lock-free snapshot for order sizing and pricing.</summary>
    public MarketView Market => _market;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        foreach (var tf in _store.All.Where(s => s.Enabled).Select(s => s.Timeframe).Distinct()) _ = EnsureTimeframe(tf);
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            while (await timer.WaitForNextTickAsync(stop)) _queue.Writer.TryWrite(() => OnTimer(DateTimeOffset.UtcNow));
        }, stop);

        await foreach (var work in _queue.Reader.ReadAllAsync(stop))
        {
            try { lock (_gate) work(); }
            catch (Exception ex) { _log.LogError(ex, "Engine error"); }
        }
    }

    // ---------- IExchangeSink: called from the exchange's threads, so only enqueue ----------

    public void OnConnected(bool connected) => _queue.Writer.TryWrite(() =>
    {
        _connected = connected;
        PushStatus();
    });

    public void OnTrade(DateTimeOffset time, double price, double size, DateTimeOffset received) => _queue.Writer.TryWrite(() =>
    {
        var sw = Stopwatch.StartNew();
        _last = price;
        _tradeCount++;
        foreach (var s in _tfs.Values)
        {
            if (s.Builder.Add(time, price, size) is { } closed) OnBarClosed(s, closed);
            s.Dirty = true;
        }
        _feedLagMs = _feedLagMs * 0.95 + (received - time).TotalMilliseconds * 0.05;
        foreach (var r in _runners.Where(r => r.Def.Evaluate == "tick"))
            if (_tfs[r.Def.Timeframe].Builder.Current is { } cur) Fire(r, r.Rule.Evaluate(cur, commit: false), cur, onClose: false, time);
        _evalMicros = _evalMicros * 0.95 + sw.Elapsed.TotalMicroseconds * 0.05;
        UpdateMarket();
    });

    public void OnQuote(double bid, double ask, double imbalance) => _queue.Writer.TryWrite(() =>
    {
        (_bid, _ask, _imbalance) = (bid, ask, imbalance);
        UpdateMarket();
    });

    public void OnInstrument(double? last, double? mark, double? funding, double? tickSize, int? lotSize) => _queue.Writer.TryWrite(() =>
    {
        _last = last ?? _last;
        _mark = mark ?? _mark;
        _funding = funding ?? _funding;
        _tick = tickSize ?? _tick;
        _lot = lotSize ?? _lot;
        UpdateMarket();
    });

    public void OnOrder(OrderInfo order) => _queue.Writer.TryWrite(() =>
    {
        if (order.Status is "Filled" or "Canceled" or "Rejected") _orders.Remove(order.OrderId);
        else _orders[order.OrderId] = order;
        Orders.OnOrder(order);
        _ = _hub.Clients.All.SendAsync("orders", WorkingOrders());
    });

    public void OnExecution(ExecutionInfo exec) => _queue.Writer.TryWrite(() => Orders.OnExecution(exec));

    public void OnPosition(PositionInfo position) => _queue.Writer.TryWrite(() =>
    {
        bool changed = position.Qty != _position.Qty;
        _position = position;
        UpdateMarket();
        if (changed) Orders.OnPosition(position.Qty);
        _ = _hub.Clients.All.SendAsync("position", _position);
    });

    public void OnMargin(MarginInfo margin) => _queue.Writer.TryWrite(() =>
    {
        _margin = margin;
        Orders.OnMargin(margin.EquityBtc);
        _ = _hub.Clients.All.SendAsync("margin", margin);
    });

    // ---------- processing (runs on the worker, under _gate) ----------

    void OnBarClosed(TfState s, Candle bar)
    {
        s.Closed.Add(bar);
        if (s.Closed.Count > MaxBars + 500) s.Closed.RemoveRange(0, 500);
        _ = _hub.Clients.Group(TradingHub.Group(s.Tf)).SendAsync("candle", s.Tf, CandleDto.From(bar, s.Builder.Size));
        foreach (var r in _runners.Where(r => r.Def.Timeframe == s.Tf)) Fire(r, r.Rule.OnBar(bar), bar, onClose: true, bar.Time);
    }

    void OnTimer(DateTimeOffset now)
    {
        foreach (var s in _tfs.Values)
        {
            if (s.Builder.Tick(now) is { } closed) { OnBarClosed(s, closed); s.Dirty = true; }
            if (s.Dirty && s.Builder.Current is { } cur)
            {
                s.Dirty = false;   // the chart gets at most 5 updates a second per timeframe; signals are not throttled
                _ = _hub.Clients.Group(TradingHub.Group(s.Tf)).SendAsync("candle", s.Tf, CandleDto.From(cur, s.Builder.Size));
            }
        }
        Orders.OnTimer(now);
        if (now - _lastStatusPush > TimeSpan.FromSeconds(1)) PushStatus();
    }

    void Fire(Runner r, Signal signal, Candle bar, bool onClose, DateTimeOffset dataTime)
    {
        if (signal == Signal.None) return;
        if (r.LastBar == bar.Time && r.LastSignal == signal) return;   // tick mode: once per bar per side
        r.LastBar = bar.Time;
        r.LastSignal = signal;

        string action = r.Def.AutoTrade ? Orders.TrySubmitForStrategy(r.Def, signal) : "alert";
        var now = DateTimeOffset.UtcNow;
        var ev = new SignalEvent(now, r.Def.Id, r.Def.Name, r.Def.Timeframe, signal.ToString(),
            onClose ? bar.Close : _last ?? bar.Close, onClose, Math.Max(0, (now - dataTime).TotalMilliseconds), action);
        _signals.AddFirst(ev);
        if (_signals.Count > 200) _signals.RemoveLast();
        _log.LogInformation("Signal {Side} from {Strategy} at {Price}: {Action}", ev.Side, ev.Strategy, ev.Price, action);
        _ = _hub.Clients.All.SendAsync("signal", ev);
    }

    void UpdateMarket() => _market = new(_last, _bid, _ask, _tick, _lot, _position.Qty);

    void PushStatus()
    {
        _lastStatusPush = DateTimeOffset.UtcNow;
        _ = _hub.Clients.All.SendAsync("status", BuildStatus());
    }

    StatusInfo BuildStatus() => new(_ex.Name, _ex.Symbol, _connected, _ex.Testnet, _ex.CanTrade, Orders.AutoTrading, Orders.AutoTradingOffReason,
        _last, _mark, _bid, _ask, _imbalance, _funding, Math.Round(_feedLagMs, 1), Math.Round(_evalMicros, 1), _tradeCount);

    List<OrderInfo> WorkingOrders() => _orders.Values.OrderByDescending(x => x.Time).ToList();

    // ---------- strategies ----------

    void OnStrategyChanged(StrategyDefinition? def, string id)
    {
        _queue.Writer.TryWrite(() => _runners.RemoveAll(r => r.Def.Id == id));
        if (def is not { Enabled: true }) return;
        _ = EnsureTimeframe(def.Timeframe).ContinueWith(_ => _queue.Writer.TryWrite(() => AddRunner(def)));
    }

    void AddRunner(StrategyDefinition def)
    {
        _runners.RemoveAll(r => r.Def.Id == def.Id);
        var r = new Runner(def, new RuleStrategy(def));
        foreach (var bar in _tfs[def.Timeframe].Closed) r.Rule.OnBar(bar);   // warm up indicators; signals from history are ignored
        _runners.Add(r);
    }

    // ---------- timeframes ----------

    /// <summary>Starts building <paramref name="tf"/> from live trades and loads its history once. Safe to call repeatedly.</summary>
    public Task EnsureTimeframe(string tf)
    {
        var size = Timeframes.Size(tf);   // throws on unknown timeframes before anything is created
        lock (_gate)
        {
            if (!_tfs.TryGetValue(tf, out var s)) _tfs[tf] = s = new TfState(tf, new CandleBuilder(size));
            if (s.Seeding is { IsFaulted: false }) return s.Seeding;
            return s.Seeding = Task.Run(() => Seed(s));
        }
    }

    async Task Seed(TfState s)
    {
        var (history, partial) = await _ex.HistoryAsync(s.Tf, 1000, CancellationToken.None);
        var done = new TaskCompletionSource();
        _queue.Writer.TryWrite(() =>
        {
            var firstLive = s.Closed.Count > 0 ? s.Closed[0].Time : s.Builder.Current?.Time ?? DateTimeOffset.MaxValue;
            s.Closed.InsertRange(0, history.Where(b => b.Time < firstLive));
            if (partial is { } p)
            {
                if (s.Builder.Current is not { } cur) s.Builder.Seed(p);
                else if (cur.Time == p.Time) s.Builder.Seed(cur with { Open = p.Open, High = Math.Max(cur.High, p.High), Low = Math.Min(cur.Low, p.Low) });
            }
            foreach (var def in _store.All.Where(d => d.Enabled && d.Timeframe == s.Tf)) AddRunner(def);
            _log.LogInformation("Loaded {Count} {Tf} bars", s.Closed.Count, s.Tf);
            done.SetResult();
        });
        await done.Task;
    }

    // ---------- snapshots for the API ----------

    public async Task<List<CandleDto>> GetCandles(string tf)
    {
        try { await EnsureTimeframe(tf); }
        catch (Exception ex) { _log.LogWarning("History for {Tf} failed: {Message}", tf, ex.Message); }
        lock (_gate)
        {
            var s = _tfs[tf];
            var bars = s.Closed.TakeLast(MaxBars).Select(c => CandleDto.From(c, s.Builder.Size)).ToList();
            if (s.Builder.Current is { } cur && (bars.Count == 0 || CandleDto.From(cur, s.Builder.Size).Time > bars[^1].Time))
                bars.Add(CandleDto.From(cur, s.Builder.Size));
            return bars;
        }
    }

    public object State()
    {
        lock (_gate)
            return new { status = BuildStatus(), position = _position, margin = _margin, orders = WorkingOrders(), signals = _signals.ToList(), trades = Orders.Trades };
    }

    sealed class TfState(string tf, CandleBuilder builder)
    {
        public string Tf { get; } = tf;
        public CandleBuilder Builder { get; } = builder;
        public List<Candle> Closed { get; } = [];
        public Task? Seeding { get; set; }
        public bool Dirty { get; set; }
    }

    sealed class Runner(StrategyDefinition def, RuleStrategy rule)
    {
        public StrategyDefinition Def { get; } = def;
        public RuleStrategy Rule { get; } = rule;
        public DateTimeOffset LastBar { get; set; }
        public Signal LastSignal { get; set; }
    }
}
