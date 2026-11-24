using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using BitmexBot.Core;
using BitmexBot.Web;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ExchangeOptions>(builder.Configuration.GetSection("Exchange"));
builder.Services.Configure<RiskOptions>(builder.Configuration.GetSection("Risk"));
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;   // NaN indicator values
});
builder.Services.AddSignalR().AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.PayloadSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
});

builder.Services.AddSingleton<IExchange>(sp =>
{
    var o = sp.GetRequiredService<IOptions<ExchangeOptions>>().Value;
    return o.Name switch
    {
        "paper" => new PaperExchange(o.PaperStartPrice, o.PaperStartBalanceBtc, o.MakerFee, o.TakerFee),
        // New exchanges: implement IExchange (see src/BitmexBot.Core/Exchange.cs) and add them here.
        _ => throw new InvalidOperationException($"Unknown exchange '{o.Name}'. Available: paper"),
    };
});
builder.Services.AddSingleton<StrategyStore>();
builder.Services.AddSingleton<TradingEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TradingEngine>());
builder.Services.AddHostedService<ExchangeRunner>();

var app = builder.Build();
var appOptions = app.Services.GetRequiredService<IOptions<AppOptions>>().Value;
var exchangeOptions = app.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value;
if (appOptions.AccessToken.Length == 0)
    app.Logger.LogWarning("App:AccessToken is empty: anyone who can reach this server can place orders. Keep it bound to localhost or set a token.");

// Token check for the API and the hub. Static files (the UI itself) stay public so the login screen can load.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (appOptions.AccessToken.Length > 0 && (path.StartsWithSegments("/api") || path.StartsWithSegments("/hub")))
    {
        string? given = ctx.Request.Headers.Authorization.ToString() is { Length: > 7 } h && h.StartsWith("Bearer ") ? h[7..] : ctx.Request.Query["access_token"];
        if (given is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(appOptions.AccessToken)))
        {
            ctx.Response.StatusCode = 401;
            return;
        }
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<TradingHub>("/hub");

var api = app.MapGroup("/api");
api.AddEndpointFilter(async (ctx, next) =>
{
    try { return await next(ctx); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExchangeException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/state", (TradingEngine e) => Results.Ok(e.State()));
api.MapGet("/candles", async (string tf, TradingEngine e) => Results.Ok(await e.GetCandles(tf)));

api.MapGet("/strategies", (StrategyStore s) => s.All);
api.MapPost("/strategies", (StrategyDefinition def, StrategyStore s) => s.Upsert(def));
api.MapDelete("/strategies/{id}", (string id, StrategyStore s) => s.Delete(id) ? Results.NoContent() : Results.NotFound());

api.MapPost("/orders", async (TradeRequest req, TradingEngine e) => await e.Orders.SubmitManual(req));
api.MapPost("/orders/{id}/cancel", async (string id, IExchange ex) => { await ex.CancelAsync(id); return Results.NoContent(); });
api.MapPost("/orders/cancel-all", async (IExchange ex) => { await ex.CancelAllAsync(); return Results.NoContent(); });
api.MapPost("/position/close", async (IExchange ex) => { await ex.ClosePositionAsync(); return Results.NoContent(); });
api.MapPost("/kill", async (TradingEngine e) => { await e.Orders.Kill(); return Results.NoContent(); });
api.MapPost("/autotrade", (AutoTradeRequest req, TradingEngine e) => { e.Orders.SetAutoTrading(req.Enabled); return Results.NoContent(); });

// Backtests run on a CSV in the data folder (time,open,high,low,close,volume; time = bar close, UTC) or on the exchange's history.
api.MapGet("/datasets", () => Directory.Exists(appOptions.DataDir)
    ? Directory.GetFiles(appOptions.DataDir, "*.csv").Select(Path.GetFileName).Order().ToList()
    : []);
api.MapPost("/backtest", async (BacktestRequest req, IExchange ex) =>
{
    List<Candle> bars;
    if (string.IsNullOrEmpty(req.Dataset))
    {
        bars = (await ex.HistoryAsync(req.Strategy.Timeframe, Math.Clamp(req.Bars, 100, 20000), CancellationToken.None)).Closed;
    }
    else
    {
        string file = Path.Combine(appOptions.DataDir, Path.GetFileName(req.Dataset));   // GetFileName blocks ../ paths
        if (!File.Exists(file)) return Results.NotFound(new { error = $"No dataset {req.Dataset}" });
        bars = BitmexCandles.ReadCsv(file);
        var size = Timeframes.Size(req.Strategy.Timeframe);
        var source = req.DatasetTimeframe is null ? size : Timeframes.Size(req.DatasetTimeframe);
        if (source != size)
        {
            if (size.Ticks % source.Ticks != 0 || size < source) throw new ArgumentException($"Can't build {req.Strategy.Timeframe} bars from {req.DatasetTimeframe} data");
            bars = Candles.Aggregate(bars, source, (int)(size.Ticks / source.Ticks));
        }
    }
    var result = Backtester.Run(bars, new RuleStrategy(req.Strategy), req.Strategy.ToBacktestSettings(exchangeOptions.MakerFee, exchangeOptions.TakerFee));
    return Results.Ok(new
    {
        bars = bars.Count,
        from = bars.FirstOrDefault().Time,
        to = bars.LastOrDefault().Time,
        result.Signals, result.MissedEntries, trades = result.Trades.Count, result.Wins, result.WinRate,
        result.NetXbt, result.FeesXbt, result.AvgWinXbt, result.AvgLossXbt, result.ExpectancyXbt,
        profitFactor = double.IsInfinity(result.ProfitFactor) ? (double?)null : result.ProfitFactor,
        result.MaxDrawdownXbt,
        tradeList = result.Trades.TakeLast(500),
    });
});

app.MapFallbackToFile("index.html");
app.Run();

public sealed record AutoTradeRequest(bool Enabled);
public sealed record BacktestRequest(StrategyDefinition Strategy, string? Dataset, string? DatasetTimeframe, int Bars = 1000);

/// <summary>Runs the exchange feed into the engine and restarts it if it ever throws.</summary>
sealed class ExchangeRunner(IExchange exchange, TradingEngine engine, ILogger<ExchangeRunner> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await exchange.RunAsync(engine, stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "{Exchange} feed stopped", exchange.Name); }
            engine.OnConnected(false);
            await Task.Delay(TimeSpan.FromSeconds(5), stop).ContinueWith(_ => { });
        }
    }
}
public partial class Program;
