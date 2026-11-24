namespace BitmexBot.Core;

/// <summary>One OHLCV bar. <see cref="Time"/> is the bar's CLOSE time, as BitMEX's /trade/bucketed reports it.</summary>
public readonly record struct Candle(DateTimeOffset Time, double Open, double High, double Low, double Close, double Volume);

public static class Candles
{
    /// <summary>Heikin Ashi bars (kept so the old bot's behaviour can be compared against real closes).</summary>
    public static List<Candle> HeikinAshi(IReadOnlyList<Candle> bars)
    {
        var result = new List<Candle>(bars.Count);
        double haOpen = 0, haClose = 0;
        for (int i = 0; i < bars.Count; i++)
        {
            var c = bars[i];
            double close = (c.Open + c.High + c.Low + c.Close) / 4;
            double open = i == 0 ? (c.Open + c.Close) / 2 : (haOpen + haClose) / 2;
            result.Add(c with
            {
                Open = open,
                Close = close,
                High = Math.Max(c.High, Math.Max(open, close)),
                Low = Math.Min(c.Low, Math.Min(open, close)),
            });
            haOpen = open;
            haClose = close;
        }
        return result;
    }

    /// <summary>Merges consecutive bars into bars of <paramref name="factor"/>× the size, e.g. 5m → 15m with factor 3.
    /// Groups align to the close time so a 15m bar always closes on :00/:15/:30/:45; incomplete groups are dropped.</summary>
    public static List<Candle> Aggregate(IReadOnlyList<Candle> bars, TimeSpan sourceSize, int factor)
    {
        if (factor == 1) return bars.ToList();
        var target = sourceSize * factor;
        return bars
            .GroupBy(b => b.Time.UtcTicks - 1 - (b.Time.UtcTicks - 1) % target.Ticks)   // bar closing at the boundary belongs to the group before it
            .Where(g => g.Count() == factor)
            .Select(g => new Candle(
                g.Last().Time,
                g.First().Open,
                g.Max(b => b.High),
                g.Min(b => b.Low),
                g.Last().Close,
                g.Sum(b => b.Volume)))
            .ToList();
    }
}
