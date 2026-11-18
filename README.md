# Deriv cTrader Bots

This repository contains two separate cTrader cBots. The weekday AutoHedgeScalper strategy has been replaced with a fresh multi-timeframe market-structure scalping strategy. WeekendSweepReclaim remains a separate bot and is not part of that replacement.

## 1. AutoHedgeScalper — Multi-Timeframe Structure Scalper

The only entry strategy in AutoHedgeScalper is:

- **M15:** identify bullish, bearish, or ranging market structure from swing highs and lows.
- **M10:** identify support, resistance, rising support trendlines, and falling resistance trendlines.
- **M5 and M1:** detect price approaching or touching those areas.
- **Synthetic 30-second candle:** aggregate live quotes into 30-second candles and wait for a bullish or bearish confirmation candle.
- **Reversals:** buy confirmed support/trendline-support reactions and sell confirmed resistance/trendline-resistance reactions.
- **Breakouts:** detect a completed M1/M5 close beyond a level, wait for a retest, and require 30-second confirmation in the breakout direction.
- **Position management:** close at the next structural target when reached, or no later than 8 seconds after entry.

The old EMA, RSI, confidence-score, spread-cost, and session/day entry gates have been removed from AutoHedgeScalper. Structural conditions are the entry logic.

### Markets

The Markets to scan (broker symbol names) parameter is a comma-separated watchlist. The default is:

- XAUUSD
- EURUSD
- GBPUSD
- USDJPY
- US100
- US500
- US30

Add the exact stock and futures/CFD symbol names shown by the connected cTrader broker. Symbol names vary by broker; unavailable names are reported in the cBot log and skipped. The bot can only trade instruments made available by the connected broker.

### Safety and execution

- Default maximum simultaneous positions: 7.
- Default maximum holding time: 8 seconds (parameter cannot be set above 8).
- Default position size: 0.01 lots, capped by the Max Lots parameter.
- Live-account execution is blocked unless Allow Live Account is explicitly enabled.
- Test on a Deriv demo account first. A successful compile does not prove profitability or guarantee broker execution.

## 2. WeekendSweepReclaim — separate weekend crypto bot

WeekendSweepReclaim is a separate BTC/ETH weekend strategy. It is unchanged by the AutoHedgeScalper strategy replacement.

## Build

GitHub Actions builds both projects using the cTrader Automate NuGet package and uploads the compiled .algo files and matching source files as a workflow artifact.
