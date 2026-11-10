// =====================================================================================
// AutoHedgeScalper.cs - cTrader Automate (cAlgo) cBot
// Fully Dynamic Hedged Basket Scalper.
//
// This bot calculates its own lot sizes and profit targets based on your account balance
// and market volatility. It mimics the high-frequency "basket" behavior.
//
// SAFE DEFAULTS: Run on DEMO first. The bot manages margin aggressively.
// =====================================================================================
using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class AutoHedgeScalper : Robot
    {
        // ---------------- Safety ----------------
        [Parameter("Allow Live Account", DefaultValue = false, Group = "Safety")]
        public bool AllowLiveAccount { get; set; }

        [Parameter("Verbose log", DefaultValue = true, Group = "Safety")]
        public bool Verbose { get; set; }

        // ---------------- Auto-Sizing Settings ----------------
        [Parameter("Risk % of Balance per Trade", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 2.0, Step = 0.1, Group = "Auto-Sizing")]
        public double RiskPercentPerTrade { get; set; }

        [Parameter("Target Basket Profit %", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 10.0, Step = 0.5, Group = "Auto-Sizing")]
        public double TargetBasketProfitPercent { get; set; }

        [Parameter("Max Basket Loss %", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 20.0, Step = 0.5, Group = "Auto-Sizing")]
        public double MaxBasketLossPercent { get; set; }

        // ---------------- Strategy Settings ----------------
        [Parameter("Base Trades per Basket", DefaultValue = 2, MinValue = 1, MaxValue = 4, Group = "Strategy")]
        public int BaseTradesPerBasket { get; set; }

        [Parameter("Max Spread (pips)", DefaultValue = 1.5, MinValue = 0.1, Step = 0.1, Group = "Strategy")]
        public double MaxSpreadPips { get; set; }

        [Parameter("Cooldown between baskets (seconds)", DefaultValue = 60, MinValue = 10, Group = "Strategy")]
        public int CooldownSeconds { get; set; }

        // ---------------- Internals ----------------
        private string _label;
        private bool _basketOpen = false;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private DateTime _lastScan = DateTime.MinValue;

        private Bars _m1;
        private ExponentialMovingAverage _fastEma;
        private ExponentialMovingAverage _slowEma;
        private AverageTrueRange _atr;

        protected override void OnStart()
        {
            _label = "AutoHedge";

            if (Account.IsLive && !AllowLiveAccount)
            {
                Print("BLOCKED: Live account and Allow Live Account = false. Stopping.");
                Stop();
                return;
            }

            _m1 = MarketData.GetBars(TimeFrame.Minute);
            _fastEma = Indicators.ExponentialMovingAverage(_m1.ClosePrices, 9);
            _slowEma = Indicators.ExponentialMovingAverage(_m1.ClosePrices, 21);
            _atr = Indicators.AverageTrueRange(_m1, 14, MovingAverageType.Simple);

            Print("AutoHedgeScalper Started. Calculating dynamic sizes and targets...");
        }

        protected override void OnTick()
        {
            DateTime now = Server.Time;

            // 1. Manage open basket
            if (_basketOpen)
            {
                ManageBasket(now);
                return;
            }

            // 2. Cooldown
            if (now < _cooldownUntil) return;

            // 3. Scan for entry
            if ((now - _lastScan).TotalSeconds < 1) return;
            _lastScan = now;

            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            if (spreadPips > MaxSpreadPips) return;

            int confidence = CalculateConfidence();
            if (confidence >= 80)
            {
                FireBasket(now, confidence);
            }
        }

        private int CalculateConfidence()
        {
            if (_m1.Count < 30) return 0;

            // Dynamic Confidence Scoring (0 to 100)
            // Measures momentum (EMA gap) and recent candle strength
            double fast = _fastEma.Result.Last(1);
            double slow = _slowEma.Result.Last(1);
            double atr = _atr.Result.Last(1);

            if (atr <= 0) return 0;

            double emaGapPips = Math.Abs(fast - slow) / Symbol.PipSize;
            double atrPips = atr / Symbol.PipSize;

            // Score 1: EMA separation (max 50 points)
            int score = (int)Math.Min(50, (emaGapPips / (atrPips * 0.5)) * 50);

            // Score 2: Candle strength (max 50 points)
            double lastCandleMove = Math.Abs(_m1.ClosePrices.Last(1) - _m1.OpenPrices.Last(1)) / Symbol.PipSize;
            score += (int)Math.Min(50, (lastCandleMove / atrPips) * 50);

            return Math.Min(100, score);
        }

        private void FireBasket(DateTime now, int confidence)
        {
            // Dynamic Sizing: Calculate lot size based on Risk % and current ATR
            double atrPips = _atr.Result.Last(1) / Symbol.PipSize;
            if (atrPips <= 0) return;

            // We risk RiskPercentPerTrade of balance. Stop loss is assumed to be 3x ATR.
            double riskMoney = Account.Balance * (RiskPercentPerTrade / 100.0);
            double assumedStopLossPips = atrPips * 3.0;

            double volumeUnits = riskMoney / (assumedStopLossPips * Symbol.PipValue);
            double volume = Symbol.NormalizeVolumeInUnits(volumeUnits, RoundingMode.Down);

            if (volume < Symbol.VolumeInUnitsMin)
            {
                if (Verbose) Print("Skipped: Calculated volume too small.");
                return;
            }

            // Dynamic Trade Count: 80% = 2 trades, 90% = 3 trades, 100% = 4 trades
            int tradesPerSide = BaseTradesPerBasket;
            if (confidence >= 90) tradesPerSide += 1;
            if (confidence >= 95) tradesPerSide += 1;

            _basketOpen = true;
            Print("FIRING BASKET (Confidence {0}%): {1} Buys, {1} Sells @ {2:F2} lots each.",
                confidence, tradesPerSide, volume / Symbol.QuantityToVolumeInUnits(1.0));

            for (int i = 0; i < tradesPerSide; i++)
            {
                ExecuteMarketOrder(TradeType.Buy, SymbolName, volume, _label);
                ExecuteMarketOrder(TradeType.Sell, SymbolName, volume, _label);
            }
        }

        private void ManageBasket(DateTime now)
        {
            Position[] positions = Positions.FindAll(_label, SymbolName);

            if (positions.Length == 0)
            {
                _basketOpen = false;
                _cooldownUntil = now.AddSeconds(CooldownSeconds);
                return;
            }

            double totalNetProfit = 0;
            foreach (var p in positions)
            {
                totalNetProfit += p.NetProfit;
            }

            // Dynamic Targets based on current Account Balance
            double targetProfitUSD = Account.Balance * (TargetBasketProfitPercent / 100.0);
            double maxLossUSD = Account.Balance * (MaxBasketLossPercent / 100.0);

            if (totalNetProfit >= targetProfitUSD)
            {
                Print("BASKET TARGET HIT: +${0:F2}. Closing all.", totalNetProfit);
                CloseAllBasket(now);
            }
            else if (totalNetProfit <= -maxLossUSD)
            {
                Print("BASKET STOP LOSS HIT: -${0:F2}. Closing all.", Math.Abs(totalNetProfit));
                CloseAllBasket(now);
            }
        }

        private void CloseAllBasket(DateTime now)
        {
            Position[] positions = Positions.FindAll(_label, SymbolName);
            foreach (var p in positions)
            {
                ClosePosition(p);
            }
            _basketOpen = false;
            _cooldownUntil = now.AddSeconds(CooldownSeconds);
        }
    }
}