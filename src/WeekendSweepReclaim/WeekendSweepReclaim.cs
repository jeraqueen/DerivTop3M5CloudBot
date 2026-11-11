// =====================================================================================
// WeekendSweepReclaim.cs - Multi-market continuous stream scalper
// UPDATED: True Hedging, Dynamic Auto-Sizing, and EAT Time-Based Pair Filters.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class WeekendSweepReclaim : Robot
    {
        [Parameter("Dry Run (simulate only)", DefaultValue = false, Group = "Safety")]
        public bool DryRun { get; set; }
        [Parameter("Allow Live Account", DefaultValue = false, Group = "Safety")]
        public bool AllowLiveAccount { get; set; }
        [Parameter("Verbose log", DefaultValue = true, Group = "Safety")]
        public bool Verbose { get; set; }

        [Parameter("Markets to scan", DefaultValue = "EURUSD,GBPUSD,USDJPY,AUDUSD,USDCAD,USDCHF,BTCUSD,ETHUSD", Group = "Strategy")]
        public string MarketsToScan { get; set; }
        [Parameter("Min confidence %", DefaultValue = 70, MinValue = 40, MaxValue = 100, Group = "Strategy")]
        public int MinConfidence { get; set; }
        [Parameter("Max open trades at once", DefaultValue = 7, MinValue = 1, MaxValue = 15, Group = "Strategy")]
        public int MaxOpen { get; set; }
        [Parameter("Seconds between entries", DefaultValue = 2, MinValue = 1, Group = "Strategy")]
        public int GapSeconds { get; set; }

        [Parameter("Take profit (pips)", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1, Group = "Trade")]
        public double TakeProfitPips { get; set; }
        [Parameter("Stop loss (pips)", DefaultValue = 3.5, MinValue = 0.3, Step = 0.1, Group = "Trade")]
        public double StopLossPips { get; set; }
        [Parameter("Max hold (seconds)", DefaultValue = 120, MinValue = 5, Group = "Trade")]
        public int MaxHoldSeconds { get; set; }
        [Parameter("Commission per side (pips)", DefaultValue = 0.0, MinValue = 0, Step = 0.1, Group = "Trade")]
        public double CommissionPipsPerSide { get; set; }
        [Parameter("Max cost share of TP", DefaultValue = 0.75, MinValue = 0.05, MaxValue = 1.5, Step = 0.05, Group = "Trade")]
        public double MaxCostShare { get; set; }
        [Parameter("Max lots cap", DefaultValue = 0.50, MinValue = 0.01, Group = "Size")]
        public double MaxLots { get; set; }

        // Internal sizing factor: retained at 2%; not exposed as a bot setting.
        private const double InternalMarginRiskPercent = 2.0;
        private const int TickWindowSeconds = 20;
        private const double TickMovePips = 0.8;
        private const string BotLabel = "WeekendSweepReclaim";

        private struct TickPt { public DateTime T; public double Mid; }
        private class VTrade
        {
            public TradeType Side;
            public double Entry;
            public DateTime Time;
            public double VolumeUnits;
        }
        private class MarketState
        {
            public string Name;
            public Symbol Symbol;
            public Bars Bars;
            public Bars BarsM5;
            public Bars BarsM15;
            public ExponentialMovingAverage Fast;
            public ExponentialMovingAverage Slow;
            public ExponentialMovingAverage FastM5;
            public ExponentialMovingAverage SlowM5;
            public ExponentialMovingAverage FastM15;
            public ExponentialMovingAverage SlowM15;
            public AverageTrueRange Atr;
            public Queue<TickPt> Ticks = new Queue<TickPt>();
            public List<VTrade> VirtualTrades = new List<VTrade>();
            public DateTime LastEntry = DateTime.MinValue;
            public DateTime LastDetailLog = DateTime.MinValue;
            public bool IsCrypto;
        }

        private readonly List<MarketState> _markets = new List<MarketState>();
        private DateTime _lastScan = DateTime.MinValue;
        private DateTime _day = DateTime.MinValue;
        private double _dayStartBalance;
        private double _dayNet;
        private int _vWins, _vLosses;
        private double _vPips;

        protected override void OnStart()
        {
            if (!DryRun && Account.IsLive && !AllowLiveAccount)
            {
                Print("BLOCKED: live account and Allow Live Account = false. Stopping.");
                Stop();
                return;
            }

            foreach (string raw in (MarketsToScan ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = raw.Trim();
                if (name.Length == 0) continue;

                Symbol symbol = Symbols.GetSymbol(name);
                if (symbol == null)
                {
                    Print("MARKET UNAVAILABLE: {0}. Check the exact symbol name in this broker.", name);
                    continue;
                }

                Bars bars = MarketData.GetBars(TimeFrame.Minute, name);
                Bars barsM5 = MarketData.GetBars(TimeFrame.Minute5, name);
                Bars barsM15 = MarketData.GetBars(TimeFrame.Minute15, name);
                var state = new MarketState
                {
                    Name = name,
                    Symbol = symbol,
                    Bars = bars,
                    BarsM5 = barsM5,
                    BarsM15 = barsM15,
                    Fast = Indicators.ExponentialMovingAverage(bars.ClosePrices, 9),
                    Slow = Indicators.ExponentialMovingAverage(bars.ClosePrices, 21),
                    FastM5 = Indicators.ExponentialMovingAverage(barsM5.ClosePrices, 9),
                    SlowM5 = Indicators.ExponentialMovingAverage(barsM5.ClosePrices, 21),
                    FastM15 = Indicators.ExponentialMovingAverage(barsM15.ClosePrices, 9),
                    SlowM15 = Indicators.ExponentialMovingAverage(barsM15.ClosePrices, 21),
                    Atr = Indicators.AverageTrueRange(bars, 14, MovingAverageType.Simple),
                    IsCrypto = name.IndexOf("BTC", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("ETH", StringComparison.OrdinalIgnoreCase) >= 0
                };
                _markets.Add(state);
                Print("SCANNING: {0}", name);
            }

            if (_markets.Count == 0)
            {
                Print("No configured markets are available. Check Markets to scan.");
                Stop();
                return;
            }

            Positions.Closed += OnPositionClosed;
            RollDay(Server.Time);
            Timer.Start(TimeSpan.FromSeconds(1));
            Print("Multi-market Cycle Scalper started. Scanning {0} markets automatically.", _markets.Count);
        }

        protected override void OnTimer()
        {
            DateTime now = Server.Time;
            RollDay(now);

            foreach (MarketState m in _markets)
            {
                double bid = m.Symbol.Bid;
                double ask = m.Symbol.Ask;
                if (bid <= 0 || ask <= 0) continue;
                double mid = (bid + ask) / 2.0;

                m.Ticks.Enqueue(new TickPt { T = now, Mid = mid });
                while (m.Ticks.Count > 0 && (now - m.Ticks.Peek().T).TotalSeconds > TickWindowSeconds)
                    m.Ticks.Dequeue();

                ManageOpenTrades(m, now, bid, ask);
            }

            bool weekend = now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday;
            int totalOpen = DryRun
                ? _markets.Sum(m => m.VirtualTrades.Count)
                : _markets.Sum(m => Positions.FindAll(BotLabel, m.Name).Length);
            if (totalOpen >= MaxOpen) return;
            if ((now - _lastScan).TotalSeconds < 1) return;
            _lastScan = now;

            // Evaluate every eligible market; take the strongest valid signal first.
            var candidates = new List<Tuple<MarketState, TradeType, double>>();
            foreach (MarketState m in _markets)
            {
                if (m.IsCrypto != weekend) continue;
                if (!IsPairAllowedNow(m.Name, now)) continue;
                if (m.Bars.Count < 40 || m.BarsM5.Count < 40 || m.BarsM15.Count < 40) continue;

                double score;
                TradeType direction;
                if (Score(m, now, out direction, out score) && score >= MinConfidence)
                    candidates.Add(Tuple.Create(m, direction, score));
            }

            foreach (var candidate in candidates.OrderByDescending(x => x.Item3))
            {
                MarketState m = candidate.Item1;
                if (totalOpen >= MaxOpen) break;
                if ((now - m.LastEntry).TotalSeconds < GapSeconds) continue;

                double spreadPips = (m.Symbol.Ask - m.Symbol.Bid) / m.Symbol.PipSize;
                double totalCostPips = spreadPips + 2.0 * CommissionPipsPerSide;
                double maxCostPips = MaxCostShare * TakeProfitPips;
                if (totalCostPips > maxCostPips)
                {
                    if (Verbose)
                        Print("{0}: REJECTED by spread filter. Spread={1:F2} pips, commission={2:F2}, total={3:F2}, allowed={4:F2}.",
                            m.Name, spreadPips, 2.0 * CommissionPipsPerSide, totalCostPips, maxCostPips);
                    continue;
                }

                if (Enter(m, candidate.Item2, now))
                    totalOpen++;
            }
        }

        private bool IsPairAllowedNow(string pairName, DateTime utcNow)
        {
            int hour = utcNow.Hour;
            string name = pairName.ToUpperInvariant();

            // 00:00-06:00 UTC (03:00-09:00 EAT): Asian session.
            if (hour >= 0 && hour < 6)
                return name.Contains("AUDUSD") || name.Contains("USDJPY");

            // 06:00-08:00 UTC (09:00-11:00 EAT): dead zone.
            if (hour >= 6 && hour < 8)
                return false;

            // 08:00-12:00 UTC (11:00-15:00 EAT): London open.
            if (hour >= 8 && hour < 12)
                return name.Contains("EURUSD") || name.Contains("GBPUSD") || name.Contains("USDCHF");

            // 12:00-16:00 UTC (15:00-19:00 EAT): London/New York overlap.
            if (hour >= 12 && hour < 16)
                return true;

            // 16:00-17:00 UTC (19:00-20:00 EAT): New York afternoon.
            if (hour >= 16 && hour < 17)
                return name.Contains("USDCAD") || name.Contains("BTC") || name.Contains("ETH");

            // 17:00 UTC onwards: evening/rollover.
            return false;
        }

        private bool Score(MarketState m, DateTime now, out TradeType direction, out double score)
        {
            direction = TradeType.Buy;
            score = 0;

            // M1 provides the entry trigger; M5 is the primary trend;
            // M15 is the higher-timeframe directional confirmation.
            double m1Sep = (m.Fast.Result.Last(1) - m.Slow.Result.Last(1)) / m.Symbol.PipSize;
            double m5Sep = (m.FastM5.Result.Last(1) - m.SlowM5.Result.Last(1)) / m.Symbol.PipSize;
            double m15Sep = (m.FastM15.Result.Last(1) - m.SlowM15.Result.Last(1)) / m.Symbol.PipSize;
            double atrPips = m.Atr.Result.Last(1) / m.Symbol.PipSize;
            if (atrPips <= 0 || m1Sep == 0 || m5Sep == 0 || m15Sep == 0) return false;

            // ALLOW HEDGING: Direction is based purely on M1 (1-minute momentum)
            int sign = m1Sep > 0 ? 1 : -1;
            direction = sign > 0 ? TradeType.Buy : TradeType.Sell;

            // Penalize score if M5/M15 disagree, but DO NOT block the trade.
            double m1Trend = Clamp01(Math.Abs(m1Sep) / Math.Max(0.1, 0.35 * atrPips));
            double m5Trend = (Math.Sign(m5Sep) == sign) ? Clamp01(Math.Abs(m5Sep) / Math.Max(0.1, 0.50 * atrPips)) : 0;
            double m15Trend = (Math.Sign(m15Sep) == sign) ? Clamp01(Math.Abs(m15Sep) / Math.Max(0.1, 0.75 * atrPips)) : 0;

            int m1Aligned = 0;
            for (int i = 1; i <= 5; i++)
            {
                double change = m.Bars.ClosePrices.Last(i) - m.Bars.ClosePrices.Last(i + 1);
                if (change * sign > 0) m1Aligned++;
            }
            int m5Aligned = 0;
            for (int i = 1; i <= 3; i++)
            {
                double change = m.BarsM5.ClosePrices.Last(i) - m.BarsM5.ClosePrices.Last(i + 1);
                if (change * sign > 0) m5Aligned++;
            }
            int m15Aligned = 0;
            for (int i = 1; i <= 2; i++)
            {
                double change = m.BarsM15.ClosePrices.Last(i) - m.BarsM15.ClosePrices.Last(i + 1);
                if (change * sign > 0) m15Aligned++;
            }

            double candleScore = (0.20 * (m1Aligned / 5.0))
                + (0.45 * (m5Aligned / 3.0))
                + (0.35 * (m15Aligned / 2.0));

            double tickMomentum = 0;
            if (m.Ticks.Count >= 5)
            {
                TickPt oldest = m.Ticks.Peek();
                if ((now - oldest.T).TotalSeconds >= TickWindowSeconds * 0.6)
                {
                    double move = ((m.Symbol.Bid + m.Symbol.Ask) / 2.0 - oldest.Mid)
                        / m.Symbol.PipSize * sign;
                    tickMomentum = Clamp01(move / TickMovePips);
                }
            }

            double cost = (m.Symbol.Ask - m.Symbol.Bid) / m.Symbol.PipSize
                + 2.0 * CommissionPipsPerSide;
            double costQuality = Clamp01(1.0 - cost / (MaxCostShare * TakeProfitPips));

            // Transparent 0-100 setup score, not a statistically calibrated win probability.
            double trendScore = (0.20 * m1Trend) + (0.45 * m5Trend) + (0.35 * m15Trend);
            score = 100.0 * ((0.35 * trendScore)
                + (0.35 * candleScore)
                + (0.15 * tickMomentum)
                + (0.15 * costQuality));

            if (Verbose && (now - m.LastDetailLog).TotalSeconds >= 30)
            {
                m.LastDetailLog = now;
                double spreadNow = (m.Symbol.Ask - m.Symbol.Bid) / m.Symbol.PipSize;
                Print("{0} M1/M5/M15 setup score: {1:F0}/100 ({2}; trend {3:F0}, candles {4:F0}, ticks {5:F0}, cost quality {6:F0}; spread {7:F2} pips, cost limit {8:F2})",
                    m.Name, score, direction, trendScore * 100, candleScore * 100,
                    tickMomentum * 100, costQuality * 100, spreadNow, MaxCostShare * TakeProfitPips);
            }
            return true;
        }

        private bool Enter(MarketState m, TradeType side, DateTime now)
        {
            double atrPips = m.Atr.Result.Last(1) / m.Symbol.PipSize;
            if (atrPips <= 0) atrPips = 10.0;

            // Dynamic free-margin/ATR sizing. The 2% factor stays internal.
            double riskMoney = Account.FreeMargin * (InternalMarginRiskPercent / 100.0);
            double pipValuePerLot = m.Symbol.PipValue;
            if (riskMoney <= 0 || pipValuePerLot <= 0)
            {
                Print("{0}: ENTRY BLOCKED; free margin={1:F2}, pip value/lot={2:F5}.",
                    m.Name, Account.FreeMargin, pipValuePerLot);
                return false;
            }

            double lots = riskMoney / (atrPips * pipValuePerLot);
            lots = Math.Min(lots, MaxLots);
            double units = m.Symbol.QuantityToVolumeInUnits(lots);
            units = m.Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);
            if (units < m.Symbol.VolumeInUnitsMin)
            {
                Print("{0}: ENTRY BLOCKED; calculated volume {1} units ({2:F4} lots) is below broker minimum {3} units ({4:F4} lots). Free margin={5:F2}, ATR={6:F2} pips.",
                    m.Name, units, units / m.Symbol.LotSize, m.Symbol.VolumeInUnitsMin,
                    m.Symbol.VolumeInUnitsMin / m.Symbol.LotSize, Account.FreeMargin, atrPips);
                return false;
            }

            m.LastEntry = now;
            if (DryRun)
            {
                m.VirtualTrades.Add(new VTrade
                {
                    Side = side,
                    Entry = side == TradeType.Buy ? m.Symbol.Ask : m.Symbol.Bid,
                    Time = now,
                    VolumeUnits = units
                });
                if (Verbose) Print("[DRY] SIMULATED ENTRY {0} {1} {2:F2} lots", m.Name, side, units / m.Symbol.LotSize);
                return true;
            }

            TradeResult result = ExecuteMarketOrder(side, m.Name, units, BotLabel, StopLossPips, TakeProfitPips);
            if (!result.IsSuccessful)
            {
                Print("{0}: BROKER REJECTED ORDER. Error={1}; volume={2} units ({3:F4} lots); spread={4:F2} pips; SL={5:F1}; TP={6:F1}.",
                    m.Name, result.Error, units, units / m.Symbol.LotSize,
                    (m.Symbol.Ask - m.Symbol.Bid) / m.Symbol.PipSize, StopLossPips, TakeProfitPips);
                return false;
            }

            if (Verbose)
                Print("OPEN CONFIRMED {0} {1} {2:F2} lots @ {3}", m.Name, side, units / m.Symbol.LotSize, result.Position.EntryPrice);
            return true;
        }

        private void ManageOpenTrades(MarketState m, DateTime now, double bid, double ask)
        {
            if (DryRun)
            {
                for (int i = m.VirtualTrades.Count - 1; i >= 0; i--)
                {
                    VTrade trade = m.VirtualTrades[i];
                    double pips = trade.Side == TradeType.Buy
                        ? (bid - trade.Entry) / m.Symbol.PipSize
                        : (trade.Entry - ask) / m.Symbol.PipSize;
                    string reason = null;
                    if (pips >= TakeProfitPips) reason = "TP";
                    else if (pips <= -StopLossPips) reason = "SL";
                    else if ((now - trade.Time).TotalSeconds >= MaxHoldSeconds) reason = "TIME";
                    if (reason == null) continue;

                    pips -= 2.0 * CommissionPipsPerSide;
                    _vPips += pips;
                    if (pips > 0) _vWins++; else _vLosses++;
                    double money = pips * m.Symbol.PipValue * (trade.VolumeUnits / m.Symbol.LotSize);
                    _dayNet += money;
                    if (Verbose) Print("[DRY] {0} closed ({1}) {2:F2} pips; estimated net {3:F2}", m.Name, reason, pips, money);
                    m.VirtualTrades.RemoveAt(i);
                }
                return;
            }

            foreach (Position position in Positions.FindAll(BotLabel, m.Name))
            {
                if ((now - position.EntryTime).TotalSeconds >= MaxHoldSeconds)
                    ClosePosition(position);
            }
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            Position position = args.Position;
            if (position.Label != BotLabel) return;
            _dayNet += position.NetProfit;
            if (Verbose)
                Print("CLOSED {0} {1} {2:F2} pips; net {3:F2}; day net {4:F2}",
                    position.SymbolName, position.TradeType, position.Pips, position.NetProfit, _dayNet);
        }

        private static double Clamp01(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        private void RollDay(DateTime now)
        {
            if (_day.Date == now.Date) return;
            _day = now.Date;
            _dayStartBalance = Account.Balance;
            _dayNet = 0;
        }

        protected override void OnStop()
        {
            Timer.Stop();
            if (DryRun)
                Print("DRY RUN SUMMARY: {0} wins / {1} losses, {2:F1} net pips; estimated day net {3:F2}",
                    _vWins, _vLosses, _vPips, _dayNet);
        }
    }
}