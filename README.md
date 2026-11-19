# Deriv cTrader Bots

This repository contains two separate cTrader cBots. The weekday AutoHedgeScalper strategy has been replaced with a fresh multi-timeframe market-structure scalping strategy. WeekendSweepReclaim remains a separate bot and is not part of that replacement.

## 1. AutoHedgeScalper — Multi-Timeframe Structure Scalper

The only entry strategy in AutoHedgeScalper is:

- **M15:** identify bullish, bearish, or ranging market structure from swing highs and lows.
- **M10:** identify support, resistance, rising support trendlines, and falling resistance trendlines.
- **M5 and M1:** detect price approaching or touching those areas.
- **Synthetic one-minute candle:** aggregate live quotes into one-minute candles and wait for a bullish or bearish confirmation candle.
- **Reversals:** buy confirmed support/trendline-support reactions and sell confirmed resistance/trendline-resistance reactions.
- **Breakouts:** detect a completed M1/M5 close beyond a level, wait for a retest, and require one-minute confirmation in the breakout direction.
- **Position management:** each bot-managed position is monitored independently and the bot sends a close request when its net profit reaches the configured account-currency target (default $1.00). There is no time-based exit and no automatic stop loss.

The old EMA, RSI, confidence-score, spread-cost, and session/day entry gates have been removed from AutoHedgeScalper. Structural conditions are the entry logic.

### Markets

The Markets to scan (broker symbol names) parameter is a comma-separated watchlist. The updated default watchlist uses symbols visible in the supplied broker screenshots, plus BTCUSD requested by the user:

- **Crypto:** BTCUSD, ADAUSD, ALGUSD, AVAUSD, BATUSD, BCHUSD, BNBUSD, BTCETH, BTCLTC, DOGUSD, DOTUSD, DSHUSD, ETCUSD, FILUSD, IOTUSD, LINKUSD, LTCUSD
- **Forex:** AUDJPY, AUDUSD, EURAUD, EURCAD, EURCHF, EURGBP, EURJPY, EURUSD, GBPAUD, GBPJPY, GBPUSD, USDCAD, USDCHF, USDJPY
- **Metals:** XAUUSD, XAGEUR, XAGUSD, XPDUSD, XPTUSD, XCUUSD

US100, US500, and US30 have been removed because they are not listed by this broker. Symbol names vary by broker; unavailable names are reported in the cBot log and skipped. BTCUSD was explicitly requested but is not visible in the screenshots, so confirm it loads in the cBot log. The bot can only trade instruments made available by the connected broker.

### Safety and execution

- Default maximum simultaneous positions: 7.
- No automatic holding-time exit. Positions remain open until the configured net-profit target is reached or the user closes them manually.
- Default position size: 0.01 lots, capped by the Max Lots parameter.
- Dry Run defaults to false so demo orders are sent; Allow Live Account defaults to false, blocking live accounts.
- The scanner rechecks an eligible setup every 2 seconds after a completed synthetic one-minute confirmation candle, so it can re-enter after a manual close if the setup remains valid.
- Existing positions are profit-monitored before market scanning; failed close requests are retried and diagnostics are printed to the cBot log.
- Test on a Deriv demo account first. A successful compile does not prove profitability or guarantee broker execution.

## 2. WeekendSweepReclaim — separate weekend crypto bot

WeekendSweepReclaim is a separate BTC/ETH weekend strategy. It is unchanged by the AutoHedgeScalper strategy replacement.

## Build

GitHub Actions builds both projects using the cTrader Automate NuGet package and uploads the compiled .algo files and matching source files as a workflow artifact.
