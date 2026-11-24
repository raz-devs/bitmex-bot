namespace BitmexBot.Core;

/// <summary>
/// One side of a comparison. <c>Ind</c> is one of: price, ema, sma, rsi, macd, bb, value.
/// <c>Source</c> picks the candle field (open, high, low, close, volume). <c>Output</c> picks a line for
/// multi-line indicators: macd → line|signal|hist, bb → upper|middle|lower.
/// </summary>
public sealed record Operand
{
    public string Ind { get; init; } = "price";
    public int Period { get; init; } = 14;
    public string Source { get; init; } = "close";
    public string? Output { get; init; }
    public double Value { get; init; }
    public double Mult { get; init; } = 2;
    public int Fast { get; init; } = 12;
    public int Slow { get; init; } = 26;
    public int Signal { get; init; } = 9;

    public override string ToString() => Ind switch
    {
        "value" => Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "price" => Source,
        "macd" => $"MACD({Fast},{Slow},{Signal}).{Output ?? "line"}",
        "bb" => $"BB({Period},{Mult}).{Output ?? "middle"}",
        _ => $"{Ind.ToUpperInvariant()}({Period}{(Source == "close" ? "" : "," + Source)})",
    };
}

/// <summary>Op: crossesAbove, crossesBelow, gt, lt.</summary>
public sealed record Comparison(Operand Left, string Op, Operand Right)
{
    public override string ToString() => $"{Left} {Op} {Right}";
}

/// <summary>Mode "all" = every rule must hold (AND); "any" = at least one (OR).</summary>
public sealed record ConditionGroup(string Mode, List<Comparison> Rules);

public sealed record OrderPlan
{
    public EntryOrderType Type { get; init; } = EntryOrderType.Limit;
    /// <summary>XBTUSD contracts. BitMEX requires a multiple of the instrument's lot size (100 at the time of writing).</summary>
    public int Contracts { get; init; } = 100;
    public int OffsetTicks { get; init; } = 0;
    public bool PostOnly { get; init; } = true;
    public int TimeoutBars { get; init; } = 2;
    public double TakeProfitPct { get; init; } = 1.0;
    public double StopLossPct { get; init; } = 0.5;
}

public sealed record StrategyDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = "New strategy";
    public string Timeframe { get; init; } = "15m";
    /// <summary>"close" evaluates once per closed bar; "tick" also evaluates the forming bar on every trade.</summary>
    public string Evaluate { get; init; } = "close";
    public ConditionGroup? Long { get; init; }
    public ConditionGroup? Short { get; init; }
    public OrderPlan Order { get; init; } = new();
    public bool Enabled { get; init; } = true;
    /// <summary>When false the strategy only raises alerts. Auto trading must also be switched on globally.</summary>
    public bool AutoTrade { get; init; }

    public BacktestSettings ToBacktestSettings(double makerFee, double takerFee) => new()
    {
        Contracts = Order.Contracts,
        EntryOrder = Order.Type,
        LimitOffsetTicks = Order.OffsetTicks,
        EntryTimeoutBars = Order.TimeoutBars,
        TakeProfitPct = Order.TakeProfitPct,
        StopLossPct = Order.StopLossPct,
        MakerFee = makerFee,
        TakerFee = takerFee,
    };
}

/// <summary>
/// Runs a <see cref="StrategyDefinition"/>. <see cref="OnBar"/> commits a closed bar (used by the backtester and live on close);
/// <see cref="Evaluate"/> with commit=false checks the forming bar without changing indicator state (live "tick" mode).
/// Crosses compare the last closed bar's values with the current ones.
/// </summary>
public sealed class RuleStrategy : IStrategy
{
    readonly StrategyDefinition _def;
    readonly List<(Comparison Rule, Node Left, Node Right)> _long, _short;

    public RuleStrategy(StrategyDefinition def)
    {
        _def = def;
        _long = Compile(def.Long);
        _short = Compile(def.Short);
    }

    public Signal OnBar(Candle bar) => Evaluate(bar, commit: true);

    public Signal Evaluate(Candle bar, bool commit)
    {
        foreach (var (_, l, r) in _long.Concat(_short)) { l.Compute(bar, commit); r.Compute(bar, commit); }
        var signal = Holds(_def.Long, _long) ? Signal.Long : Holds(_def.Short, _short) ? Signal.Short : Signal.None;
        if (commit) foreach (var (_, l, r) in _long.Concat(_short)) { l.Commit(); r.Commit(); }
        return signal;
    }

