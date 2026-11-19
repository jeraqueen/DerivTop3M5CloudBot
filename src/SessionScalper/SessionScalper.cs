using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class SessionScalper : Robot
    {
        [Parameter("Allowed symbols", DefaultValue = "EURUSD,GBPUSD,USDJPY", Group = "Markets")]
        public string AllowedSymbols { get; set; }

        [Parameter("Entry timeframe (M1 or M5)", DefaultValue = "M1", Group = "Strategy")]
        public string EntryTimeframe { get; set; }

        [Parameter("Trend EMA period", DefaultValue = 50, MinValue = 2, Group = "Strategy")]
        public int TrendEmaPeriod { get; set; }

        [Parameter("Entry EMA period", DefaultValue = 20, MinValue = 2, Group = "Strategy")]
        public int EntryEmaPeriod { get; set; }

        [Parameter("ADX period", DefaultValue = 14, MinValue = 2, Group = "Strategy")]
        public int AdxPeriod { get; set; }

        [Parameter("Minimum ADX", DefaultValue = 20, MinValue = 1, Group = "Strategy")]
        public double MinimumAdx { get; set; }

        [Parameter("Minimum candle body / range", DefaultValue = 0.50, MinValue = 0.1, MaxValue = 1, Step = 0.05, Group = "Strategy")]
        public double MinimumBodyRatio { get; set; }

        [Parameter("Pullback tolerance (pips)", DefaultValue = 2.0, MinValue = 0, Step = 0.1, Group = "Strategy")]
        public double PullbackTolerancePips { get; set; }

        [Parameter("Start hour UTC", DefaultValue = 7, MinValue = 0, MaxValue = 23, Group = "Session")]
        public int StartHour { get; set; }

        [Parameter("End hour UTC", DefaultValue = 16, MinValue = 1, MaxValue = 24, Group = "Session")]
        public int EndHour { get; set; }

        [Parameter("Risk per trade (%)", DefaultValue = 0.5, MinValue = 0.05, MaxValue = 5, Step = 0.05, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("Maximum daily loss (%)", DefaultValue = 2.0, MinValue = 0.1, MaxValue = 20, Step = 0.1, Group = "Risk")]
        public double MaxDailyLossPercent { get; set; }

        [Parameter("Enable daily profit lock", DefaultValue = true, Group = "Risk")]
        public bool EnableDailyProfitLock { get; set; }

        [Parameter("Daily profit lock (%)", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 50, Step = 0.1, Group = "Risk")]
        public double DailyProfitLockPercent { get; set; }

        [Parameter("Maximum trades per day", DefaultValue = 10, MinValue = 1, MaxValue = 100, Group = "Risk")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Maximum open trades overall", DefaultValue = 2, MinValue = 1, MaxValue = 20, Group = "Risk")]
        public int MaxOpenTradesOverall { get; set; }

        [Parameter("Maximum consecutive losses", DefaultValue = 3, MinValue = 1, MaxValue = 20, Group = "Risk")]
        public int MaxConsecutiveLosses { get; set; }

        [Parameter("Loss cooldown (minutes)", DefaultValue = 60, MinValue = 1, MaxValue = 1440, Group = "Risk")]
        public int LossCooldownMinutes { get; set; }

        [Parameter("ATR period", DefaultValue = 14, MinValue = 2, Group = "Stops and targets")]
        public int AtrPeriod { get; set; }

        [Parameter("Stop = ATR x", DefaultValue = 1.0, MinValue = 0.1, Step = 0.1, Group = "Stops and targets")]
        public double AtrStopMultiplier { get; set; }

        [Parameter("Reward / risk ratio", DefaultValue = 1.5, MinValue = 0.5, Step = 0.1, Group = "Stops and targets")]
        public double RewardRatio { get; set; }

        [Parameter("Move stop to breakeven at +1R", DefaultValue = true, Group = "Stops and targets")]
        public bool MoveToBreakeven { get; set; }

        [Parameter("Close partial at +R", DefaultValue = false, Group = "Stops and targets")]
        public bool CloseHalfAtOneR { get; set; }

        [Parameter("Partial close percent", DefaultValue = 50, MinValue = 10, MaxValue = 90, Step = 5, Group = "Stops and targets")]
        public double PartialClosePercent { get; set; }

        [Parameter("Breakeven / management trigger (R)", DefaultValue = 1.0, MinValue = 0.2, Step = 0.1, Group = "Stops and targets")]
        public double ManagementTriggerR { get; set; }

        [Parameter("Trail remaining position", DefaultValue = true, Group = "Stops and targets")]
        public bool TrailRemainingPosition { get; set; }

        [Parameter("Trailing distance = ATR x", DefaultValue = 0.5, MinValue = 0.1, Step = 0.1, Group = "Stops and targets")]
        public double TrailAtrMultiplier { get; set; }

        [Parameter("Maximum spread (pips)", DefaultValue = 1.0, MinValue = 0.1, Step = 0.1, Group = "Filters")]
        public double MaxSpreadPips { get; set; }

        [Parameter("Maximum spread as % of TP", DefaultValue = 20, MinValue = 1, MaxValue = 50, Group = "Filters")]
        public double MaxSpreadToTpPercent { get; set; }

        [Parameter("Minimum ATR (pips)", DefaultValue = 0.5, MinValue = 0, Step = 0.1, Group = "Filters")]
        public double MinAtrPips { get; set; }

        [Parameter("Maximum ATR (pips)", DefaultValue = 30.0, MinValue = 0.1, Step = 0.1, Group = "Filters")]
        public double MaxAtrPips { get; set; }

        [Parameter("Manual high-impact news times (UTC, comma-separated)", DefaultValue = "", Group = "News filter")]
        public string NewsTimesUtc { get; set; }

        [Parameter("News block window (minutes before/after)", DefaultValue = 30, MinValue = 0, MaxValue = 240, Group = "News filter")]
        public int NewsWindowMinutes { get; set; }

        [Parameter("Maximum slippage (pips)", DefaultValue = 1.0, MinValue = 0, Step = 0.1, Group = "Execution")]
        public double MaxSlippagePips { get; set; }

        [Parameter("Magic number", DefaultValue = 26051901, MinValue = 1, Group = "Execution")]
        public int MagicNumber { get; set; }

        private Bars _m15;
        private Bars _entryBars;
        private ExponentialMovingAverage _trendEma;
        private ExponentialMovingAverage _entryEma;
        private DirectionalMovementSystem _dms;
        private AverageTrueRange _atr;
        private string _label;
        private DateTime _day;
        private double _dayStartBalance;
        private readonly List<double> _closedToday = new List<double>();
        private int _tradesToday;
        private int _consecutiveLosses;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private bool _dayLocked;
        private bool _sessionSummaryWritten;
        private int _lastEntryBar = -1;
        private readonly HashSet<long> _partialClosed = new HashSet<long>();

        protected override void OnStart()
        {
            _label = "SessionScalper_" + MagicNumber.ToString(CultureInfo.InvariantCulture);
            if (EndHour <= StartHour)
            {
                Print("CONFIGURATION ERROR: EndHour must be later than StartHour on the same UTC day.");
                Stop();
                return;
            }

            var allowed = (AllowedSymbols ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            if (!allowed.Any(x => string.Equals(x, SymbolName, StringComparison.OrdinalIgnoreCase)))
            {
                Print("SKIP START: chart symbol {0} is not in Allowed symbols: {1}", SymbolName, AllowedSymbols);
                Stop();
                return;
            }

            string tf = (EntryTimeframe ?? "M1").Trim().ToUpperInvariant();
            if (tf == "M1" || tf == "MINUTE" || tf == "1")
                _entryBars = MarketData.GetBars(TimeFrame.Minute, SymbolName);
            else if (tf == "M5" || tf == "MINUTE5" || tf == "5")
                _entryBars = MarketData.GetBars(TimeFrame.Minute5, SymbolName);
            else
            {
                Print("CONFIGURATION ERROR: EntryTimeframe must be M1 or M5.");
                Stop();
                return;
            }

            _m15 = MarketData.GetBars(TimeFrame.Minute15, SymbolName);
            _trendEma = Indicators.ExponentialMovingAverage(_m15.ClosePrices, TrendEmaPeriod);
            _entryEma = Indicators.ExponentialMovingAverage(_entryBars.ClosePrices, EntryEmaPeriod);
            _dms = Indicators.DirectionalMovementSystem(AdxPeriod);
            _atr = Indicators.AverageTrueRange(_entryBars, AtrPeriod, MovingAverageType.Exponential);

            ResetDailyState(Server.Time.Date);
            Positions.Closed += OnPositionClosed;
            Timer.Start(1);
            Print("SessionScalper started on {0}. Entry={1}, UTC session={2:00}:00-{3:00}:00, label={4}. Confirm demo account before enabling.", SymbolName, tf, StartHour, EndHour, _label);
            Print("News filter times are manually supplied UTC timestamps. Slippage is checked after fill; excess-slippage positions are immediately closed.");
        }

        protected override void OnTimer()
        {
            DateTime now = Server.Time;
            if (now.Date != _day)
                ResetDailyState(now.Date);

            ManageOpenPositions();

            if (_dayLocked)
                return;

            if (now.Hour >= EndHour)
            {
                CloseAllBotPositions("end of session");
                WriteDailySummary();
                _dayLocked = true;
                return;
            }

            if (now.Hour < StartHour || now.Hour >= EndHour)
                return;

            if (now < _cooldownUntil)
                return;

            double dayNet = GetBotDailyNet();
            double lossLimit = _dayStartBalance * MaxDailyLossPercent / 100.0;
            if (dayNet <= -lossLimit)
            {
                Print("DAILY LOSS LIMIT HIT: net={0:F2}, limit=-{1:F2}. Closing all SessionScalper positions and locking until next UTC day.", dayNet, lossLimit);
                CloseAllBotPositions("daily loss limit");
                _dayLocked = true;
                WriteDailySummary();
                return;
            }

            if (EnableDailyProfitLock)
            {
                double profitLimit = _dayStartBalance * DailyProfitLockPercent / 100.0;
                if (dayNet >= profitLimit)
                {
                    Print("DAILY PROFIT LOCK HIT: net={0:F2}, target={1:F2}. Closing positions and locking until next UTC day.", dayNet, profitLimit);
                    CloseAllBotPositions("daily profit lock");
                    _dayLocked = true;
                    WriteDailySummary();
                    return;
                }
            }

            if (_tradesToday >= MaxTradesPerDay)
            {
                Print("SKIP ENTRY: maximum daily trades reached ({0}).", MaxTradesPerDay);
                return;
            }

            if (Positions.FindAll(_label).Length >= MaxOpenTradesOverall)
            {
                Print("SKIP ENTRY: maximum overall open trades reached ({0}).", MaxOpenTradesOverall);
                return;
            }

            if (Positions.FindAll(_label, SymbolName).Length > 0)
                return;

            if (_entryBars.Count < Math.Max(EntryEmaPeriod + 5, AtrPeriod + 5) || _m15.Count < TrendEmaPeriod + 5)
                return;

            int closedIndex = _entryBars.Count - 2;
            if (closedIndex <= 1 || closedIndex == _lastEntryBar)
                return;
            _lastEntryBar = closedIndex;

            EvaluateEntry(now, closedIndex);
        }

        private void EvaluateEntry(DateTime now, int i)
        {
            double spread = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            if (spread > MaxSpreadPips)
            {
                Print("SKIP {0}: spread {1:F2} pips exceeds MaxSpreadPips {2:F2}.", SymbolName, spread, MaxSpreadPips);
                return;
            }

            double atrPrice = _atr.Result[i];
            double atrPips = atrPrice / Symbol.PipSize;
            if (atrPips < MinAtrPips || atrPips > MaxAtrPips)
            {
                Print("SKIP {0}: ATR {1:F2} pips outside allowed range {2:F2}-{3:F2}.", SymbolName, atrPips, MinAtrPips, MaxAtrPips);
                return;
            }

            if (IsNewsBlocked(now, out string newsReason))
            {
                Print("SKIP {0}: {1}.", SymbolName, newsReason);
                return;
            }

            int trendIndex = _m15.Count - 2;
            if (trendIndex < TrendEmaPeriod + 2)
                return;

            double trendClose = _m15.ClosePrices[trendIndex];
            double trendEmaNow = _trendEma.Result[trendIndex];
            double trendEmaPrevious = _trendEma.Result[trendIndex - 1];
            double adx = _dms.ADX[trendIndex];
            bool upTrend = trendClose > trendEmaNow && trendEmaNow > trendEmaPrevious && adx > MinimumAdx;
            bool downTrend = trendClose < trendEmaNow && trendEmaNow < trendEmaPrevious && adx > MinimumAdx;
            if (!upTrend && !downTrend)
            {
                Print("SKIP {0}: M15 trend filter failed (close={1}, EMA50={2}, ADX={3:F1}; minimum ADX={4:F1}).", SymbolName, trendClose, trendEmaNow, adx, MinimumAdx);
                return;
            }

            double open = _entryBars.OpenPrices[i];
            double high = _entryBars.HighPrices[i];
            double low = _entryBars.LowPrices[i];
            double close = _entryBars.ClosePrices[i];
            double range = high - low;
            if (range <= 0)
            {
                Print("SKIP {0}: signal candle has zero range.", SymbolName);
                return;
            }

            double bodyRatio = Math.Abs(close - open) / range;
            if (bodyRatio < MinimumBodyRatio)
            {
                Print("SKIP {0}: candle body ratio {1:P0} is below required {2:P0}.", SymbolName, bodyRatio, MinimumBodyRatio);
                return;
            }

            double emaNow = _entryEma.Result[i];
            double emaPrevious = _entryEma.Result[i - 1];
            double tolerance = PullbackTolerancePips * Symbol.PipSize;
            bool buySignal = upTrend && _entryBars.ClosePrices[i] > emaNow &&
                _entryBars.LowPrices[i - 1] <= emaPrevious + tolerance &&
                close > open;
            bool sellSignal = downTrend && _entryBars.ClosePrices[i] < emaNow &&
                _entryBars.HighPrices[i - 1] >= emaPrevious - tolerance &&
                close < open;

            if (!buySignal && !sellSignal)
            {
                Print("SKIP {0}: no confirmed EMA20 pullback-and-rejection candle in M15 trend direction.", SymbolName);
                return;
            }

            double stopPips = atrPips * AtrStopMultiplier;
            double targetPips = stopPips * RewardRatio;
            if (targetPips <= 0)
            {
                Print("SKIP {0}: calculated target distance is invalid.", SymbolName);
                return;
            }

            if (spread > targetPips * MaxSpreadToTpPercent / 100.0)
            {
                Print("SKIP {0}: spread {1:F2} pips exceeds 20% of TP distance ({2:F2} pips).", SymbolName, spread, targetPips);
                return;
            }

            OpenTrade(buySignal ? TradeType.Buy : TradeType.Sell, stopPips, targetPips, atrPips, now);
        }

        private void OpenTrade(TradeType side, double stopPips, double targetPips, double atrPips, DateTime now)
        {
            double riskCash = _dayStartBalance * RiskPercent / 100.0;
            double volume = Symbol.VolumeForFixedRisk(riskCash, stopPips, RoundingMode.Down);
            volume = Symbol.NormalizeVolumeInUnits(volume, RoundingMode.Down);
            if (volume < Symbol.VolumeInUnitsMin)
            {
                Print("SKIP {0}: risk-sized volume {1} is below broker minimum {2}.", SymbolName, volume, Symbol.VolumeInUnitsMin);
                return;
            }

            double expectedPrice = side == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            Print("ENTRY ATTEMPT {0} {1}: volume={2}, SL={3:F2} pips, TP={4:F2} pips, risk={5:F2} ({6:F2}%).", SymbolName, side, volume, stopPips, targetPips, riskCash, RiskPercent);
            TradeResult result = ExecuteMarketOrder(side, SymbolName, volume, _label, stopPips, targetPips, "SessionScalper magic=" + MagicNumber);
            if (!result.IsSuccessful || result.Position == null)
            {
                Print("ORDER FAILED {0}: {1}.", SymbolName, result.Error);
                return;
            }

            _tradesToday++;
            double slippagePips = Math.Abs(result.Position.EntryPrice - expectedPrice) / Symbol.PipSize;
            Print("OPENED {0} {1}: position={2}, entry={3}, slippage={4:F2} pips.", SymbolName, side, result.Position.Id, result.Position.EntryPrice, slippagePips);
            if (slippagePips > MaxSlippagePips)
            {
                Print("SLIPPAGE FILTER: actual slippage {0:F2} exceeds {1:F2} pips. Closing position {2}.", slippagePips, MaxSlippagePips, result.Position.Id);
                ClosePosition(result.Position);
            }
        }

        private void ManageOpenPositions()
        {
            foreach (var position in Positions.FindAll(_label))
            {
                try
                {
                    if (position.SymbolName != SymbolName)
                        continue;
                    double initialRiskPips = position.TakeProfit.HasValue
                        ? Math.Abs(position.TakeProfit.Value - position.EntryPrice) / Symbol.PipSize / RewardRatio
                        : 0;
                    if (initialRiskPips <= 0)
                        continue;

                    double currentPrice = position.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
                    double profitPips = position.TradeType == TradeType.Buy
                        ? (currentPrice - position.EntryPrice) / Symbol.PipSize
                        : (position.EntryPrice - currentPrice) / Symbol.PipSize;

                    if (profitPips >= initialRiskPips * ManagementTriggerR)
                    {
                        if (MoveToBreakeven)
                        {
                            double be = position.EntryPrice;
                            bool improves = !position.StopLoss.HasValue ||
                                (position.TradeType == TradeType.Buy ? be > position.StopLoss.Value : be < position.StopLoss.Value);
                            if (improves)
                            {
                                TradeResult modify = ModifyPosition(position, be, position.TakeProfit);
                                if (modify.IsSuccessful)
                                    Print("BREAKEVEN: position {0} stop moved to entry {1}.", position.Id, be);
                                else
                                    Print("BREAKEVEN FAILED: position {0}: {1}.", position.Id, modify.Error);
                            }
                        }

                        if (CloseHalfAtOneR && !_partialClosed.Contains(position.Id))
                        {
                            double partialVolume = Symbol.NormalizeVolumeInUnits(position.VolumeInUnits * PartialClosePercent / 100.0, RoundingMode.Down);
                            if (partialVolume >= Symbol.VolumeInUnitsMin && partialVolume < position.VolumeInUnits)
                            {
                                TradeResult partial = ClosePosition(position, partialVolume);
                                if (partial.IsSuccessful)
                                {
                                    _partialClosed.Add(position.Id);
                                    Print("PARTIAL PROFIT: closed {1:F0}% of position {0} at management trigger.", position.Id, PartialClosePercent);
                                }
                                else
                                    Print("PARTIAL CLOSE FAILED: position {0}: {1}.", position.Id, partial.Error);
                            }
                            else
                            {
                                _partialClosed.Add(position.Id);
                                Print("PARTIAL CLOSE SKIPPED: position {0}; half-volume is below broker minimum.", position.Id);
                            }
                        }
                    }

                    if (TrailRemainingPosition && profitPips >= initialRiskPips * ManagementTriggerR)
                    {
                        int index = _entryBars.Count - 2;
                        if (index < 1)
                            continue;
                        double trailDistance = _atr.Result[index] * TrailAtrMultiplier;
                        double candidate = position.TradeType == TradeType.Buy
                            ? Symbol.Bid - trailDistance
                            : Symbol.Ask + trailDistance;
                        bool improves = !position.StopLoss.HasValue ||
                            (position.TradeType == TradeType.Buy ? candidate > position.StopLoss.Value : candidate < position.StopLoss.Value);
                        if (improves && candidate > 0)
                        {
                            TradeResult trail = ModifyPosition(position, candidate, position.TakeProfit);
                            if (!trail.IsSuccessful)
                                Print("TRAIL UPDATE FAILED: position {0}: {1}.", position.Id, trail.Error);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Print("POSITION MANAGEMENT ERROR {0}: {1}.", position.Id, ex.Message);
                }
            }
        }

        private bool IsNewsBlocked(DateTime now, out string reason)
        {
            reason = "";
            if (NewsWindowMinutes <= 0 || string.IsNullOrWhiteSpace(NewsTimesUtc))
                return false;

            foreach (string raw in NewsTimesUtc.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime newsTime))
                {
                    Print("NEWS INPUT WARNING: could not parse '{0}'. Use UTC date/time such as 2026-11-20 13:30.", raw.Trim());
                    continue;
                }

                double minutes = Math.Abs((now - newsTime).TotalMinutes);
                if (minutes <= NewsWindowMinutes)
                {
                    reason = "inside manual high-impact news block around " + newsTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
                    return true;
                }
            }
            return false;
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            Position position = args.Position;
            if (position.Label != _label)
                return;

            if (Server.Time.Date == _day)
                _closedToday.Add(position.NetProfit);

            if (position.NetProfit < 0)
            {
                _consecutiveLosses++;
                Print("LOSS recorded: position {0}, net={1:F2}, consecutive losses={2}.", position.Id, position.NetProfit, _consecutiveLosses);
                if (_consecutiveLosses >= MaxConsecutiveLosses)
                {
                    _cooldownUntil = Server.Time.AddMinutes(LossCooldownMinutes);
                    Print("LOSS COOLDOWN: {0} consecutive losses. No new entries until {1:yyyy-MM-dd HH:mm} UTC.", _consecutiveLosses, _cooldownUntil);
                    _consecutiveLosses = 0;
                }
            }
            else if (position.NetProfit > 0)
            {
                _consecutiveLosses = 0;
                Print("WIN recorded: position {0}, net={1:F2}.", position.Id, position.NetProfit);
            }
            _partialClosed.Remove(position.Id);
        }

        private double GetBotDailyNet()
        {
            double closed = _closedToday.Sum();
            double floating = Positions.FindAll(_label).Sum(p => p.NetProfit);
            return closed + floating;
        }

        private void CloseAllBotPositions(string reason)
        {
            foreach (var position in Positions.FindAll(_label))
            {
                TradeResult close = ClosePosition(position);
                if (close.IsSuccessful)
                    Print("CLOSED position {0} ({1}).", position.Id, reason);
                else
                    Print("CLOSE FAILED position {0} ({1}): {2}.", position.Id, reason, close.Error);
            }
        }

        private void ResetDailyState(DateTime date)
        {
            _day = date;
            _dayStartBalance = Account.Balance;
            _closedToday.Clear();
            _tradesToday = 0;
            _consecutiveLosses = 0;
            _cooldownUntil = DateTime.MinValue;
            _dayLocked = false;
            _sessionSummaryWritten = false;
            _partialClosed.Clear();
            Print("NEW UTC DAY {0:yyyy-MM-dd}: reference balance={1:F2}.", _day, _dayStartBalance);
        }

        private void WriteDailySummary()
        {
            if (_sessionSummaryWritten)
                return;
            _sessionSummaryWritten = true;
            int wins = _closedToday.Count(x => x > 0);
            int losses = _closedToday.Count(x => x < 0);
            double avgWin = wins > 0 ? _closedToday.Where(x => x > 0).Average() : 0;
            double avgLoss = losses > 0 ? _closedToday.Where(x => x < 0).Average() : 0;
            int total = wins + losses;
            double winRate = total > 0 ? (double)wins / total * 100.0 : 0;
            Print("SESSION SUMMARY {0:yyyy-MM-dd} UTC | net closed={1:F2} | trades={2} | wins={3} | losses={4} | avg win={5:F2} | avg loss={6:F2} | win rate={7:F1}%.",
                _day, _closedToday.Sum(), total, wins, losses, avgWin, avgLoss, winRate);
        }

        protected override void OnStop()
        {
            Timer.Stop();
            Positions.Closed -= OnPositionClosed;
            Print("SessionScalper stopped on {0}. Open positions are not automatically closed on manual bot stop; use EndHour or close them yourself.", SymbolName);
        }
    }
}
