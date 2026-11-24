# bitmex-bot

Crypto trading desk in C# (.NET 10) and React: live chart with indicators, a rule builder for entries and exits,
limit/market orders with take profit and stop loss, risk limits, and a backtester that runs the same strategy code.

BitMEX stopped trading in September 2026, so the app talks to exchanges through an adapter interface (`IExchange`).
It ships with a **paper exchange** (a simulator) so everything can be tried without an account. A real exchange is
one new adapter class; nothing else changes.

## Run the app

Requires the .NET 10 SDK and Node 20+.

```bash
cd web && npm install && npm run build && cd ..        # builds the UI into src/BitmexBot.Web/wwwroot
dotnet run --project src/BitmexBot.Web                   # http://localhost:5000
```

For UI development, run the server and then `npm run dev` in `web/` (Vite proxies `/api` and `/hub` to :5000).

### Settings (`src/BitmexBot.Web/appsettings.json`, or environment variables like `Exchange__ApiKey`)

| Setting | Default | Meaning |
|---|---|---|
| `Exchange:Name` | `paper` | Which adapter runs. Only `paper` exists today |
| `Exchange:Testnet` | `true` | Real adapters must default to testnet; mainnet only when set to `false` |
| `Exchange:ApiKey` / `ApiSecret` | empty | Stay on the server. Without them, real adapters run market data only |
| `Exchange:MakerFee` / `TakerFee` | `0.0002` / `0.00075` | Used by the paper exchange and backtests |
| `Risk:MaxContracts` | `1000` | Largest order and largest absolute position |
| `Risk:MaxOrdersPerMinute` | `20` | Entry orders per minute, manual and automatic |
| `Risk:DailyLossLimitPct` | `5` | Auto trading switches off after this drawdown from the day's starting equity |
| `App:AccessToken` | empty | If set, the UI asks for it and every API/hub call must send it. Otherwise keep the server on localhost |
| `App:DataDir` | `data` | Strategies (`strategies.json`) and backtest CSVs |

Use `dotnet user-secrets` or environment variables for keys rather than committing them.

### How it stays fast

- The exchange adapter's receive loop only hands events to a queue; one worker does all processing, so slow work never backs up the socket.
- Candles for every timeframe are built from individual trades, so the forming bar is known on every trade.
- Indicators update in O(1) per bar. Strategies set to "check on every trade" evaluate the forming bar on each trade (via `Peek`) and fire once per bar per side. "Check on candle close" fires the moment a bar closes (a 200 ms clock closes bars even without a new trade).
- The header shows feed lag (exchange timestamp to processing) and signal calculation time.

### Orders

Entries are limit (default best bid/ask, optional offset, post-only, cancelled after N bars) or market. When an entry fills, the app places a reduce-only take-profit limit and a reduce-only stop for the filled quantity, resizes them on partial fills, and cancels the other one when either fills. Strategies only place orders when both the strategy's "Place orders automatically" box and the global Auto trading switch are on. The kill switch cancels everything, closes the position and switches auto trading off.

### Adding an exchange

Implement `IExchange` (`src/BitmexBot.Core/Exchange.cs`): stream trades, quotes, orders, executions, position and balance into the `IExchangeSink`, and map `OrderSpec` (Limit/Market/Stop, post-only, reduce-only) to the exchange's API. Register it in `src/BitmexBot.Web/Program.cs`. `PaperExchange` is a complete reference. Contracts are treated as inverse (1 contract = 1 USD, PnL in BTC).

## Backtest from the command line (BitMEX history)

This CLI downloads from BitMEX's API, which may no longer be available. In the app, backtests run on any CSV in the data folder.

```bash
dotnet run --project src/BitmexBot.Backtest -- --from 2024-01-01 --to 2025-01-01 --tf 15m
```

The first run downloads candles from BitMEX's public API (about 2 seconds per 1,000 bars because of the rate limit) and caches them in `data/`.

| Option | Default | Meaning |
|---|---|---|
| `--tf` | `15m` | 1m, 5m, 15m, 30m, 1h, 4h, 1d |
| `--order` | `limit` | `limit` or `market` entry |
| `--offset-ticks` | `0` | Limit price = signal close − ticks (long) / + ticks (short) |
| `--timeout-bars` | `2` | Cancel an unfilled limit entry after this many bars |
| `--tp` / `--sl` | `1.0` / `0.5` | Take profit / stop loss, % from entry |
| `--contracts` | `100` | XBTUSD contracts (1 contract = 1 USD) |
| `--maker` / `--taker` | `0.0002` / `0.00075` | Fee rates. Set these to your own BitMEX fee tier |
| `--long-ema --long-sma --short-ema --short-sma` | `70 80 100 80` | Strategy periods (the old bot's defaults) |
| `--heikin` | off | Compute signals on Heikin Ashi bars, like the old bot |
| `--testnet` | off | Download from testnet instead of mainnet |
| `--trades file.csv` | | Write every trade to a CSV |

## How fills are simulated

The rules lean pessimistic, so a strategy that only looks good because of easy fills shows up as a loser here:

- A limit order fills only if price trades **through** it. Touching it is not enough. Unfilled orders count as missed entries.
- Market entries fill at the next bar's open, with slippage against you.
- If take profit and stop loss are both inside one bar, the stop is assumed to hit first.
- A stop that gaps fills at the bar's open, not at the stop price.
- PnL uses the inverse-contract formula: `contracts × (1/entry − 1/exit)` XBT, minus maker or taker fees on each side.

Exits are TP/SL only, one position at a time.

## Layout

- `src/BitmexBot.Core`: indicators (SMA, EMA, RSI, MACD, Bollinger, with `Peek` for the forming bar), candle builder, rule engine (`RuleStrategy`), backtester, `IExchange` and the paper exchange.
- `src/BitmexBot.Web`: ASP.NET Core server: trading engine, order manager, SignalR hub, REST API.
- `web/`: React + TradingView Lightweight Charts UI.
- `src/BitmexBot.Backtest`: command line backtester.
- `tests/`: run everything with `dotnet test`.
