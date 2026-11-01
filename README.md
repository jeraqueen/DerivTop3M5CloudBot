# Deriv Top 3 M5 Cloud Bot

Independent cTrader Automate cBot project for demo testing.

## Intended markets
- BTCUSD
- ETHUSD
- Volatility 5 Index
- Volatility 10 Index
- Volatility 15 Index
- Volatility 15 (1s) Index
- Volatility 25 Index

The cBot must validate that each exact symbol is available in the connected cTrader account. It must not silently substitute symbols or discover additional markets.

## Strategies
Two independent engines per market, each with its own position tracking and exit logic.

### 75% candle-direction engine
- Observe the forming M5 candle from its opening.
- Determine its live direction during the first 30–60 seconds.
- BUY when the candle is green; SELL when red.
- Enter no later than 60 seconds after the candle opens, using a deterministic direction decision.
- Close at the end of that same originating M5 candle.
- Do not re-enter that strategy on the same candle.

### 25% pullback engine
- Evaluate direction from the live M5 candle at its opening.
- BUY for upward development; SELL for downward development.
- Track the favourable extreme and the candle's developing high-to-low range.
- Close when price retraces 25% of the developing range from the favourable extreme.
- Do not re-enter that strategy on the same candle.

## Trade ownership
Each strategy and symbol has independent position ownership. A strategy may only manage and close positions it opened. No cross-strategy or cross-symbol position management.

## Risk and exits
- No stop-loss is part of the requested strategy.
- The 75% engine uses the originating candle-end exit.
- The 25% engine uses the specified pullback exit.
- Demo testing only until extensive validation is completed.
- No guarantee of execution at a desired price or of closure during connectivity/broker failures.

## Scope restrictions
No legacy strategy/filter code, automatic symbol discovery, extra markets, or extra timeframes. No ATR, spread, market-hours, volatility, or candle-reversal signal filters. Keep only essential broker/order validity and account-margin checks.

## Important implementation note
The exact broker symbol names and availability must be checked in the user's cTrader account. In particular, do not assume every Deriv synthetic index is offered on Deriv cTrader merely because it exists on another Deriv platform.

The 75% direction decision must be deterministic and based only on price data available at decision time. A candle's final colour cannot be known in advance.
