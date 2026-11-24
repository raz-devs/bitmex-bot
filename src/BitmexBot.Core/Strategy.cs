namespace BitmexBot.Core;

public enum Signal { None, Long, Short }

/// <summary>Fed one closed bar at a time, in order. Live code and the backtester use the same instances.</summary>
public interface IStrategy
{
    Signal OnBar(Candle bar);
}

/// <summary>
/// The old bot's "Strat2", with its bugs fixed:
/// long when EMA(LongEma) crosses above SMA(LongSma), short when EMA(ShortEma) crosses below SMA(ShortSma).
/// The old code only fired when <c>(int)|EMA - MA| == 0</c> at the moment a 3-second timer sampled, which missed
/// most crosses, and its short trigger checked the long direction. Here a cross is a sign change between two closed bars.
/// Defaults are the old UI's defaults (EMA 70 / MA 80 for longs, EMA 100 / MA 80 for shorts).
/// </summary>
public sealed class EmaSmaCross(int longEma = 70, int longSma = 80, int shortEma = 100, int shortSma = 80) : IStrategy
{
    readonly Ema _longEma = new(longEma), _shortEma = new(shortEma);
    readonly Sma _longSma = new(longSma), _shortSma = new(shortSma);
    double _pLe = double.NaN, _pLs = double.NaN, _pSe = double.NaN, _pSs = double.NaN;

    public Signal OnBar(Candle bar)
    {
        double le = _longEma.Next(bar.Close), ls = _longSma.Next(bar.Close);
        double se = _shortEma.Next(bar.Close), ss = _shortSma.Next(bar.Close);

        // NaN comparisons are false, so no signal fires until both bars have full indicator values.
        var signal = Cross.Above(_pLe, _pLs, le, ls) ? Signal.Long
                   : Cross.Below(_pSe, _pSs, se, ss) ? Signal.Short
                   : Signal.None;

        (_pLe, _pLs, _pSe, _pSs) = (le, ls, se, ss);
        return signal;
    }
}
