namespace BitmexBot.Core;

public enum EntryOrderType { Limit, Market }

public sealed record BacktestSettings
{
    /// <summary>XBTUSD contracts per trade (1 contract = 1 USD).</summary>
    public int Contracts { get; init; } = 100;
    public EntryOrderType EntryOrder { get; init; } = EntryOrderType.Limit;
    /// <summary>Limit entry price = signal bar close minus (long) / plus (short) this many ticks.</summary>
    public int LimitOffsetTicks { get; init; } = 0;
    /// <summary>Cancel an unfilled limit entry after this many bars (the old bot cancelled after a timer).</summary>
    public int EntryTimeoutBars { get; init; } = 2;
    public double TakeProfitPct { get; init; } = 1.0;
    public double StopLossPct { get; init; } = 0.5;
    /// <summary>Fee rates as fractions of notional. Negative = rebate. Check your own BitMEX fee tier.</summary>
    public double MakerFee { get; init; } = 0.0002;
    public double TakerFee { get; init; } = 0.00075;
    public double TickSize { get; init; } = 0.5;
    /// <summary>Applied against you on market entries and stop-loss exits.</summary>
    public int SlippageTicks { get; init; } = 1;
}

public sealed record Trade(
    Signal Side, DateTimeOffset EntryTime, double EntryPrice,
    DateTimeOffset ExitTime, double ExitPrice, string ExitReason,
    double PnlXbt, double FeesXbt);

public sealed record BacktestResult(IReadOnlyList<Trade> Trades, int Signals, int MissedEntries)
{
    public int Wins => Trades.Count(t => t.PnlXbt > 0);
    public double WinRate => Trades.Count == 0 ? 0 : (double)Wins / Trades.Count;
    public double NetXbt => Trades.Sum(t => t.PnlXbt);
    public double FeesXbt => Trades.Sum(t => t.FeesXbt);
    public double AvgWinXbt => Trades.Where(t => t.PnlXbt > 0).Select(t => t.PnlXbt).DefaultIfEmpty().Average();
    public double AvgLossXbt => Trades.Where(t => t.PnlXbt <= 0).Select(t => t.PnlXbt).DefaultIfEmpty().Average();
    /// <summary>Average net result per trade. Below zero means the strategy loses money over time.</summary>
    public double ExpectancyXbt => Trades.Count == 0 ? 0 : NetXbt / Trades.Count;
    public double ProfitFactor
    {
        get
        {
            double won = Trades.Where(t => t.PnlXbt > 0).Sum(t => t.PnlXbt);
            double lost = -Trades.Where(t => t.PnlXbt <= 0).Sum(t => t.PnlXbt);
            return lost == 0 ? double.PositiveInfinity : won / lost;
        }
    }
    public double MaxDrawdownXbt
    {
        get
        {
            double equity = 0, peak = 0, maxDd = 0;
            foreach (var t in Trades)
            {
                equity += t.PnlXbt;
                peak = Math.Max(peak, equity);
                maxDd = Math.Max(maxDd, peak - equity);
            }
            return maxDd;
        }
    }
}

/// <summary>
/// Bar-by-bar simulation of one XBTUSD position (inverse contract: PnL in XBT = contracts × (1/entry − 1/exit)).
/// Fill rules are deliberately pessimistic:
/// a limit order fills only if price trades THROUGH it (touching is not enough);
/// if take profit and stop loss are both inside one bar, the stop is assumed to hit first;
/// a stop that gaps fills at the bar's open, not at the stop price.
/// Exits are TP/SL only; signals while a position is open are ignored.
/// </summary>
public static class Backtester
{
    public static BacktestResult Run(IReadOnlyList<Candle> bars, IStrategy strategy, BacktestSettings s)
    {
        var trades = new List<Trade>();
        int signals = 0, missed = 0;

        Signal pendingSide = Signal.None;   // unfilled entry order
        double pendingPrice = 0;
        int pendingPlacedAt = -1;

        Signal side = Signal.None;          // open position
        double entry = 0, entryFee = 0, tp = 0, sl = 0;
        DateTimeOffset entryTime = default;
        int exitsFrom = int.MaxValue;

        for (int i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];

            // 1. Pending entry
            if (pendingSide != Signal.None)
            {
                if (s.EntryOrder == EntryOrderType.Market)
                {
                    double slip = s.SlippageTicks * s.TickSize;
                    Open(pendingSide, pendingSide == Signal.Long ? bar.Open + slip : bar.Open - slip, s.TakerFee, i);
                    exitsFrom = i;   // filled at the open, so the rest of this bar happens after entry
                }
                else if (pendingSide == Signal.Long ? bar.Low < pendingPrice : bar.High > pendingPrice)
                {
                    Open(pendingSide, pendingPrice, s.MakerFee, i);
                    // shortcut: intrabar order of fill vs TP/SL is unknown, so exits start next bar; use 1m data or trades to refine.
                    exitsFrom = i + 1;
                }
                else if (i - pendingPlacedAt >= s.EntryTimeoutBars)
                {
                    pendingSide = Signal.None;
                    missed++;
                }
            }

            // 2. Exits
            if (side != Signal.None && i >= exitsFrom)
            {
                bool isLong = side == Signal.Long;
                bool slHit = isLong ? bar.Low <= sl : bar.High >= sl;
                bool tpHit = isLong ? bar.High > tp : bar.Low < tp;
                if (slHit)
                {
                    double slip = s.SlippageTicks * s.TickSize;
                    double px = isLong ? Math.Min(sl, bar.Open) - slip : Math.Max(sl, bar.Open) + slip;
                    Close(bar.Time, px, s.TakerFee, "StopLoss");
                }
                else if (tpHit)
                {
                    Close(bar.Time, tp, s.MakerFee, "TakeProfit");
                }
            }

            // 3. New signal (strategy sees every bar so its indicators stay current)
            var signal = strategy.OnBar(bar);
            if (signal == Signal.None) continue;
            signals++;
            if (side != Signal.None) continue;
            if (pendingSide != Signal.None && pendingSide != signal) missed++;   // replaced before it filled

            pendingSide = signal;
            pendingPlacedAt = i;
            double offset = s.LimitOffsetTicks * s.TickSize;
            pendingPrice = signal == Signal.Long ? bar.Close - offset : bar.Close + offset;
        }

        if (side != Signal.None) Close(bars[^1].Time, bars[^1].Close, s.TakerFee, "EndOfData");
        if (pendingSide != Signal.None) missed++;
        return new BacktestResult(trades, signals, missed);

        void Open(Signal newSide, double price, double feeRate, int index)
        {
            side = newSide;
            entry = price;
            entryTime = bars[index].Time;
            entryFee = s.Contracts / price * feeRate;
            double tpMove = s.TakeProfitPct / 100, slMove = s.StopLossPct / 100;
            tp = newSide == Signal.Long ? price * (1 + tpMove) : price * (1 - tpMove);
            sl = newSide == Signal.Long ? price * (1 - slMove) : price * (1 + slMove);
            pendingSide = Signal.None;
        }

        void Close(DateTimeOffset time, double price, double feeRate, string reason)
        {
            double gross = side == Signal.Long
                ? s.Contracts * (1 / entry - 1 / price)
                : s.Contracts * (1 / price - 1 / entry);
            double fees = entryFee + s.Contracts / price * feeRate;
            trades.Add(new Trade(side, entryTime, entry, time, price, reason, gross - fees, fees));
            side = Signal.None;
            exitsFrom = int.MaxValue;
        }
    }
}
