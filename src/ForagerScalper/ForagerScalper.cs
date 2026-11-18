using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class AutoHedgeScalper : Robot
    {
        [Parameter("Markets to scan (broker symbol names)", DefaultValue = "XAUUSD,BTCUSD,ADAUSD,ALGUSD,AVAUSD,BATUSD,BCHUSD,BNBUSD,BTCETH,BTCLTC,DOGUSD,DOTUSD,DSHUSD,ETCUSD,FILUSD,IOTUSD,LINKUSD,LTCUSD,AUDJPY,AUDUSD,EURAUD,EURCAD,EURCHF,EURGBP,EURJPY,EURUSD,GBPAUD,GBPJPY,GBPUSD,USDCAD,USDCHF,USDJPY,XAGEUR,XAGUSD,XPDUSD,XPTUSD,XCUUSD", Group = "Markets")]
        public string MarketsToScan { get; set; }

        [Parameter("Maximum open positions", DefaultValue = 7, MinValue = 1, MaxValue = 20, Group = "Execution")]
        public int MaxOpenPositions { get; set; }

        [Parameter("Maximum holding time (seconds)", DefaultValue = 8, MinValue = 1, MaxValue = 8, Group = "Execution")]
        public int MaxHoldSeconds { get; set; }

        [Parameter("Lots per position", DefaultValue = 0.01, MinValue = 0.001, Step = 0.001, Group = "Risk")]
        public double LotsPerPosition { get; set; }

        [Parameter("Maximum lots per position", DefaultValue = 0.50, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double MaxLots { get; set; }

        [Parameter("Dry run (no broker orders)", DefaultValue = false, Group = "Safety")]
        public bool DryRun { get; set; }

        [Parameter("Allow live account", DefaultValue = false, Group = "Safety")]
        public bool AllowLiveAccount { get; set; }

        [Parameter("Pivot lookback bars", DefaultValue = 80, MinValue = 30, MaxValue = 250, Group = "Strategy")]
        public int PivotLookback { get; set; }

        [Parameter("Zone tolerance (% of 10m range)", DefaultValue = 20, MinValue = 5, MaxValue = 50, Group = "Strategy")]
        public int ZoneTolerancePercent { get; set; }

        [Parameter("Stop buffer (% of 10m range)", DefaultValue = 10, MinValue = 3, MaxValue = 30, Group = "Strategy")]
        public int StopBufferPercent { get; set; }

        private const string BotLabel = "AutoHedgeScalper_MTF";
        private const int PivotStrength = 2;
        private static readonly TimeSpan SyntheticCandlePeriod = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(250);

        private sealed class Candle30
        {
            public DateTime Start;
            public double Open;
            public double High;
            public double Low;
            public double Close;
        }

        private sealed class Pivot
        {
            public int Index;
            public double Price;
        }

        private sealed class MarketState
        {
            public Symbol Symbol;
            public Bars M1;
            public Bars M5;
            public Bars M10;
            public Bars M15;
            public Candle30 Building30;
            public Candle30 Previous30;
            public Candle30 LastClosed30;
            public DateTime LastEvaluated30 = DateTime.MinValue;
            public DateTime LastEntryTime = DateTime.MinValue;
            public TradeType? PendingBreakoutSide;
            public double PendingBreakoutLevel;
            public DateTime PendingBreakoutTime = DateTime.MinValue;
        }

        private sealed class Signal
        {
            public TradeType Side;
            public double StopPrice;
            public double TargetPrice;
            public string Reason;
        }

        private readonly List<MarketState> _markets = new List<MarketState>();
        private readonly Dictionary<long, double> _positionTargets = new Dictionary<long, double>();

        protected override void OnStart()
        {
            if (Account.IsLive && !AllowLiveAccount)
            {
                Print("BLOCKED: this cBot is set to demo-only. Allow Live Account is false.");
                Stop();
                return;
            }

            string[] names = (MarketsToScan ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string name in names)
            {
                Symbol symbol = Symbols.GetSymbol(name);
                if (symbol == null)
                {
                    Print("MARKET NOT FOUND: {0}. Check the exact name in your cTrader broker.", name);
                    continue;
                }

                try
                {
                    var state = new MarketState
                    {
                        Symbol = symbol,
                        M1 = MarketData.GetBars(TimeFrame.Minute, name),
                        M5 = MarketData.GetBars(TimeFrame.Minute5, name),
                        M10 = MarketData.GetBars(TimeFrame.Minute10, name),
                        M15 = MarketData.GetBars(TimeFrame.Minute15, name)
                    };

                    _markets.Add(state);
                    Print("SCANNING {0}: M15 structure / M10 levels / M5+M1 approach / synthetic 30-second confirmation.",
                        name);
                }
                catch (Exception ex)
                {
                    Print("COULD NOT LOAD {0}: {1}", name, ex.Message);
                }
            }

            if (_markets.Count == 0)
            {
                Print("No markets loaded. Edit Markets to scan using the exact symbol names shown by your broker.");
                Stop();
                return;
            }

            Positions.Closed += OnPositionClosed;
            Timer.Start(ScanInterval);
            Print("NEW MTF STRUCTURE SCALPER STARTED. Markets loaded: {0}. Maximum hold: {1}s.",
                _markets.Count, MaxHoldSeconds);
        }

        protected override void OnTimer()
        {
            DateTime now = Server.Time;

            foreach (MarketState market in _markets)
            {
                UpdateSynthetic30(market, now);
                ManagePositions(market, now);

                if (market.LastClosed30 == null || market.LastClosed30.Start == market.LastEvaluated30)
                    continue;

                market.LastEvaluated30 = market.LastClosed30.Start;

                int openCount = _markets.Sum(x => Positions.FindAll(BotLabel, x.Symbol.Name).Length);

                if (openCount >= MaxOpenPositions)
                    continue;

                if (Positions.FindAll(BotLabel, market.Symbol.Name).Length > 0)
                    continue;

                Signal signal = FindSignal(market, now);
                if (signal != null)
                    OpenPosition(market, signal, now);
                else
                    Print("{0}: scanned latest 30-second close; no confirmed setup yet.", market.Symbol.Name);
            }
        }

        private void UpdateSynthetic30(MarketState market, DateTime now)
        {
            double bid = market.Symbol.Bid;
            double ask = market.Symbol.Ask;
            if (bid <= 0 || ask <= 0)
                return;

            double mid = (bid + ask) / 2.0;
            if (market.Building30 == null)
            {
                market.Building30 = new Candle30
                {
                    Start = now,
                    Open = mid,
                    High = mid,
                    Low = mid,
                    Close = mid
                };
                return;
            }

            if ((now - market.Building30.Start) >= SyntheticCandlePeriod)
            {
                market.Previous30 = market.LastClosed30;
                market.LastClosed30 = market.Building30;
                market.Building30 = new Candle30
                {
                    Start = now,
                    Open = mid,
                    High = mid,
                    Low = mid,
                    Close = mid
                };
                return;
            }

            market.Building30.High = Math.Max(market.Building30.High, mid);
            market.Building30.Low = Math.Min(market.Building30.Low, mid);
            market.Building30.Close = mid;
        }

        private Signal FindSignal(MarketState m, DateTime now)
        {
            if (m.M1.Count < 10 || m.M5.Count < 10 || m.M10.Count < 20 || m.M15.Count < 20)
                return null;

            double price = (m.Symbol.Bid + m.Symbol.Ask) / 2.0;
            double averageRange = AverageRange(m.M10, 14);
            if (averageRange <= 0)
                return null;

            double tolerance = Math.Max(m.Symbol.PipSize * 3.0,
                averageRange * ZoneTolerancePercent / 100.0);
            double buffer = Math.Max(m.Symbol.PipSize * 2.0,
                averageRange * StopBufferPercent / 100.0);

            string structure = ReadMarketStructure(m.M15);
            List<Pivot> highs = FindPivots(m.M10, true);
            List<Pivot> lows = FindPivots(m.M10, false);
            if (highs.Count == 0 || lows.Count == 0)
                return null;

            double support;
            double resistance;
            FindNearestLevels(highs, lows, price, out support, out resistance);

            double trendSupport;
            double trendResistance;
            FindTrendlines(highs, lows, m.M10.Count - 1, out trendSupport, out trendResistance);

            bool touchedSupport = IsNear(price, support, tolerance)
                && (CandleTouches(m.M1, support, tolerance) || CandleTouches(m.M5, support, tolerance));
            bool touchedResistance = IsNear(price, resistance, tolerance)
                && (CandleTouches(m.M1, resistance, tolerance) || CandleTouches(m.M5, resistance, tolerance));

            bool bullish30 = IsBullishConfirmation(m);
            bool bearish30 = IsBearishConfirmation(m);

            // Reversal at support or resistance: enter against the approach after the 30-second confirmation.
            if (touchedSupport && bullish30)
            {
                double target = resistance;
                if (target > price)
                    return MakeSignal(m, TradeType.Buy, support - buffer, target, "Support rejection; " + structure);
            }

            if (touchedResistance && bearish30)
            {
                double target = support;
                if (target > 0 && target < price)
                    return MakeSignal(m, TradeType.Sell, resistance + buffer, target, "Resistance rejection; " + structure);
            }

            // Trendline reactions use the same 30-second confirmation.
            if (IsNear(price, trendSupport, tolerance)
                && (CandleTouches(m.M1, trendSupport, tolerance) || CandleTouches(m.M5, trendSupport, tolerance))
                && bullish30)
            {
                double target = resistance;
                if (target > price)
                    return MakeSignal(m, TradeType.Buy, trendSupport - buffer, target, "Rising trendline rejection; " + structure);
            }

            if (IsNear(price, trendResistance, tolerance)
                && (CandleTouches(m.M1, trendResistance, tolerance) || CandleTouches(m.M5, trendResistance, tolerance))
                && bearish30)
            {
                double target = support;
                if (target > 0 && target < price)
                    return MakeSignal(m, TradeType.Sell, trendResistance + buffer, target, "Falling trendline rejection; " + structure);
            }

            // Detect a break on a completed M1 or M5 candle, then wait for a retest and 30-second confirmation.
            UpdateBreakoutState(m, highs, lows, buffer, now);

            if (m.PendingBreakoutSide.HasValue)
            {
                if ((now - m.PendingBreakoutTime).TotalMinutes > 30)
                {
                    m.PendingBreakoutSide = null;
                }
                else if ((now - m.PendingBreakoutTime).TotalSeconds >= 30
                    && IsNear(price, m.PendingBreakoutLevel, tolerance))
                {
                    if (m.PendingBreakoutSide.Value == TradeType.Buy && bullish30)
                    {
                        double target = FindNextLevelAbove(highs, price);
                        if (target <= price)
                            return null;
                        Signal signal = MakeSignal(m, TradeType.Buy, m.PendingBreakoutLevel - buffer,
                            target, "Resistance breakout retest; " + structure);
                        if (signal != null)
                            m.PendingBreakoutSide = null;
                        return signal;
                    }

                    if (m.PendingBreakoutSide.Value == TradeType.Sell && bearish30)
                    {
                        double target = FindNextLevelBelow(lows, price);
                        if (target <= 0 || target >= price)
                            return null;
                        Signal signal = MakeSignal(m, TradeType.Sell, m.PendingBreakoutLevel + buffer,
                            target, "Support breakdown retest; " + structure);
                        if (signal != null)
                            m.PendingBreakoutSide = null;
                        return signal;
                    }
                }
            }

            return null;
        }

        private void UpdateBreakoutState(MarketState m, List<Pivot> highs, List<Pivot> lows,
            double buffer, DateTime now)
        {
            if (m.PendingBreakoutSide.HasValue || m.M1.Count < 4 || m.M5.Count < 4)
                return;

            double previousM1 = m.M1.ClosePrices[m.M1.Count - 3];
            double currentM1 = m.M1.ClosePrices[m.M1.Count - 2];
            double previousM5 = m.M5.ClosePrices[m.M5.Count - 3];
            double currentM5 = m.M5.ClosePrices[m.M5.Count - 2];

            double priorSupport;
            double priorResistance;
            FindNearestLevels(highs, lows, previousM1, out priorSupport, out priorResistance);

            bool brokeUp = priorResistance > 0
                && ((previousM1 <= priorResistance + buffer && currentM1 > priorResistance + buffer)
                    || (previousM5 <= priorResistance + buffer && currentM5 > priorResistance + buffer));
            bool brokeDown = priorSupport > 0
                && ((previousM1 >= priorSupport - buffer && currentM1 < priorSupport - buffer)
                    || (previousM5 >= priorSupport - buffer && currentM5 < priorSupport - buffer));

            if (brokeUp)
            {
                m.PendingBreakoutSide = TradeType.Buy;
                m.PendingBreakoutLevel = priorResistance;
                m.PendingBreakoutTime = now;
            }
            else if (brokeDown)
            {
                m.PendingBreakoutSide = TradeType.Sell;
                m.PendingBreakoutLevel = priorSupport;
                m.PendingBreakoutTime = now;
            }
        }

        private Signal MakeSignal(MarketState m, TradeType side, double stopPrice,
            double targetPrice, string reason)
        {
            double entry = side == TradeType.Buy ? m.Symbol.Ask : m.Symbol.Bid;
            double stopPips = side == TradeType.Buy
                ? (entry - stopPrice) / m.Symbol.PipSize
                : (stopPrice - entry) / m.Symbol.PipSize;
            double targetPips = side == TradeType.Buy
                ? (targetPrice - entry) / m.Symbol.PipSize
                : (entry - targetPrice) / m.Symbol.PipSize;

            if (stopPips <= 0 || targetPips <= 0)
                return null;

            return new Signal
            {
                Side = side,
                StopPrice = stopPrice,
                TargetPrice = targetPrice,
                Reason = reason
            };
        }

        private void OpenPosition(MarketState m, Signal signal, DateTime now)
        {
            double entry = signal.Side == TradeType.Buy ? m.Symbol.Ask : m.Symbol.Bid;
            double stopPips = signal.Side == TradeType.Buy
                ? (entry - signal.StopPrice) / m.Symbol.PipSize
                : (signal.StopPrice - entry) / m.Symbol.PipSize;
            double targetPips = signal.Side == TradeType.Buy
                ? (signal.TargetPrice - entry) / m.Symbol.PipSize
                : (entry - signal.TargetPrice) / m.Symbol.PipSize;

            if (stopPips <= 0 || targetPips <= 0)
                return;

            double lots = Math.Min(LotsPerPosition, MaxLots);
            double volume = m.Symbol.QuantityToVolumeInUnits(lots);
            volume = m.Symbol.NormalizeVolumeInUnits(volume, RoundingMode.Down);
            if (volume < m.Symbol.VolumeInUnitsMin)
            {
                Print("{0}: calculated volume is below broker minimum; no order sent.", m.Symbol.Name);
                return;
            }

            if (DryRun)
            {
                Print("[DRY RUN] {0} {1} {2} | SL {3:F1} pips | target {4:F1} pips | {5}",
                    m.Symbol.Name, signal.Side, lots, stopPips, targetPips, signal.Reason);
                m.LastEntryTime = now;
                return;
            }

            TradeResult result = ExecuteMarketOrder(signal.Side, m.Symbol.Name, volume,
                BotLabel, stopPips, targetPips);

            if (!result.IsSuccessful)
            {
                Print("{0}: order rejected: {1}", m.Symbol.Name, result.Error);
                return;
            }

            m.LastEntryTime = now;
            if (result.Position != null)
                _positionTargets[result.Position.Id] = signal.TargetPrice;

            Print("OPENED {0} {1} at {2} | SL {3:F1} pips | target {4:F1} pips | max hold {5}s | {6}",
                m.Symbol.Name, signal.Side, result.Position.EntryPrice, stopPips, targetPips,
                MaxHoldSeconds, signal.Reason);
        }

        private void ManagePositions(MarketState m, DateTime now)
        {
            double targetBuffer = Math.Max(m.Symbol.PipSize,
                AverageRange(m.M10, 14) * 0.05);

            foreach (Position position in Positions.FindAll(BotLabel, m.Symbol.Name))
            {
                double target;
                bool targetHit = _positionTargets.TryGetValue(position.Id, out target)
                    && (position.TradeType == TradeType.Buy
                        ? m.Symbol.Bid >= target - targetBuffer
                        : m.Symbol.Ask <= target + targetBuffer);

                bool timeExpired = (now - position.EntryTime).TotalSeconds >= MaxHoldSeconds;
                if (targetHit || timeExpired)
                {
                    ClosePosition(position);
                    _positionTargets.Remove(position.Id);
                }
            }
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.Label == BotLabel)
                _positionTargets.Remove(args.Position.Id);
        }

        private bool IsBullishConfirmation(MarketState m)
        {
            return m.LastClosed30 != null && m.LastClosed30.Close > m.LastClosed30.Open;
        }

        private bool IsBearishConfirmation(MarketState m)
        {
            return m.LastClosed30 != null && m.LastClosed30.Close < m.LastClosed30.Open;
        }

        private string ReadMarketStructure(Bars bars)
        {
            List<Pivot> highs = FindPivots(bars, true);
            List<Pivot> lows = FindPivots(bars, false);
            if (highs.Count < 2 || lows.Count < 2)
                return "structure forming";

            Pivot h1 = highs[highs.Count - 2];
            Pivot h2 = highs[highs.Count - 1];
            Pivot l1 = lows[lows.Count - 2];
            Pivot l2 = lows[lows.Count - 1];

            if (h2.Price > h1.Price && l2.Price > l1.Price)
                return "bullish structure";
            if (h2.Price < h1.Price && l2.Price < l1.Price)
                return "bearish structure";
            return "ranging structure";
        }

        private List<Pivot> FindPivots(Bars bars, bool highs)
        {
            var pivots = new List<Pivot>();
            int first = Math.Max(PivotStrength, bars.Count - PivotLookback);
            int last = bars.Count - PivotStrength - 2;

            for (int i = first; i <= last; i++)
            {
                double value = highs ? bars.HighPrices[i] : bars.LowPrices[i];
                bool isPivot = true;

                for (int j = 1; j <= PivotStrength; j++)
                {
                    double left = highs ? bars.HighPrices[i - j] : bars.LowPrices[i - j];
                    double right = highs ? bars.HighPrices[i + j] : bars.LowPrices[i + j];

                    if (highs ? (value <= left || value <= right) : (value >= left || value >= right))
                    {
                        isPivot = false;
                        break;
                    }
                }

                if (isPivot)
                    pivots.Add(new Pivot { Index = i, Price = value });
            }

            return pivots;
        }

        private void FindNearestLevels(List<Pivot> highs, List<Pivot> lows,
            double price, out double support, out double resistance)
        {
            support = 0;
            resistance = 0;

            foreach (Pivot p in lows)
                if (p.Price < price && p.Price > support)
                    support = p.Price;

            foreach (Pivot p in highs)
                if (p.Price > price && (resistance == 0 || p.Price < resistance))
                    resistance = p.Price;
        }

        private void FindTrendlines(List<Pivot> highs, List<Pivot> lows, int currentIndex,
            out double risingSupport, out double fallingResistance)
        {
            risingSupport = 0;
            fallingResistance = 0;

            if (lows.Count >= 2)
            {
                Pivot a = lows[lows.Count - 2];
                Pivot b = lows[lows.Count - 1];
                if (b.Index > a.Index && b.Price > a.Price)
                    risingSupport = ProjectLine(a, b, currentIndex);
            }

            if (highs.Count >= 2)
            {
                Pivot a = highs[highs.Count - 2];
                Pivot b = highs[highs.Count - 1];
                if (b.Index > a.Index && b.Price < a.Price)
                    fallingResistance = ProjectLine(a, b, currentIndex);
            }
        }

        private double ProjectLine(Pivot a, Pivot b, int currentIndex)
        {
            double slope = (b.Price - a.Price) / (b.Index - a.Index);
            return b.Price + slope * (currentIndex - b.Index);
        }

        private double FindNextLevelAbove(List<Pivot> highs, double price)
        {
            double level = 0;
            foreach (Pivot p in highs)
                if (p.Price > price && (level == 0 || p.Price < level))
                    level = p.Price;
            return level;
        }

        private double FindNextLevelBelow(List<Pivot> lows, double price)
        {
            double level = 0;
            foreach (Pivot p in lows)
                if (p.Price < price && (level == 0 || p.Price > level))
                    level = p.Price;
            return level;
        }

        private bool CandleTouches(Bars bars, double level, double tolerance)
        {
            if (level <= 0 || bars.Count < 3)
                return false;

            for (int i = bars.Count - 2; i >= Math.Max(1, bars.Count - 3); i--)
                if (bars.LowPrices[i] <= level + tolerance
                    && bars.HighPrices[i] >= level - tolerance)
                    return true;

            return false;
        }

        private double AverageRange(Bars bars, int period)
        {
            if (bars.Count < period + 2)
                return 0;

            double total = 0;
            for (int i = bars.Count - 2; i >= bars.Count - 1 - period; i--)
                total += bars.HighPrices[i] - bars.LowPrices[i];

            return total / period;
        }

        private bool IsNear(double price, double level, double tolerance)
        {
            return level > 0 && Math.Abs(price - level) <= tolerance;
        }

        protected override void OnStop()
        {
            Timer.Stop();
            Positions.Closed -= OnPositionClosed;
            Print("AutoHedgeScalper stopped.");
        }
    }
}