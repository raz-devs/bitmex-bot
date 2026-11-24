namespace BitmexBot.Core;

/// <summary>
/// Simulated inverse perpetual (like BTCUSD inverse contracts) for paper trading and for running the app without an exchange account.
/// Prices follow a random walk; history is synthetic. Fill rules match the backtester: limits fill only when price
/// trades through them, stops fill at the trade price that triggered them, market buys pay one tick above the last trade.
/// Not for judging strategies: use the backtester on real data for that.
/// shortcut: no partial fills, no liquidation, no funding; add them if paper results are used for more than plumbing tests.
/// </summary>
public sealed class PaperExchange : IExchange
{
    readonly Lock _gate = new();
    readonly Random _rng;
    readonly double _makerFee, _takerFee, _tick, _volPerMinute;
    readonly TimeSpan _interval;
    readonly Dictionary<string, OrderInfo> _working = [];
    IExchangeSink? _sink;
    double _price, _wallet, _realised;
    int _pos;
    double? _avg;
    long _nextId;

    public PaperExchange(double startPrice = 60000, double startBalanceBtc = 1, double makerFee = 0.0002, double takerFee = 0.00075,
        double tickSize = 0.5, double volatilityPerMinute = 0.0008, int? seed = null, TimeSpan? tradeInterval = null)
    {
        _price = startPrice;
        _wallet = startBalanceBtc;
        _makerFee = makerFee;
        _takerFee = takerFee;
        _tick = tickSize;
        _volPerMinute = volatilityPerMinute;
        _rng = seed is { } s ? new Random(s) : new Random();
        _interval = tradeInterval ?? TimeSpan.FromMilliseconds(100);
    }

    public string Name => "paper";
    public string Symbol => "BTCUSD";
    public bool Testnet => true;
    public bool CanTrade => true;
    public double Price { get { lock (_gate) return _price; } }
    public int PositionQty { get { lock (_gate) return _pos; } }
    public double WalletBtc { get { lock (_gate) return _wallet; } }

    public async Task RunAsync(IExchangeSink sink, CancellationToken ct)
    {
        lock (_gate)
        {
            _sink = sink;
            sink.OnConnected(true);
            sink.OnInstrument(_price, _price, 0.0001, _tick, 1);
            PublishAccount();
        }
        using var timer = new PeriodicTimer(_interval);
        double stepVol = _volPerMinute * Math.Sqrt(_interval.TotalMinutes);
        while (await timer.WaitForNextTickAsync(ct))
        {
            double next;
            lock (_gate) next = Round(_price * Math.Exp(Gauss() * stepVol));
            var now = DateTimeOffset.UtcNow;
            Trade(now, next, 1 + _rng.Next(500));
        }
    }

    /// <summary>Applies one market trade: matches working orders, then reports the trade. Public so tests can drive it.</summary>
    public void Trade(DateTimeOffset time, double price, double size)
    {
        lock (_gate)
        {
            _price = price;
            foreach (var o in _working.Values.ToList())
            {
                bool buy = o.Side == "Buy";
                if (o.Type == "Limit" && (buy ? price < o.Price : price > o.Price)) Fill(o, o.Price!.Value, _makerFee);
                else if (o.Type == "Stop" && (buy ? price >= o.StopPrice : price <= o.StopPrice)) Fill(o, price, _takerFee);
            }
            _sink?.OnTrade(time, price, size, DateTimeOffset.UtcNow);
            _sink?.OnQuote(price, price + _tick, 0);
            if (_pos != 0) PublishAccount();
        }
    }

    public Task<(List<Candle> Closed, Candle? Partial)> HistoryAsync(string timeframe, int bars, CancellationToken ct)
    {
        var size = Timeframes.Size(timeframe);
        double vol = _volPerMinute * Math.Sqrt(size.TotalMinutes);
        var rng = new Random(timeframe.GetHashCode());
        var now = DateTimeOffset.UtcNow;
        var lastClose = new DateTimeOffset(now.UtcTicks - now.UtcTicks % size.Ticks, TimeSpan.Zero);
        var list = new List<Candle>(bars);
        double close = Price;
        for (int i = 0; i < bars; i++)   // walk backwards so the newest bar ends at today's price
        {
            double open = close / Math.Exp(G(rng) * vol);
            double high = Math.Max(open, close) * (1 + Math.Abs(G(rng)) * vol / 2);
            double low = Math.Min(open, close) * (1 - Math.Abs(G(rng)) * vol / 2);
            list.Add(new Candle(lastClose - size * i, Round(open), Round(high), Round(low), Round(close), rng.Next(1000, 100000)));
            close = open;
        }
        list.Reverse();
        return Task.FromResult((list, (Candle?)null));
    }

