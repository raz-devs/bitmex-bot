using Microsoft.AspNetCore.SignalR;

namespace BitmexBot.Web;

/// <summary>
/// Server → browser events: candle(tf, bar), signal, orders, position, margin, trades, status, log.
/// The browser calls Watch(tf) for the chart's timeframe to receive its candles.
/// </summary>
public sealed class TradingHub(TradingEngine engine) : Hub
{
    public static string Group(string tf) => "tf:" + tf;

    public async Task Watch(string tf, string? previous)
    {
        if (!string.IsNullOrEmpty(previous)) await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(previous));
        _ = engine.EnsureTimeframe(tf);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(tf));
    }
}
