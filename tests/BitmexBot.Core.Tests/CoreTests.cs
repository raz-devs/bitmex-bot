using BitmexBot.Core;

namespace BitmexBot.Core.Tests;

public class IndicatorTests
{
    static readonly double[] Prices = [44.34, 44.09, 44.15, 43.61, 44.33, 44.83, 45.10, 45.42, 45.84, 46.08, 45.89, 46.03, 45.61, 46.28, 46.28, 46.00, 46.03, 46.41, 46.22, 45.64];

    [Fact]
    public void Sma_matches_plain_average()
    {
        var sma = new Sma(3);
        Assert.True(double.IsNaN(sma.Next(1)));
        Assert.True(double.IsNaN(sma.Next(2)));
        Assert.Equal(2, sma.Next(3), 10);
        Assert.Equal(3, sma.Next(4), 10);
    }

    [Fact]
    public void Ema_seeds_with_sma_then_smooths()
    {
        var ema = new Ema(3);
        ema.Next(1); ema.Next(2);
        Assert.Equal(2, ema.Next(3), 10);           // seed = SMA(1,2,3)
        Assert.Equal(3, ema.Next(4), 10);           // (4-2)*0.5 + 2
    }

    [Fact]
    public void Rsi_matches_reference_wilder_calculation()
    {
        const int period = 14;
        var rsi = new Rsi(period);
        var values = Prices.Select(rsi.Next).ToArray();

        // Reference: textbook Wilder RSI computed in one pass over the full series.
        double avgGain = 0, avgLoss = 0;
        for (int i = 1; i <= period; i++)
        {
            double ch = Prices[i] - Prices[i - 1];
            avgGain += Math.Max(ch, 0); avgLoss += Math.Max(-ch, 0);
        }
        avgGain /= period; avgLoss /= period;
        Assert.True(double.IsNaN(values[period - 1]));
        Assert.Equal(100 - 100 / (1 + avgGain / avgLoss), values[period], 8);
        for (int i = period + 1; i < Prices.Length; i++)
        {
            double ch = Prices[i] - Prices[i - 1];
            avgGain = (avgGain * (period - 1) + Math.Max(ch, 0)) / period;
            avgLoss = (avgLoss * (period - 1) + Math.Max(-ch, 0)) / period;
            Assert.Equal(100 - 100 / (1 + avgGain / avgLoss), values[i], 8);
        }
    }

    [Fact]
    public void Peek_equals_the_value_next_will_commit()
    {
        var (sma, ema, rsi) = (new Sma(5), new Ema(5), new Rsi(5));
        foreach (var p in Prices)
        {
            double ps = sma.Peek(p), pe = ema.Peek(p), pr = rsi.Peek(p);
            Assert.Equal(ps, sma.Next(p), 10);
            Assert.Equal(pe, ema.Next(p), 10);
            Assert.Equal(pr, rsi.Next(p), 10);
        }
    }
}

public class StrategyTests
{
    static Candle Bar(int i, double close) =>
        new(DateTimeOffset.UnixEpoch.AddMinutes(5 * (i + 1)), close, close + 1, close - 1, close, 1);

    [Fact]
    public void Ema_sma_cross_fires_once_per_cross_in_each_direction()
    {
        var strat = new EmaSmaCross(3, 5, 3, 5);
        // Falling, then rising, then falling again.
        double[] closes = [.. Enumerable.Range(0, 10).Select(i => 100.0 - i), .. Enumerable.Range(0, 10).Select(i => 91.0 + 2 * i), .. Enumerable.Range(0, 10).Select(i => 109.0 - 2 * i)];
        var signals = closes.Select((c, i) => strat.OnBar(Bar(i, c))).ToList();

        Assert.Equal(1, signals.Count(s => s == Signal.Long));
        Assert.Equal(1, signals.Count(s => s == Signal.Short));
        Assert.True(signals.IndexOf(Signal.Long) is > 9 and < 20);
        Assert.True(signals.IndexOf(Signal.Short) >= 20);
    }
}

