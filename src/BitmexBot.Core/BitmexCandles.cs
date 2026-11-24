using System.Globalization;
using System.Net;
using System.Text.Json;

namespace BitmexBot.Core;

/// <summary>Downloads historical candles from BitMEX's public REST API and caches them as CSV.</summary>
public static class BitmexCandles
{
    public const string MainnetUrl = "https://www.bitmex.com";
    public const string TestnetUrl = "https://testnet.bitmex.com";

    /// <summary>BitMEX serves 1m, 5m, 1h and 1d bins. Build other sizes with <see cref="Candles.Aggregate"/>.</summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> BinSizes = new Dictionary<string, TimeSpan>
    {
        ["1m"] = TimeSpan.FromMinutes(1), ["5m"] = TimeSpan.FromMinutes(5),
        ["1h"] = TimeSpan.FromHours(1), ["1d"] = TimeSpan.FromDays(1),
    };

    public static async Task<List<Candle>> DownloadAsync(
        HttpClient http, string symbol, string binSize, DateTimeOffset from, DateTimeOffset to,
        string baseUrl = MainnetUrl, Action<string>? log = null, CancellationToken ct = default)
    {
        if (!BinSizes.TryGetValue(binSize, out var size)) throw new ArgumentException($"Unsupported bin size {binSize}", nameof(binSize));
        var result = new List<Candle>();
        var start = from;
        while (start < to)
        {
            // startTime is inclusive and filters on the bin's close timestamp.
            string url = $"{baseUrl}/api/v1/trade/bucketed?binSize={binSize}&symbol={Uri.EscapeDataString(symbol)}" +
                         $"&partial=false&reverse=false&count=1000&startTime={start.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}" +
                         $"&endTime={to.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}";
            using var resp = await http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                log?.Invoke($"Rate limited, waiting {wait.TotalSeconds:0}s");
                await Task.Delay(wait, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();

            var page = Parse(await resp.Content.ReadAsStringAsync(ct));
            if (page.Count == 0) break;
            result.AddRange(page);
            log?.Invoke($"{result.Count} bars, up to {page[^1].Time:yyyy-MM-dd HH:mm}");
            start = page[^1].Time + size;
            await Task.Delay(TimeSpan.FromSeconds(2.1), ct);   // unauthenticated limit is 30 requests/minute
        }
        return result;
    }

    public static List<Candle> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<Candle>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            // Bins with no trades come back with null prices; skip them.
            if (e.GetProperty("open").ValueKind != JsonValueKind.Number) continue;
            list.Add(new Candle(
                e.GetProperty("timestamp").GetDateTimeOffset(),
                e.GetProperty("open").GetDouble(),
                e.GetProperty("high").GetDouble(),
                e.GetProperty("low").GetDouble(),
                e.GetProperty("close").GetDouble(),
                e.GetProperty("volume").GetDouble()));
        }
        return list;
    }

    public static void WriteCsv(string path, IEnumerable<Candle> bars)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new StreamWriter(path);
        w.WriteLine("time,open,high,low,close,volume");
        foreach (var b in bars)
            w.WriteLine(string.Join(',', b.Time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                F(b.Open), F(b.High), F(b.Low), F(b.Close), F(b.Volume)));

        static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);
    }

    public static List<Candle> ReadCsv(string path) =>
        File.ReadLines(path).Skip(1).Where(l => l.Length > 0).Select(l =>
        {
            var p = l.Split(',');
            return new Candle(
                DateTimeOffset.Parse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                D(p[1]), D(p[2]), D(p[3]), D(p[4]), D(p[5]));
        }).ToList();

    // Invariant culture so the CSV reads the same on any Windows locale (some use a comma as decimal separator).
    static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
