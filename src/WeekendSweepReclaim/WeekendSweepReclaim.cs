// =====================================================================================
// WeekendSweepReclaim.cs - Multi-market continuous stream scalper
// One cBot instance scans the configured market universe automatically.
// It can be attached to ANY chart; the chart symbol is not used as a trade filter.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

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
        [Parameter("Min confidence %", DefaultValue = 80, MinValue = 40, MaxValue = 100, Group = "Strategy")]
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
        [Parameter("Max cost share of TP", DefaultValue = 0.35, MinValue = 0.05, MaxValue = 1.0, Step = 0.05, Group = "Trade")]
        public double MaxCostShare { get; set; }

        [Parameter("Margin Risk % per trade", DefaultValue = 2.0, MinValue = 0.1, MaxValue = 10.0, Step = 0.1, Group = "Size")]
        public double MarginRiskPercent { get; set; }
        [Parameter("Max lots cap", DefaultValue = 0.50, MinValue = 0.01, Group = "Size")]
        public double MaxLots { get; set; }
        [Parameter("Daily loss limit %", DefaultValue = 5.0, MinValue = 0.1, Step = 0.1, Group = "Size")]
        public double DailyLossPercent { get; set; }

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
            public ExponentialMovingAverage Fast;
            public ExponentialMovingAverage Slow;
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
                var state = new MarketState
                {
                    Name = name,
                    Symbol = symbol,
                    Bars = bars,
                    Fast = Indicators.ExponentialMovingAverage(bars.ClosePrices, 9),
                    Slow = Indicators.ExponentialMovingAverage(bars.ClosePrices, 21),
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
            Print("Multi-market Cycle Scalper started from chart {0}. Scanning {1} markets automatically.",
                SymbolName, _markets.Count);
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
            if (DailyLimitHit()) return;
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
                if (m.Bars.Count < 40) continue;

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
                if (spreadPips + 2.0 * CommissionPipsPerSide > MaxCostShare * TakeProfitPips)
                    continue;

                Enter(m, candidate.Item2, now);
                totalOpen++;
            }
        }

        private bool Score(MarketState m, DateTime now, out TradeType direction, out double score)
        {
            direction = TradeType.Buy;
            score = 0;
            double f = m.Fast.Result.Last(1);
            double s = m.Slow.Result.Last(1);
            double atrPips = m.Atr.Result.Last(1) / m.Symbol.PipSize;
            if (atrPips <= 0) return false;

            double separation = (f - s) / m.Symbol.PipSize;
            direction = separation >= 0 ? TradeType.Buy : TradeType.Sell;
            int sign = direction == TradeType.Buy ? 1 : -1;
            double trend = Clamp01(Math.Abs(separation) / (0.5 * atrPips));

            int aligned = 0;
            for (int i = 1; i <= 5; i++)
            {
                double change = m.Bars.ClosePrices.Last(i) - m.Bars.ClosePrices.Last(i + 1);
                if (change * sign > 0) aligned++;
            }
            double candles = aligned / 5.0;

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
            score = 25.0 * (trend + candles + tickMomentum + costQuality);

            if (Verbose && (now - m.LastDetailLog).TotalSeconds >= 30)
            {
                m.LastDetailLog = now;
                Print("{0} scan: {1:F0}% confidence ({2}, trend {3:F0}, candles {4:F0}, ticks {5:F0}, cost {6:F0})",
                    m.Name, score, direction, trend * 25, candles * 25, tickMomentum * 25, costQuality * 25);
            }
            return true;
        }

        private void Enter(MarketState m, TradeType side, DateTime now)
        {
            double atrPips = m.Atr.Result.Last(1) / m.Symbol.PipSize;
            if (atrPips <= 0) atrPips = 10.0;

            double riskMoney = Account.FreeMargin * (MarginRiskPercent / 100.0);
            double lots = riskMoney / (atrPips * m.Symbol.PipValue);
            double units = lots * m.Symbol.LotSize;
            double maxUnits = m.Symbol.QuantityToVolumeInUnits(MaxLots);
            if (units > maxUnits) units = maxUnits;
            units = m.Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);
            if (units < m.Symbol.VolumeInUnitsMin)
            {
                if (Verbose) Print("{0}: skipped; calculated volume below minimum.", m.Name);
                return;
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
                if (Verbose) Print("[DRY] {0} {1} {2:F2} lots", m.Name, side, units / m.Symbol.LotSize);
                return;
            }

            TradeResult result = ExecuteMarketOrder(side, m.Name, units, BotLabel, StopLossPips, TakeProfitPips);
            if (!result.IsSuccessful)
                Print("{0}: order failed: {1}", m.Name, result.Error);
            else if (Verbose)
                Print("OPEN {0} {1} {2:F2} lots @ {3}", m.Name, side, units / m.Symbol.LotSize, result.Position.EntryPrice);
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

        private bool DailyLimitHit()
        {
            return _dayNet <= -(_dayStartBalance * DailyLossPercent / 100.0);
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