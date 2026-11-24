# bitmex-bot

XBTUSD trading tools for BitMEX, in C# (.NET 10).

This first part is the shared strategy core and a backtester. The live engine and web app (websocket feed, TradingView Lightweight Charts, rule editor, orders with TP/SL) will be built on the same core, so a strategy behaves the same in backtests and live.

## Run a backtest

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

- `src/BitmexBot.Core`: streaming indicators (SMA, EMA, RSI, MACD, with `Peek` for the forming bar), the EMA/SMA cross strategy, the backtester, and the BitMEX candle downloader.
- `src/BitmexBot.Backtest`: command line runner.
- `tests/BitmexBot.Core.Tests`: run with `dotnet test`.
