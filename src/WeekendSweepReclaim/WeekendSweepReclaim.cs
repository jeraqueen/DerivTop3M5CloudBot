// =====================================================================================
// WeekendSweepReclaim.cs - Continuous Weekend Stream Scalper
// Opens trades individually up to MaxOpen, uses risk-based dynamic sizing,
// and closes each trade individually. No martingale and no locked baskets.
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
        [Parameter("Dry Run (simulate only)", DefaultValue = false, Group = "Safety")] public bool DryRun { get; set; }
        [Parameter("Allow Live Account", DefaultValue = false, Group = "Safety")] public bool AllowLiveAccount { get; set; }
        [Parameter("Verbose log", DefaultValue = true, Group = "Safety")] public bool Verbose { get; set; }

        [Parameter("Allowed Symbol Keywords", DefaultValue = "EURUSD,GBPUSD,USDJPY,AUDUSD,USDCAD,USDCHF,BTC,ETH", Group = "Strategy")] public string AllowedKeywords { get; set; }
        [Parameter("Min confidence %", DefaultValue = 80, MinValue = 40, MaxValue = 100, Group = "Strategy")] public int MinConfidence { get; set; }
        [Parameter("Max open trades at once", DefaultValue = 7, MinValue = 1, MaxValue = 15, Group = "Strategy")] public int MaxOpen { get; set; }
        [Parameter("Seconds between entries", DefaultValue = 2, MinValue = 1, Group = "Strategy")] public int GapSeconds { get; set; }

        [Parameter("Take profit (pips)", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1, Group = "Trade")] public double TakeProfitPips { get; set; }
        [Parameter("Stop loss (pips)", DefaultValue = 3.5, MinValue = 0.3, Step = 0.1, Group = "Trade")] public double StopLossPips { get; set; }
        [Parameter("Max hold (seconds)", DefaultValue = 120, MinValue = 5, Group = "Trade")] public int MaxHoldSeconds { get; set; }
        [Parameter("Commission per side (pips)", DefaultValue = 0.0, MinValue = 0, Step = 0.1, Group = "Trade")] public double CommissionPipsPerSide { get; set; }
        [Parameter("Max cost share of TP", DefaultValue = 0.35, MinValue = 0.05, MaxValue = 1.0, Step = 0.05, Group = "Trade")] public double MaxCostShare { get; set; }

        [Parameter("Margin Risk % per trade", DefaultValue = 2.0, MinValue = 0.1, MaxValue = 10.0, Step = 0.1, Group = "Size")] public double MarginRiskPercent { get; set; }
        [Parameter("Max lots cap", DefaultValue = 0.50, MinValue = 0.01, Group = "Size")] public double MaxLots { get; set; }
        [Parameter("Daily loss limit %", DefaultValue = 5.0, MinValue = 0.1, Step = 0.1, Group = "Size")] public double DailyLossPercent { get; set; }

        private struct TickPt { public DateTime T; public double Mid; }
        private class VTrade { public TradeType Side; public double Entry; public DateTime Time; public double VolumeUnits; }

        private const int TickWindowSeconds = 20;
        private const double TickMovePips = 0.8;

        private readonly Queue<TickPt> _window = new Queue<TickPt>();
        private readonly List<VTrade> _virtual = new List<VTrade>();

        private Bars _m1;
        private ExponentialMovingAverage _fast;
        private ExponentialMovingAverage _slow;
        private AverageTrueRange _atr;

        private string _label;
        private bool _symbolAllowed;
        private bool _isCrypto;
        private DateTime _lastScan = DateTime.MinValue;
        private DateTime _lastEntry = DateTime.MinValue;
        private DateTime _lastDetailLog = DateTime.MinValue;

        private DateTime _day = DateTime.MinValue;
        private double _dayStartBalance;
        private double _dayNet;

        private int _vWins, _vLosses;
        private double _vPips;

        protected override void OnStart()
        {
            _label = "WeekendSweepReclaim";
            var allowed = (AllowedKeywords ?? "BTC,ETH").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            _symbolAllowed = allowed.Any(x => SymbolName.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
            _isCrypto = SymbolName.IndexOf("BTC", StringComparison.OrdinalIgnoreCase) >= 0
                || SymbolName.IndexOf("ETH", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!_symbolAllowed)
            {
                Print("SYMBOL BLOCKED: {0} is not in the allowed markets [{1}].", SymbolName, AllowedKeywords);
                Stop();
                return;
            }

            if (!DryRun && Account.IsLive && !AllowLiveAccount)
            {
                Print("BLOCKED: live account and Allow Live Account = false. Stopping.");
                Stop();
                return;
            }

            _m1 = MarketData.GetBars(TimeFrame.Minute);
            _fast = Indicators.ExponentialMovingAverage(_m1.ClosePrices, 9);
            _slow = Indicators.ExponentialMovingAverage(_m1.ClosePrices, 21);
            _atr = Indicators.AverageTrueRange(_m1, 14, MovingAverageType.Simple);

            Positions.Closed += OnPositionClosed;
            RollDay(Server.Time);

            Print("Cycle scalper started on {0}. Forex Mon-Fri; BTC/ETH Sat-Sun. MaxOpen={1} | Gap={2}s | Dynamic Sizing ON",
                SymbolName, MaxOpen, GapSeconds);
        }

        protected override void OnTick()
        {
            DateTime now = Server.Time;
            RollDay(now);
            if (!_symbolAllowed) return;

            double bid = Symbol.Bid;
            double ask = Symbol.Ask;
            double mid = (bid + ask) / 2.0;

            _window.Enqueue(new TickPt { T = now, Mid = mid });
            while (_window.Count > 0 && (now - _window.Peek().T).TotalSeconds > TickWindowSeconds)
                _window.Dequeue();

            ManageOpenTrades(now, bid, ask);

            bool weekend = now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday;
            // Forex symbols trade Monday-Friday; BTC and ETH trade Saturday-Sunday.
            if (_isCrypto != weekend) return;
            if (DailyLimitHit()) return;

            int open = DryRun ? _virtual.Count : Positions.FindAll(_label, SymbolName).Length;
            if (open >= MaxOpen) return;

            if ((now - _lastScan).TotalSeconds < 1) return;
            _lastScan = now;

            double score;
            TradeType dir;
            if (!Score(mid, bid, ask, out dir, out score)) return;

            if (score >= MinConfidence)
            {
                if ((now - _lastEntry).TotalSeconds < GapSeconds) return;

                double spreadPips = (ask - bid) / Symbol.PipSize;
                if (spreadPips + 2.0 * CommissionPipsPerSide > MaxCostShare * TakeProfitPips) return;

                Enter(dir, now, bid, ask);
            }
        }

        private bool Score(double mid, double bid, double ask, out TradeType dir, out double score)
        {
            dir = TradeType.Buy;
            score = 0;

            if (_m1.Count < 40) return false;

            double f = _fast.Result.Last(1);
            double s = _slow.Result.Last(1);
            double atrPips = _atr.Result.Last(1) / Symbol.PipSize;
            if (atrPips <= 0) return false;

            double sepPips = (f - s) / Symbol.PipSize;
            dir = sepPips >= 0 ? TradeType.Buy : TradeType.Sell;
            int sign = dir == TradeType.Buy ? 1 : -1;

            double p1 = Clamp01(Math.Abs(sepPips) / (0.5 * atrPips));

            int agree = 0;
            for (int i = 1; i <= 5; i++)
            {
                double diff = _m1.ClosePrices.Last(i) - _m1.ClosePrices.Last(i + 1);
                if (diff * sign > 0) agree++;
            }
            double p2 = agree / 5.0;

            double p3 = 0;
            if (_window.Count >= 5)
            {
                TickPt oldest = _window.Peek();
                double span = (Server.Time - oldest.T).TotalSeconds;
                if (span >= TickWindowSeconds * 0.6)
                {
                    double movePips = (mid - oldest.Mid) / Symbol.PipSize * sign;
                    p3 = Clamp01(movePips / TickMovePips);
                }
            }

            double cost = (ask - bid) / Symbol.PipSize + 2.0 * CommissionPipsPerSide;
            double p4 = Clamp01(1.0 - cost / (MaxCostShare * TakeProfitPips));

            score = 25.0 * (p1 + p2 + p3 + p4);

            if (Verbose && (Server.Time - _lastDetailLog).TotalSeconds >= 30)
            {
                _lastDetailLog = Server.Time;
                Print("Scan {0}: {1:F0}% (trend {2:F0}, candles {3:F0}, ticks {4:F0}, cost {5:F0})",
                    dir, score, p1 * 25, p2 * 25, p3 * 25, p4 * 25);
            }
            return true;
        }

        private static double Clamp01(double x)
        {
            if (x < 0) return 0;
            if (x > 1) return 1;
            return x;
        }

        private void Enter(TradeType side, DateTime now, double bid, double ask)
        {
            double atrPips = _atr.Result.Last(1) / Symbol.PipSize;
            if (atrPips <= 0) atrPips = 10.0;

            // PipValue is expressed per lot. Convert calculated lots into volume units.
            double riskMoney = Account.FreeMargin * (MarginRiskPercent / 100.0);
            double calculatedLots = riskMoney / (atrPips * Symbol.PipValue);
            double units = calculatedLots * Symbol.LotSize;

            double maxUnits = Symbol.QuantityToVolumeInUnits(MaxLots);
            if (units > maxUnits) units = maxUnits;

            units = Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);
            if (units < Symbol.VolumeInUnitsMin)
            {
                if (Verbose) Print("Skipped: Calculated volume below minimum.");
                return;
            }

            _lastEntry = now;
            double entryPrice = side == TradeType.Buy ? ask : bid;

            if (DryRun)
            {
                _virtual.Add(new VTrade { Side = side, Entry = entryPrice, Time = now, VolumeUnits = units });
                if (Verbose) Print("[DRY] {0} {1:F2} lots @ {2}", side, units / Symbol.LotSize, entryPrice);
                return;
            }

            TradeResult res = ExecuteMarketOrder(side, SymbolName, units, _label, StopLossPips, TakeProfitPips);
            if (!res.IsSuccessful)
                Print("Order failed: {0}", res.Error);
            else if (Verbose)
                Print("OPEN {0} {1:F2} lots @ {2}", side, units / Symbol.LotSize, res.Position.EntryPrice);
        }

        private void ManageOpenTrades(DateTime now, double bid, double ask)
        {
            if (DryRun)
            {
                for (int i = _virtual.Count - 1; i >= 0; i--)
                {
                    VTrade v = _virtual[i];
                    double pips = v.Side == TradeType.Buy
                        ? (bid - v.Entry) / Symbol.PipSize
                        : (v.Entry - ask) / Symbol.PipSize;

                    string why = null;
                    if (pips >= TakeProfitPips) why = "TP";
                    else if (pips <= -StopLossPips) why = "SL";
                    else if ((now - v.Time).TotalSeconds >= MaxHoldSeconds) why = "TIME";
                    if (why == null) continue;

                    pips -= 2.0 * CommissionPipsPerSide;
                    _vPips += pips;
                    if (pips > 0) _vWins++; else _vLosses++;

                    // Approximate simulated net P/L in account currency using volume and pip value.
                    double simulatedMoney = pips * Symbol.PipValue * (v.VolumeUnits / Symbol.LotSize);
                    RecordResult(pips, simulatedMoney);

                    if (Verbose)
                        Print("[DRY] close {0} ({1}) {2:F2} pips | est. net {3:F2} | day net {4:F2} | total {5:F1} pips, W{6}/L{7}",
                            v.Side, why, pips, simulatedMoney, _dayNet, _vPips, _vWins, _vLosses);

                    _virtual.RemoveAt(i);
                }
                return;
            }

            foreach (Position p in Positions.FindAll(_label, SymbolName))
            {
                if ((now - p.EntryTime).TotalSeconds >= MaxHoldSeconds)
                    ClosePosition(p);
            }
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            Position p = args.Position;
            if (p.Label != _label || p.SymbolName != SymbolName) return;

            RecordResult(p.Pips, p.NetProfit);
            if (Verbose)
                Print("CLOSED {0} {1:F2} pips, net {2:F2} | day net {3:F2}",
                    p.TradeType, p.Pips, p.NetProfit, _dayNet);
        }

        private void RecordResult(double pips, double money)
        {
            _dayNet += money;
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
            if (DryRun)
                Print("DRY RUN SUMMARY: {0} wins / {1} losses, net {2:F1} pips | estimated day net {3:F2}",
                    _vWins, _vLosses, _vPips, _dayNet);
        }
    }
}