    static bool Holds(ConditionGroup? group, List<(Comparison Rule, Node Left, Node Right)> rules)
    {
        if (group is null || rules.Count == 0) return false;
        var results = rules.Select(x => Test(x.Rule.Op, x.Left, x.Right));
        return group.Mode == "any" ? results.Any(b => b) : results.All(b => b);
    }

    static bool Test(string op, Node l, Node r) => op switch
    {
        "crossesAbove" => Cross.Above(l.Prev, r.Prev, l.Cur, r.Cur),
        "crossesBelow" => Cross.Below(l.Prev, r.Prev, l.Cur, r.Cur),
        "gt" => l.Cur > r.Cur,
        "lt" => l.Cur < r.Cur,
        _ => throw new ArgumentException($"Unknown operator '{op}'"),
    };

    static List<(Comparison, Node, Node)> Compile(ConditionGroup? g) =>
        g?.Rules.Select(c => (c, Node.Create(c.Left), Node.Create(c.Right))).ToList() ?? [];

    /// <summary>One operand's live state. Each comparison gets its own nodes, so indicators are never shared or double-fed.</summary>
    sealed class Node
    {
        readonly Func<Candle, bool, double> _calc;
        public double Prev { get; private set; } = double.NaN;
        public double Cur { get; private set; } = double.NaN;

        Node(Func<Candle, bool, double> calc) => _calc = calc;

        public void Compute(Candle bar, bool commit) => Cur = _calc(bar, commit);
        public void Commit() => Prev = Cur;

        public static Node Create(Operand o)
        {
            Func<Candle, double> src = o.Source switch
            {
                "open" => c => c.Open,
                "high" => c => c.High,
                "low" => c => c.Low,
                "volume" => c => c.Volume,
                "close" => c => c.Close,
                _ => throw new ArgumentException($"Unknown source '{o.Source}'"),
            };
            if (o.Period < 1 && o.Ind is "ema" or "sma" or "rsi" or "bb") throw new ArgumentException($"{o.Ind} needs a period of at least 1");
            switch (o.Ind)
            {
                case "value": return new((_, _) => o.Value);
                case "price": return new((c, _) => src(c));
                case "ema": { var i = new Ema(o.Period); return new((c, commit) => commit ? i.Next(src(c)) : i.Peek(src(c))); }
                case "sma": { var i = new Sma(o.Period); return new((c, commit) => commit ? i.Next(src(c)) : i.Peek(src(c))); }
                case "rsi": { var i = new Rsi(o.Period); return new((c, commit) => commit ? i.Next(src(c)) : i.Peek(src(c))); }
                case "macd":
                {
                    var i = new Macd(o.Fast, o.Slow, o.Signal);
                    return new((c, commit) =>
                    {
                        var v = commit ? i.Next(src(c)) : i.Peek(src(c));
                        return o.Output switch { "signal" => v.Signal, "hist" => v.Histogram, _ => v.Line };
                    });
                }
                case "bb":
                {
                    var i = new Bollinger(o.Period, o.Mult);
                    return new((c, commit) =>
                    {
                        var v = commit ? i.Next(src(c)) : i.Peek(src(c));
                        return o.Output switch { "upper" => v.Upper, "lower" => v.Lower, _ => v.Middle };
                    });
                }
                default: throw new ArgumentException($"Unknown indicator '{o.Ind}'");
            }
        }
    }
}

public static class Timeframes
{
    public static readonly string[] All = ["1m", "5m", "15m", "30m", "1h", "4h", "1d"];

    public static TimeSpan Size(string tf) => tf switch
    {
        "1m" => TimeSpan.FromMinutes(1), "5m" => TimeSpan.FromMinutes(5), "15m" => TimeSpan.FromMinutes(15),
        "30m" => TimeSpan.FromMinutes(30), "1h" => TimeSpan.FromHours(1), "4h" => TimeSpan.FromHours(4),
        "1d" => TimeSpan.FromDays(1),
        _ => throw new ArgumentException($"Unsupported timeframe {tf}"),
    };

    /// <summary>The BitMEX bin to download and how many of them make one bar of <paramref name="tf"/>.</summary>
    public static (string Bin, int Factor) Source(string tf) => tf switch
    {
        "1m" or "5m" or "1h" or "1d" => (tf, 1),
        "15m" => ("5m", 3),
        "30m" => ("5m", 6),
        "4h" => ("1h", 4),
        _ => throw new ArgumentException($"Unsupported timeframe {tf}"),
    };
}
