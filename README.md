# Deriv cTrader Bots — Forager Weekday + Weekend Sweep & Reclaim

This repository now contains the two current cTrader cBots. The previous multi-market M5 strategy has been replaced and is no longer part of this project.

## Bots

### 1. ForagerScalper — weekdays
- M1 multi-symbol scalper.
- EMA 9/21 cross, EMA 50 trend filter, and RSI confirmation.
- ATR-based stop loss, 1:2 reward-to-risk target, and 15-minute time exit.
- Ranks its configured watchlist and focuses on the top eligible symbols.
- The default watchlist is FX, gold, and indices; crypto is excluded.
- Trading is gated off on Saturday and Sunday.

### 2. WeekendSweepReclaim — weekends
- Crypto-only BTC/ETH sweep-and-reclaim strategy.
- M5 trigger timeframe by default.
- Uses the prior 12-hour range and looks for failed breakouts that reclaim the range.
- Maximum three trades per day by default.
- Partial take profit at 1R and final target at 2R by default.
- Trading is gated to Saturday and Sunday.
- Funding and open-interest inputs are manual parameters because cTrader does not provide those exchange feeds natively.

## Safety defaults
- Both bots start with Dry Run = true.
- Both bots default to Allow Live Account = false.
- Keep both on a Deriv demo account while checking symbol names, spreads, order sizing, and trade management.
- A successful compile does not prove profitability or guarantee broker execution.
- The exact symbol names available in the connected Deriv cTrader account must be checked before disabling Dry Run.

## Build
GitHub Actions builds both projects using the official cTrader Automate NuGet package and uploads the resulting .algo packages as a workflow artifact named Forager-Weekday-and-Weekend-cBots.

The .cs files are editable source code; the .algo files are the compiled packages to import into cTrader.
