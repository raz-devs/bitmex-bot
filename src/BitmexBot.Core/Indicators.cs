namespace BitmexBot.Core;

/// <summary>
/// Streaming indicators: O(1) per bar. <c>Next</c> commits a closed bar; <c>Peek</c> computes the value
/// for a still-forming bar without changing state, so live code can evaluate on every trade.
/// Values are NaN until the indicator has enough bars.
/// </summary>
public sealed class Sma(int period)
{
    readonly Queue<double> _window = new(period);
    double _sum;

    public double Value { get; private set; } = double.NaN;

    public double Next(double x)
    {
        _window.Enqueue(x);
        _sum += x;
        if (_window.Count > period) _sum -= _window.Dequeue();
        return Value = _window.Count == period ? _sum / period : double.NaN;
    }

    public double Peek(double x)
    {
        if (_window.Count < period - 1) return double.NaN;
        double drop = _window.Count == period ? _window.Peek() : 0;
        return (_sum - drop + x) / period;
    }
}

/// <summary>EMA seeded with the SMA of the first <c>period</c> values (same as TradingView's ta.ema).</summary>
public sealed class Ema(int period)
{
    readonly double _k = 2.0 / (period + 1);
    readonly Sma _seed = new(period);

    public double Value { get; private set; } = double.NaN;

    public double Next(double x) =>
        Value = double.IsNaN(Value) ? _seed.Next(x) : (x - Value) * _k + Value;

    public double Peek(double x) =>
        double.IsNaN(Value) ? _seed.Peek(x) : (x - Value) * _k + Value;
}

/// <summary>Wilder's RSI, seeded with the simple average of the first <c>period</c> gains and losses.</summary>
public sealed class Rsi(int period)
{
    double _prev = double.NaN, _avgGain, _avgLoss;
    int _count;

    public double Value { get; private set; } = double.NaN;

    public double Next(double x)
    {
        var (gain, loss, value) = Step(x);
        if (!double.IsNaN(_prev))
        {
            _avgGain = gain;
            _avgLoss = loss;
            _count++;
        }
        _prev = x;
        return Value = value;
    }

    public double Peek(double x) => Step(x).value;

    (double gain, double loss, double value) Step(double x)
    {
        if (double.IsNaN(_prev)) return (0, 0, double.NaN);
        double change = x - _prev;
        double up = Math.Max(change, 0), down = Math.Max(-change, 0);
        int n = _count + 1;
        double gain, loss;
        if (n < period) { gain = _avgGain + up; loss = _avgLoss + down; }            // still summing the seed
        else if (n == period) { gain = (_avgGain + up) / period; loss = (_avgLoss + down) / period; }
        else { gain = (_avgGain * (period - 1) + up) / period; loss = (_avgLoss * (period - 1) + down) / period; }
        if (n < period) return (gain, loss, double.NaN);
        double value = loss == 0 ? (gain == 0 ? 50 : 100) : 100 - 100 / (1 + gain / loss);
        return (gain, loss, value);
    }
}

public sealed class Macd(int fast = 12, int slow = 26, int signal = 9)
{
    readonly Ema _fast = new(fast), _slow = new(slow), _signal = new(signal);

    public (double Line, double Signal, double Histogram) Next(double x)
    {
        double line = _fast.Next(x) - _slow.Next(x);
        double sig = double.IsNaN(line) ? double.NaN : _signal.Next(line);
        return (line, sig, line - sig);
    }
}

public static class Cross
{
    /// <summary>True when <paramref name="a"/> moved from at-or-below <paramref name="b"/> to above it between two bars.</summary>
    public static bool Above(double prevA, double prevB, double a, double b) => prevA <= prevB && a > b;

    public static bool Below(double prevA, double prevB, double a, double b) => prevA >= prevB && a < b;
}