public class BacktesterTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    static Candle Bar(int i, double o, double h, double l, double c) => new(T0.AddMinutes(i), o, h, l, c, 1);

    /// <summary>Emits the scripted signals bar by bar.</summary>
    sealed class Scripted(params Signal[] script) : IStrategy
    {
        int _i;
        public Signal OnBar(Candle bar) => _i < script.Length ? script[_i++] : Signal.None;
    }

    static readonly BacktestSettings NoFees = new() { MakerFee = 0, TakerFee = 0, SlippageTicks = 0, Contracts = 10000, TakeProfitPct = 1, StopLossPct = 1, EntryTimeoutBars = 2 };

    [Fact]
    public void Limit_touch_does_not_fill_and_times_out_as_missed()
    {
        var bars = new[] { Bar(0, 100, 100, 100, 100), Bar(1, 100, 101, 100, 100.5), Bar(2, 101, 102, 100, 101), Bar(3, 101, 101, 101, 101) };
        var r = Backtester.Run(bars, new Scripted(Signal.Long), NoFees);
        Assert.Empty(r.Trades);
        Assert.Equal(1, r.MissedEntries);
    }

    [Fact]
    public void Long_limit_fills_through_price_and_takes_profit_with_inverse_pnl()
    {
        var bars = new[]
        {
            Bar(0, 100, 100, 100, 100),     // signal: limit buy at 100
            Bar(1, 100, 100, 99.5, 99.8),   // trades through 100 -> filled
            Bar(2, 100, 101.5, 99.9, 101),  // TP at 101 traded through
        };
        var r = Backtester.Run(bars, new Scripted(Signal.Long), NoFees);
        var t = Assert.Single(r.Trades);
        Assert.Equal("TakeProfit", t.ExitReason);
        Assert.Equal(100, t.EntryPrice, 10);
        Assert.Equal(101, t.ExitPrice, 10);
        Assert.Equal(10000 * (1 / 100.0 - 1 / 101.0), t.PnlXbt, 12);
    }

    [Fact]
    public void Stop_wins_when_tp_and_sl_are_both_inside_one_bar()
    {
        var bars = new[] { Bar(0, 100, 100, 100, 100), Bar(1, 100, 100, 99.5, 100), Bar(2, 100, 102, 98, 100) };
        var t = Assert.Single(Backtester.Run(bars, new Scripted(Signal.Long), NoFees).Trades);
        Assert.Equal("StopLoss", t.ExitReason);
        Assert.Equal(99, t.ExitPrice, 10);
        Assert.True(t.PnlXbt < 0);
    }

    [Fact]
    public void Short_market_entry_fills_next_open_with_slippage_and_charges_taker_fees()
    {
        var s = NoFees with { EntryOrder = EntryOrderType.Market, SlippageTicks = 2, TakerFee = 0.00075 };
        var bars = new[] { Bar(0, 100, 100, 100, 100), Bar(1, 200, 200, 200, 200), Bar(2, 200, 200, 196, 197) };
        var t = Assert.Single(Backtester.Run(bars, new Scripted(Signal.Short), s).Trades);
        Assert.Equal(199, t.EntryPrice, 10);                  // 200 open - 2 ticks of 0.5
        Assert.Equal("TakeProfit", t.ExitReason);             // TP = 199 * 0.99 = 197.01, bar 2 low 196 trades through
        Assert.Equal(197.01, t.ExitPrice, 10);
        double entryFee = 10000 / 199.0 * 0.00075;           // taker on entry, maker (0 here) on TP
        Assert.Equal(entryFee, t.FeesXbt, 12);
        Assert.Equal(10000 * (1 / 197.01 - 1 / 199.0) - entryFee, t.PnlXbt, 12);
    }

    [Fact]
    public void Stop_that_gaps_fills_at_the_open()
    {
        var bars = new[] { Bar(0, 100, 100, 100, 100), Bar(1, 100, 100, 99.5, 100), Bar(2, 95, 96, 94, 95) };
        var t = Assert.Single(Backtester.Run(bars, new Scripted(Signal.Long), NoFees).Trades);
        Assert.Equal(95, t.ExitPrice, 10);
    }
}

public class DataTests
{
    [Fact]
    public void Aggregates_5m_into_15m_aligned_to_close_time()
    {
        var t = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(1, 7).Select(i => new Candle(t.AddMinutes(5 * i), i, i + 1, i - 1, i + 0.5, 10)).ToList();
        var agg = Candles.Aggregate(bars, TimeSpan.FromMinutes(5), 3);

        Assert.Equal(2, agg.Count);                           // 00:05-00:15 and 00:20-00:30; 00:35 alone is dropped
        Assert.Equal(t.AddMinutes(15), agg[0].Time);
        Assert.Equal(1, agg[0].Open);
        Assert.Equal(4, agg[0].High);
        Assert.Equal(0, agg[0].Low);
        Assert.Equal(3.5, agg[0].Close);
        Assert.Equal(30, agg[0].Volume);
    }

    [Fact]
    public void Parses_bitmex_json_and_skips_empty_bins()
    {
        const string json = """
            [{"timestamp":"2024-01-01T00:05:00.000Z","symbol":"XBTUSD","open":42000.5,"high":42100,"low":41900,"close":42050,"volume":123},
             {"timestamp":"2024-01-01T00:10:00.000Z","symbol":"XBTUSD","open":null,"high":null,"low":null,"close":null,"volume":0}]
            """;
        var c = Assert.Single(BitmexCandles.Parse(json));
        Assert.Equal(42000.5, c.Open);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 5, 0, TimeSpan.Zero), c.Time);
    }

    [Fact]
    public void Csv_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"candles-{Guid.NewGuid()}.csv");
        var bars = new[] { new Candle(new DateTimeOffset(2024, 1, 1, 0, 5, 0, TimeSpan.Zero), 1.5, 2.25, 1, 2, 100) };
        BitmexCandles.WriteCsv(path, bars);
        Assert.Equal(bars, BitmexCandles.ReadCsv(path));
        File.Delete(path);
    }
}
