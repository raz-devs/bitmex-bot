namespace BitmexBot.Core;

/// <summary>
/// Builds candles of one size from individual trades, so the forming bar is known on every trade instead of
/// waiting for BitMEX's tradeBin message after the bar closes. A bar covers [close − size, close).
/// </summary>
public sealed class CandleBuilder(TimeSpan size)
{
    public TimeSpan Size { get; } = size;

    /// <summary>The bar still forming, or null before the first trade.</summary>
    public Candle? Current { get; private set; }

    public DateTimeOffset CloseTimeFor(DateTimeOffset t) =>
        new(t.UtcTicks - t.UtcTicks % Size.Ticks + Size.Ticks, TimeSpan.Zero);

    /// <summary>Starts from a partial bar fetched over REST (its Time must be its close time).</summary>
    public void Seed(Candle partial) => Current = partial;

    /// <summary>Applies one trade. Returns the bar that just closed if this trade belongs to a later bar.</summary>
    public Candle? Add(DateTimeOffset time, double price, double volume)
    {
        var closeTime = CloseTimeFor(time);
        if (Current is { } cur && closeTime <= cur.Time)
        {
            // Trades that arrive slightly late for the current bar still count toward it.
            Current = cur with { High = Math.Max(cur.High, price), Low = Math.Min(cur.Low, price), Close = price, Volume = cur.Volume + volume };
            return null;
        }
        var closed = Current;
        Current = new Candle(closeTime, price, price, price, price, volume);
        return closed;
    }

    /// <summary>Closes the current bar once the clock passes its close time, so on-close rules don't wait for the next trade.
    /// The next bar then starts flat at the last price.</summary>
    public Candle? Tick(DateTimeOffset now)
    {
        if (Current is not { } cur || now < cur.Time) return null;
        var next = CloseTimeFor(now);
        Current = new Candle(next, cur.Close, cur.Close, cur.Close, cur.Close, 0);
        return cur;
    }
}
