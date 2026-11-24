namespace BitmexBot.Core;

// Exchange-neutral contract between the trading engine and an exchange. Prices are quote currency (USD),
// sizes are inverse contracts (1 contract = 1 USD), balances and PnL are in the settlement coin (BTC).

public sealed record OrderInfo(string OrderId, string? ClientId, string Side, int Qty, double? Price, double? StopPrice,
    string Type, string Status, int CumQty, bool ReduceOnly, DateTimeOffset Time);

/// <summary>A fill (possibly partial). <see cref="Status"/>: PartiallyFilled or Filled.</summary>
public sealed record ExecutionInfo(string OrderId, string? ClientId, string Status, int CumQty, double AvgPrice);

public sealed record PositionInfo(int Qty, double? AvgEntry, double? Liquidation, double UnrealisedBtc, double RealisedBtc);

public sealed record MarginInfo(double WalletBtc, double EquityBtc, double AvailableBtc);

/// <summary>Side: Buy|Sell. Type: Limit|Market|Stop (stop-market, triggers on last price).</summary>
public sealed record OrderSpec(string ClientId, string Side, int Qty, string Type,
    double? Price = null, double? StopPrice = null, bool PostOnly = false, bool ReduceOnly = false);

/// <summary>Receives normalized events from an exchange feed. Calls may come from any thread.</summary>
public interface IExchangeSink
{
    void OnConnected(bool connected);
    void OnTrade(DateTimeOffset time, double price, double size, DateTimeOffset received);
    void OnQuote(double bid, double ask, double imbalance);
    void OnInstrument(double? last, double? mark, double? funding, double? tickSize, int? lotSize);
    void OnOrder(OrderInfo order);
    void OnExecution(ExecutionInfo exec);
    void OnPosition(PositionInfo position);
    void OnMargin(MarginInfo margin);
}

public interface IExchange
{
    string Name { get; }
    string Symbol { get; }
    bool Testnet { get; }
    /// <summary>False when no API key is configured: market data only.</summary>
    bool CanTrade { get; }

    /// <summary>Connects, streams events into <paramref name="sink"/> and reconnects on failure until cancelled.</summary>
    Task RunAsync(IExchangeSink sink, CancellationToken ct);

    /// <summary>The last <paramref name="bars"/> closed bars (oldest first) and the bar still forming, if known.</summary>
    Task<(List<Candle> Closed, Candle? Partial)> HistoryAsync(string timeframe, int bars, CancellationToken ct);

    /// <summary>Returns the exchange's order id. Throws <see cref="ExchangeException"/> when the exchange rejects it.</summary>
    Task<string> PlaceOrderAsync(OrderSpec order);
    Task AmendQtyAsync(string orderId, int qty);
    Task CancelAsync(string orderId);
    Task CancelAllAsync();
    /// <summary>Market-closes the whole position.</summary>
    Task ClosePositionAsync();
}

public sealed class ExchangeException(string message) : Exception(message);
