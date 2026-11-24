using System.Globalization;
using BitmexBot.Core;

// Usage: dotnet run --project src/BitmexBot.Backtest -- --from 2024-01-01 --to 2025-01-01 --tf 15m [options]
// Options: --order limit|market  --tp 1.0  --sl 0.5  --contracts 100  --offset-ticks 0  --timeout-bars 2
//          --maker 0.0002  --taker 0.00075  --long-ema 70 --long-sma 80 --short-ema 100 --short-sma 80
//          --heikin (run on Heikin Ashi bars like the old bot)  --testnet  --data data  --trades trades.csv
var opt = ParseArgs(args);
string Get(string key, string fallback) => opt.TryGetValue(key, out var v) ? v : fallback;
int I(string key, int fallback) => int.Parse(Get(key, fallback.ToString()), CultureInfo.InvariantCulture);
double Dbl(string key, double fallback) => double.Parse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

var from = DateTimeOffset.Parse(Get("from", DateTimeOffset.UtcNow.AddDays(-90).ToString("yyyy-MM-dd")), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
var to = DateTimeOffset.Parse(Get("to", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
string tf = Get("tf", "15m");
bool testnet = opt.ContainsKey("testnet");

// BitMEX only serves 1m/5m/1h/1d; other timeframes are built from the nearest smaller one.
(string bin, int factor) = tf switch
{
    "1m" or "5m" or "1h" or "1d" => (tf, 1),
    "15m" => ("5m", 3),
    "30m" => ("5m", 6),
    "4h" => ("1h", 4),
    _ => throw new ArgumentException($"Unsupported timeframe {tf}. Use 1m, 5m, 15m, 30m, 1h, 4h or 1d."),
};

string cache = Path.Combine(Get("data", "data"), $"XBTUSD-{bin}-{from:yyyyMMdd}-{to:yyyyMMdd}{(testnet ? "-testnet" : "")}.csv");
List<Candle> raw;
if (File.Exists(cache))
{
    raw = BitmexCandles.ReadCsv(cache);
}
else
{
    Console.WriteLine($"Downloading XBTUSD {bin} candles {from:yyyy-MM-dd} to {to:yyyy-MM-dd}...");
    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("bitmex-bot-backtest/0.1");
    raw = await BitmexCandles.DownloadAsync(http, "XBTUSD", bin, from, to,
        testnet ? BitmexCandles.TestnetUrl : BitmexCandles.MainnetUrl, Console.WriteLine);
    BitmexCandles.WriteCsv(cache, raw);
}

var bars = Candles.Aggregate(raw, BitmexCandles.BinSizes[bin], factor);
var signalBars = opt.ContainsKey("heikin") ? Candles.HeikinAshi(bars) : bars;

var settings = new BacktestSettings
{
    Contracts = I("contracts", 100),
    EntryOrder = Get("order", "limit") == "market" ? EntryOrderType.Market : EntryOrderType.Limit,
    LimitOffsetTicks = I("offset-ticks", 0),
    EntryTimeoutBars = I("timeout-bars", 2),
    TakeProfitPct = Dbl("tp", 1.0),
    StopLossPct = Dbl("sl", 0.5),
    MakerFee = Dbl("maker", 0.0002),
    TakerFee = Dbl("taker", 0.00075),
};
var strategy = new EmaSmaCross(I("long-ema", 70), I("long-sma", 80), I("short-ema", 100), I("short-sma", 80));

// Signals come from (optionally Heikin Ashi) bars, fills always from real prices.
var result = Backtester.Run(bars, new SignalSource(signalBars, strategy), settings);

Console.WriteLine();
Console.WriteLine($"XBTUSD {tf}, {bars.Count} bars, {bars.FirstOrDefault().Time:yyyy-MM-dd} to {bars.LastOrDefault().Time:yyyy-MM-dd}");
Console.WriteLine($"Entry: {settings.EntryOrder}, TP {settings.TakeProfitPct}%, SL {settings.StopLossPct}%, {settings.Contracts} contracts{(opt.ContainsKey("heikin") ? ", Heikin Ashi signals" : "")}");
Console.WriteLine($"Signals:          {result.Signals}");
Console.WriteLine($"Missed entries:   {result.MissedEntries} (limit orders that never filled)");
Console.WriteLine($"Trades:           {result.Trades.Count}");
Console.WriteLine($"Win rate:         {result.WinRate:P1}");
Console.WriteLine($"Avg win / loss:   {result.AvgWinXbt:F8} / {result.AvgLossXbt:F8} XBT");
Console.WriteLine($"Expectancy/trade: {result.ExpectancyXbt:F8} XBT");
Console.WriteLine($"Profit factor:    {result.ProfitFactor:F2}");
Console.WriteLine($"Fees paid:        {result.FeesXbt:F8} XBT");
Console.WriteLine($"Net PnL:          {result.NetXbt:F8} XBT");
Console.WriteLine($"Max drawdown:     {result.MaxDrawdownXbt:F8} XBT");
Console.WriteLine(result.ExpectancyXbt > 0 ? "Positive expectancy on this data. Check other periods before trusting it." : "Negative expectancy: this setup loses money on this data.");

if (opt.TryGetValue("trades", out var tradesPath))
{
    File.WriteAllLines(tradesPath, result.Trades.Select(t => string.Join(',', t.Side, t.EntryTime.UtcDateTime.ToString("O"),
        t.EntryPrice.ToString(CultureInfo.InvariantCulture), t.ExitTime.UtcDateTime.ToString("O"),
        t.ExitPrice.ToString(CultureInfo.InvariantCulture), t.ExitReason, t.PnlXbt.ToString("F8", CultureInfo.InvariantCulture)))
        .Prepend("side,entryTime,entryPrice,exitTime,exitPrice,reason,pnlXbt"));
    Console.WriteLine($"Trades written to {tradesPath}");
}

static Dictionary<string, string> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string>();
    for (int i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) continue;
        string key = a[i][2..];
        d[key] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
    }
    return d;
}

/// <summary>Feeds the strategy the signal bar at the same index as the real bar the backtester is on.</summary>
sealed class SignalSource(IReadOnlyList<Candle> signalBars, IStrategy inner) : IStrategy
{
    int _i;
    public Signal OnBar(Candle bar) => inner.OnBar(signalBars[_i++]);
}
