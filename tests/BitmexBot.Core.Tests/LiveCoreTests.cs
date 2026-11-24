using System.Text.Json;
using BitmexBot.Core;

namespace BitmexBot.Core.Tests;

public class CandleBuilderTests
{
    static readonly DateTimeOffset T = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Builds_bars_from_trades_and_closes_on_the_next_bar()
    {
        var b = new CandleBuilder(TimeSpan.FromMinutes(1));
        Assert.Null(b.Add(T.AddSeconds(1), 100, 1));
        Assert.Null(b.Add(T.AddSeconds(20), 105, 2));
        Assert.Null(b.Add(T.AddSeconds(59), 98, 3));
        Assert.Equal(new Candle(T.AddMinutes(1), 100, 105, 98, 98, 6), b.Current);

        var closed = b.Add(T.AddMinutes(1), 99, 1);   // exactly on the boundary starts the next bar
        Assert.Equal(new Candle(T.AddMinutes(1), 100, 105, 98, 98, 6), closed);
        Assert.Equal(T.AddMinutes(2), b.Current!.Value.Time);
    }

    [Fact]
    public void Tick_closes_the_bar_when_the_clock_passes_its_close()
    {
        var b = new CandleBuilder(TimeSpan.FromMinutes(1));
        b.Add(T.AddSeconds(10), 100, 1);
        Assert.Null(b.Tick(T.AddSeconds(59)));
        var closed = b.Tick(T.AddMinutes(1).AddMilliseconds(5));
        Assert.Equal(T.AddMinutes(1), closed!.Value.Time);
        Assert.Equal(new Candle(T.AddMinutes(2), 100, 100, 100, 100, 0), b.Current);
        Assert.Null(b.Add(T.AddMinutes(1).AddSeconds(1), 101, 1));   // keeps building the new bar
        Assert.Equal(101, b.Current!.Value.High);
    }
}

public class RuleStrategyTests
{
    static Candle Bar(int i, double close) => new(DateTimeOffset.UnixEpoch.AddMinutes(i + 1), close, close, close, close, 1);

    static StrategyDefinition EmaCross(string evaluate = "close") => new()
    {
        Evaluate = evaluate,
        Long = new("all", [new(new Operand { Ind = "ema", Period = 3 }, "crossesAbove", new Operand { Ind = "sma", Period = 5 })]),
        Short = new("all", [new(new Operand { Ind = "ema", Period = 3 }, "crossesBelow", new Operand { Ind = "sma", Period = 5 })]),
    };

    static readonly double[] DownUpDown = [.. Enumerable.Range(0, 10).Select(i => 100.0 - i), .. Enumerable.Range(0, 10).Select(i => 91.0 + 2 * i), .. Enumerable.Range(0, 10).Select(i => 109.0 - 2 * i)];

    [Fact]
    public void Rule_strategy_matches_the_hand_written_ema_sma_cross()
    {
        var rules = new RuleStrategy(EmaCross());
        var reference = new EmaSmaCross(3, 5, 3, 5);
        for (int i = 0; i < DownUpDown.Length; i++)
            Assert.Equal(reference.OnBar(Bar(i, DownUpDown[i])), rules.OnBar(Bar(i, DownUpDown[i])));
    }

    [Fact]
    public void Peeking_the_forming_bar_does_not_change_later_results()
    {
        var peeked = new RuleStrategy(EmaCross("tick"));
        var plain = new RuleStrategy(EmaCross());
        for (int i = 0; i < DownUpDown.Length; i++)
        {
            peeked.Evaluate(Bar(i, DownUpDown[i] + 3), commit: false);   // a forming value that never closed
            Assert.Equal(plain.OnBar(Bar(i, DownUpDown[i])), peeked.OnBar(Bar(i, DownUpDown[i])));
        }
    }

    [Fact]
    public void Peek_sees_a_cross_before_the_bar_closes()
    {
        var s = new RuleStrategy(EmaCross("tick"));
        int i = 0;
        for (; i < 10; i++) s.OnBar(Bar(i, 100.0 - i));            // falling: EMA below SMA
        Assert.Equal(Signal.Long, s.Evaluate(Bar(i, 120), commit: false));   // a big up-move mid-bar crosses already
    }

    [Fact]
    public void Any_group_and_value_operands_work()
    {
        var def = new StrategyDefinition
        {
            Long = new("any", [
                new(new Operand { Ind = "price" }, "gt", new Operand { Ind = "value", Value = 200 }),
                new(new Operand { Ind = "price" }, "lt", new Operand { Ind = "value", Value = 50 })]),
        };
        var s = new RuleStrategy(def);
        Assert.Equal(Signal.None, s.OnBar(Bar(0, 100)));
        Assert.Equal(Signal.Long, s.OnBar(Bar(1, 201)));
        Assert.Equal(Signal.Long, s.OnBar(Bar(2, 49)));
    }

    [Fact]
    public void Definitions_round_trip_through_web_json()
    {
        var json = """
            {"name":"RSI dip","timeframe":"5m","evaluate":"tick",
             "long":{"mode":"all","rules":[{"left":{"ind":"rsi","period":14},"op":"lt","right":{"ind":"value","value":30}}]},
             "order":{"type":"Market","contracts":200,"takeProfitPct":0.8,"stopLossPct":0.4},"autoTrade":true}
            """;
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var def = JsonSerializer.Deserialize<StrategyDefinition>(json, opts)!;
        Assert.Equal("rsi", def.Long!.Rules[0].Left.Ind);
        Assert.Equal(EntryOrderType.Market, def.Order.Type);
        Assert.Equal(200, def.Order.Contracts);
        Assert.True(def.AutoTrade);
        Assert.Equal(def, JsonSerializer.Deserialize<StrategyDefinition>(JsonSerializer.Serialize(def, opts), opts) with { Long = def.Long });
        Assert.Equal("RSI(14) lt 30", def.Long.Rules[0].ToString());
    }

    [Fact]
    public void Unknown_indicator_is_rejected()
    {
        var def = new StrategyDefinition { Long = new("all", [new(new Operand { Ind = "magic" }, "gt", new Operand { Ind = "value" })]) };
        Assert.Throws<ArgumentException>(() => new RuleStrategy(def));
    }
}