    public Task<string> PlaceOrderAsync(OrderSpec spec)
    {
        lock (_gate)
        {
            if (spec.Qty <= 0) throw new ExchangeException("Quantity must be positive");
            if (spec.Type == "Limit" && spec.Price is not > 0) throw new ExchangeException("Limit order needs a price");
            if (spec.Type == "Stop" && spec.StopPrice is not > 0) throw new ExchangeException("Stop order needs a stop price");
            if (spec.Type is not ("Limit" or "Market" or "Stop")) throw new ExchangeException($"Unsupported order type {spec.Type}");

            string id = $"paper-{++_nextId}";
            var order = new OrderInfo(id, spec.ClientId, spec.Side, spec.Qty, spec.Price, spec.StopPrice, spec.Type, "New", 0, spec.ReduceOnly, DateTimeOffset.UtcNow);
            bool buy = spec.Side == "Buy";

            if (spec.ReduceOnly && (_pos == 0 || (_pos > 0) == buy)) { Finish(order, "Canceled"); return Task.FromResult(id); }
            if (spec.ReduceOnly) order = order with { Qty = Math.Min(spec.Qty, Math.Abs(_pos)) };

            double bid = _price, ask = _price + _tick;   // last trade sits on the bid; one-tick spread
            if (spec.Type == "Market") { Fill(order, buy ? ask : bid, _takerFee); return Task.FromResult(id); }
            if (spec.Type == "Limit" && spec.PostOnly && (buy ? spec.Price >= ask : spec.Price <= bid))
            {
                Finish(order, "Canceled");   // would take liquidity
                return Task.FromResult(id);
            }
            _working[id] = order;
            _sink?.OnOrder(order);
            return Task.FromResult(id);
        }
    }

    public Task AmendQtyAsync(string orderId, int qty)
    {
        lock (_gate)
        {
            if (!_working.TryGetValue(orderId, out var o)) throw new ExchangeException("Order not found");
            _working[orderId] = o = o with { Qty = qty };
            _sink?.OnOrder(o);
        }
        return Task.CompletedTask;
    }

    public Task CancelAsync(string orderId)
    {
        lock (_gate) if (_working.Remove(orderId, out var o)) Finish(o, "Canceled");
        return Task.CompletedTask;
    }

    public Task CancelAllAsync()
    {
        lock (_gate)
            foreach (var id in _working.Keys.ToList())
                if (_working.Remove(id, out var o)) Finish(o, "Canceled");
        return Task.CompletedTask;
    }

    public Task ClosePositionAsync()
    {
        OrderSpec spec;
        lock (_gate)
        {
            if (_pos == 0) return Task.CompletedTask;
            spec = new OrderSpec($"close-{_nextId + 1}", _pos > 0 ? "Sell" : "Buy", Math.Abs(_pos), "Market", ReduceOnly: true);
        }
        return PlaceOrderAsync(spec);
    }

    void Fill(OrderInfo o, double price, double feeRate)
    {
        _working.Remove(o.OrderId);
        int qty = o.Qty;
        if (o.ReduceOnly)
        {
            if (_pos == 0 || (_pos > 0) == (o.Side == "Buy")) { Finish(o, "Canceled"); return; }
            qty = Math.Min(qty, Math.Abs(_pos));
        }
        int delta = o.Side == "Buy" ? qty : -qty;

        if (_pos == 0 || Math.Sign(_pos) == Math.Sign(delta))
        {
            // Inverse contracts: average entry is the harmonic mean weighted by contracts.
            int total = Math.Abs(_pos) + qty;
            _avg = total / ((_avg is { } a ? Math.Abs(_pos) / a : 0) + qty / price);
            _pos += delta;
        }
        else
        {
            int closing = Math.Min(qty, Math.Abs(_pos));
            double pnl = Math.Sign(_pos) * closing * (1 / _avg!.Value - 1 / price);
            _realised += pnl;
            _wallet += pnl;
            _pos += delta;
            if (_pos == 0) _avg = null;
            else if (Math.Sign(_pos) == Math.Sign(delta)) _avg = price;   // flipped: the remainder opened a new position here
        }
        _wallet -= qty / price * feeRate;

        var filled = o with { Qty = qty, CumQty = qty, Status = "Filled" };
        _sink?.OnExecution(new ExecutionInfo(o.OrderId, o.ClientId, "Filled", qty, price));
        _sink?.OnOrder(filled);
        PublishAccount();
    }

    void Finish(OrderInfo o, string status) => _sink?.OnOrder(o with { Status = status });

    void PublishAccount()
    {
        double unrealised = _pos == 0 || _avg is null ? 0 : _pos * (1 / _avg.Value - 1 / _price);
        _sink?.OnPosition(new PositionInfo(_pos, _avg, null, unrealised, _realised));
        _sink?.OnMargin(new MarginInfo(_wallet, _wallet + unrealised, _wallet + unrealised));
    }

    double Round(double p) => Math.Round(p / _tick) * _tick;
    double Gauss() => G(_rng);
    static double G(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
}
