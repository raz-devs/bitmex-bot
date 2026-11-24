using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BitmexBot.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BitmexBot.Web.Tests;

/// <summary>Runs the whole server against a paper exchange whose price only moves when the test says so.</summary>
public sealed class AppTests : IDisposable
{
    readonly PaperExchange _paper = new(startPrice: 100, tickSize: 0.5, tradeInterval: TimeSpan.FromHours(1));
    readonly string _dataDir = Path.Combine(Path.GetTempPath(), "bb-test-" + Guid.NewGuid().ToString("N"));
    readonly WebApplicationFactory<Program> _factory;
    readonly HttpClient _http;

    public AppTests() : this(token: "") { }

    AppTests(string token)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("App:DataDir", _dataDir);
            b.UseSetting("App:AccessToken", token);
            b.ConfigureServices(s => s.AddSingleton<IExchange>(_paper));
        });
        _http = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true);
    }

    async Task<JsonElement> State() => await _http.GetFromJsonAsync<JsonElement>("/api/state");

    async Task<JsonElement> WaitFor(Func<JsonElement, bool> condition, string what)
    {
        for (int i = 0; i < 100; i++)
        {
            var s = await State();
            if (condition(s)) return s;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Timed out waiting for {what}: {await State()}");
    }

    [Fact]
    public async Task Manual_order_gets_tp_and_sl_and_take_profit_cancels_the_stop()
    {
        await WaitFor(s => s.GetProperty("status").GetProperty("connected").GetBoolean(), "connection");
        var resp = await _http.PostAsJsonAsync("/api/orders", new { side = "Buy", type = "Market", contracts = 100, takeProfitPct = 1, stopLossPct = 1 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var s = await WaitFor(s => s.GetProperty("orders").GetArrayLength() == 2, "TP and SL orders");
        var trade = s.GetProperty("trades")[0];
        Assert.Equal("Open", trade.GetProperty("state").GetString());
        Assert.Equal(100.5, trade.GetProperty("avgEntry").GetDouble());
        Assert.Equal(101.5, trade.GetProperty("takeProfit").GetDouble());   // 100.5 * 1.01 rounded to the 0.5 tick
        Assert.Equal(99.5, trade.GetProperty("stopLoss").GetDouble());
        Assert.Equal(100, s.GetProperty("position").GetProperty("qty").GetInt32());

        _paper.Trade(DateTimeOffset.UtcNow, 102, 1);
        s = await WaitFor(s => s.GetProperty("trades")[0].GetProperty("state").GetString() == "Closed"
                               && s.GetProperty("orders").GetArrayLength() == 0, "take profit and stop cancel");
        Assert.StartsWith("Take profit", s.GetProperty("trades")[0].GetProperty("note").GetString());
        Assert.Equal(0, s.GetProperty("position").GetProperty("qty").GetInt32());
    }

    [Fact]
    public async Task Risk_limits_reject_oversized_orders()
    {
        var resp = await _http.PostAsJsonAsync("/api/orders", new { side = "Buy", type = "Market", contracts = 5000 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("1000 contract limit", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Invalid_strategy_is_rejected_and_valid_one_is_saved()
    {
        var bad = await _http.PostAsJsonAsync("/api/strategies", new
        {
            name = "bad", timeframe = "15m",
            @long = new { mode = "all", rules = new[] { new { left = new { ind = "magic" }, op = "gt", right = new { ind = "value", value = 1 } } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await _http.PostAsJsonAsync("/api/strategies", new
        {
            id = "rsi1", name = "RSI", timeframe = "5m",
            @long = new { mode = "all", rules = new[] { new { left = new { ind = "rsi", period = 14 }, op = "lt", right = new { ind = "value", value = 30 } } } },
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var all = await _http.GetFromJsonAsync<JsonElement>("/api/strategies");
        Assert.Contains(all.EnumerateArray(), x => x.GetProperty("id").GetString() == "rsi1");
        Assert.True(File.Exists(Path.Combine(_dataDir, "strategies.json")));
    }

    [Fact]
    public async Task Auto_trading_strategy_places_an_order_on_its_signal()
    {
        await _http.PostAsJsonAsync("/api/strategies", new
        {
            id = "auto1", name = "Breakout", timeframe = "1m", evaluate = "tick", autoTrade = true,
            @long = new { mode = "all", rules = new[] { new { left = new { ind = "price" }, op = "gt", right = new { ind = "value", value = 105 } } } },
            order = new { type = "Market", contracts = 100, takeProfitPct = 2, stopLossPct = 2 },
        });
        (await _http.PostAsJsonAsync("/api/autotrade", new { enabled = true })).EnsureSuccessStatusCode();

        var s = await WaitFor(s =>
        {
            _paper.Trade(DateTimeOffset.UtcNow, 106, 1);   // repeat until the runner has loaded its history and sees it
            return s.GetProperty("position").GetProperty("qty").GetInt32() == 100;
        }, "auto trade entry");
        var signal = s.GetProperty("signals")[0];
        Assert.Equal("Long", signal.GetProperty("side").GetString());
        Assert.StartsWith("order sent", signal.GetProperty("action").GetString());

        (await _http.PostAsync("/api/kill", null)).EnsureSuccessStatusCode();
        s = await WaitFor(s => s.GetProperty("position").GetProperty("qty").GetInt32() == 0, "kill switch flatten");
        Assert.False(s.GetProperty("status").GetProperty("autoTrading").GetBoolean());
        Assert.Equal(0, s.GetProperty("orders").GetArrayLength());
    }

    [Fact]
    public async Task Candles_and_backtest_endpoints_work()
    {
        var candles = await _http.GetFromJsonAsync<JsonElement>("/api/candles?tf=15m");
        Assert.True(candles.GetArrayLength() >= 1000);

        var resp = await _http.PostAsJsonAsync("/api/backtest", new
        {
            bars = 2000,
            strategy = new
            {
                name = "cross", timeframe = "15m",
                @long = new { mode = "all", rules = new[] { new { left = new { ind = "ema", period = 9 }, op = "crossesAbove", right = new { ind = "ema", period = 21 } } } },
                @short = new { mode = "all", rules = new[] { new { left = new { ind = "ema", period = 9 }, op = "crossesBelow", right = new { ind = "ema", period = 21 } } } },
            },
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var r = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2000, r.GetProperty("bars").GetInt32());
        Assert.True(r.GetProperty("signals").GetInt32() > 0);
    }

    [Fact]
    public async Task Access_token_is_required_when_configured()
    {
        using var secured = new AppTests(token: "s3cret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await secured._http.GetAsync("/api/state")).StatusCode);
        secured._http.DefaultRequestHeaders.Authorization = new("Bearer", "s3cret");
        Assert.Equal(HttpStatusCode.OK, (await secured._http.GetAsync("/api/state")).StatusCode);
    }
}
