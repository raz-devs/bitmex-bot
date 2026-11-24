using BitmexBot.Core;

namespace BitmexBot.Core.Tests;

public class PaperExchangeTests
{
    sealed class Recorder : IExchangeSink
    {
        public List<ExecutionInfo> Fills { get; } = [];
        public List<OrderInfo> Orders { get; } = [];
        public PositionInfo? Position { get; private set; }
        public void OnConnected(bool connected) { }
        public void OnTrade(DateTimeOffset time, double price, double size, DateTimeOffset received) { }
        public void OnQuote(double bid, double ask, double imbalance) { }
        public void OnInstrument(double? last, double? mark, double? funding, double? tickSize, int? lotSize) { }
        public void OnOrder(OrderInfo order) => Orders.Add(order);
        public void OnExecution(ExecutionInfo exec) => Fills.Add(exec);
        public void OnPosition(PositionInfo position) => Position = position;
        public void OnMargin(MarginInfo margin) { }
    }

    static (PaperExchange, Recorder) Create()
    {
        var ex = new PaperExchange(startPrice: 100, startBalanceBtc: 1, makerFee: 0.0002, takerFee: 0.00075, tickSize: 0.5, tradeInterval: TimeSpan.FromHours(1));
        var rec = new Recorder();
        var cts = new CancellationTokenSource();
        _ = ex.RunAsync(rec, cts.Token);   // registers the sink; the 1h interval means no random trades during the test
        return (ex, rec);
    }

    [Fact]
    public async Task Market_entry_then_limit_exit_books_inverse_pnl_and_fees()
    {
        var (ex, rec) = Create();
        await ex.PlaceOrderAsync(new OrderSpec("a", "Buy", 100, "Market"));
        Assert.Equal(100.5, rec.Fills[^1].AvgPrice);              // pays the one-tick spread
        Assert.Equal(100, ex.PositionQty);

        await ex.PlaceOrderAsync(new OrderSpec("b", "Sell", 100, "Limit", 102, ReduceOnly: true));
        ex.Trade(DateTimeOffset.UtcNow, 102, 1);                  // touch: no fill
        Assert.Equal(100, ex.PositionQty);
        ex.Trade(DateTimeOffset.UtcNow, 102.5, 1);                // through: fills at the limit
        Assert.Equal(0, ex.PositionQty);

        double pnl = 100 * (1 / 100.5 - 1 / 102.0);
        double fees = 100 / 100.5 * 0.00075 + 100 / 102.0 * 0.0002;
        Assert.Equal(1 + pnl - fees, ex.WalletBtc, 12);
        Assert.Equal(pnl, rec.Position!.RealisedBtc, 12);
    }

    [Fact]
    public async Task Stop_triggers_at_trade_price_and_reduce_only_never_flips()
    {
        var (ex, rec) = Create();
        await ex.PlaceOrderAsync(new OrderSpec("a", "Buy", 100, "Market"));
        await ex.PlaceOrderAsync(new OrderSpec("s", "Sell", 500, "Stop", StopPrice: 99, ReduceOnly: true));
        ex.Trade(DateTimeOffset.UtcNow, 98, 1);                   // gaps through the stop
        Assert.Equal(98, rec.Fills[^1].AvgPrice);
        Assert.Equal(100, rec.Fills[^1].CumQty);                  // capped to the position
        Assert.Equal(0, ex.PositionQty);
    }

    [Fact]
    public async Task Post_only_limit_that_would_cross_is_cancelled()
    {
        var (ex, rec) = Create();
        await ex.PlaceOrderAsync(new OrderSpec("p", "Buy", 100, "Limit", 101, PostOnly: true));
        Assert.Equal("Canceled", rec.Orders[^1].Status);
        Assert.Empty(rec.Fills);
    }

    [Fact]
    public async Task Selling_more_than_the_position_flips_to_short_at_the_fill_price()
    {
        var (ex, rec) = Create();
        await ex.PlaceOrderAsync(new OrderSpec("a", "Buy", 100, "Market"));
        await ex.PlaceOrderAsync(new OrderSpec("b", "Sell", 300, "Market"));
        Assert.Equal(-200, ex.PositionQty);
        Assert.Equal(100, rec.Position!.AvgEntry);
    }

    [Fact]
    public async Task History_ends_at_the_current_price_on_bar_boundaries()
    {
        var (ex, _) = Create();
        var (bars, _) = await ex.HistoryAsync("15m", 50, CancellationToken.None);
        Assert.Equal(50, bars.Count);
        Assert.Equal(100, bars[^1].Close);
        Assert.All(bars, b => Assert.Equal(0, b.Time.UtcTicks % TimeSpan.FromMinutes(15).Ticks));
        Assert.All(bars, b => Assert.True(b.High >= Math.Max(b.Open, b.Close) && b.Low <= Math.Min(b.Open, b.Close)));
    }
}
