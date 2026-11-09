// =====================================================================================
// WeekendSweepReclaim.cs  -  cTrader Automate (cAlgo) cBot
// WEEKEND CRYPTO SCALPING ONLY (BTC / ETH). Standalone, independent of any weekday bot.
// Strategy: "SWEEP & RECLAIM" = selective mean reversion on failed breakouts.
//
// NOTE: cTrader has no funding-rate or open-interest feed. Those two inputs are MANUAL
// parameters (ManualFundingRatePct, OiChange3hPct). Update them from your exchange/data source.
// Server.Time is forced to UTC via the Robot attribute.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum TriggerTf { M5, M15 }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class WeekendSweepReclaim : Robot
    {
        // ---------------- Safety ----------------
        [Parameter("Dry Run", DefaultValue = true, Group = "Safety")]
        public bool DryRun { get; set; }

        [Parameter("Allow Live Account", DefaultValue = false, Group = "Safety")]
        public bool AllowLiveAccount { get; set; }

        [Parameter("Verbose", DefaultValue = true, Group = "Safety")]
        public bool Verbose { get; set; }

        // ---------------- Strategy ----------------
        [Parameter("Allowed Symbol Keywords", DefaultValue = "BTC,ETH", Group = "Strategy")]
        public string AllowedKeywords { get; set; }

        [Parameter("Trigger Timeframe (M5/M15)", DefaultValue = TriggerTf.M5, Group = "Strategy")]
        public TriggerTf TriggerTimeframe { get; set; }

        [Parameter("Range Lookback Hours", DefaultValue = 12, MinValue = 1, MaxValue = 48, Group = "Strategy")]
        public int RangeLookbackHours { get; set; }

        [Parameter("Min Range Width %", DefaultValue = 0.4, MinValue = 0, Group = "Strategy")]
        public double MinRangeWidthPct { get; set; }

        [Parameter("Max Reclaim Bars (1 = strict wick sweep)", DefaultValue = 1, MinValue = 1, MaxValue = 4, Group = "Strategy")]
        public int MaxReclaimBars { get; set; }

        [Parameter("Max Trades Per Day", DefaultValue = 3, MinValue = 1, Group = "Strategy")]
        public int MaxTradesPerDay { get; set; }

        // ---------------- SL / TP ----------------
        [Parameter("SL Buffer % of Price", DefaultValue = 0.5, MinValue = 0.5, MaxValue = 1.0, Step = 0.05, Group = "SL/TP")]
        public double SlBufferPct { get; set; }

        [Parameter("Max SL Distance % of Price", DefaultValue = 2.5, MinValue = 0.5, Group = "SL/TP")]
        public double MaxSlPct { get; set; }

        [Parameter("Partial TP At (R)", DefaultValue = 1.0, MinValue = 0.5, Group = "SL/TP")]
        public double PartialTpR { get; set; }

        [Parameter("Final TP (R, min 2)", DefaultValue = 2.0, MinValue = 2.0, Group = "SL/TP")]
        public double FinalTpR { get; set; }

        [Parameter("Use Opposite Range Side As Final TP If Farther", DefaultValue = false, Group = "SL/TP")]
        public bool UseOppositeRangeTarget { get; set; }

        // ---------------- Risk / Filters ----------------
        [Parameter("Weekend Risk % Equity", DefaultValue = 0.25, MinValue = 0.01, MaxValue = 2.0, Step = 0.05, Group = "Risk")]
        public double WeekendRiskPct { get; set; }

        [Parameter("Max Cost To Risk Ratio", DefaultValue = 0.10, MinValue = 0.01, MaxValue = 1.0, Step = 0.01, Group = "Risk")]
        public double MaxCostToRiskRatio { get; set; }

        [Parameter("Est. Round-Trip Fee % of Price", DefaultValue = 0.06, MinValue = 0, Step = 0.01, Group = "Risk")]
        public double FeePctRoundTrip { get; set; }

        [Parameter("Avoid Funding Minutes", DefaultValue = 15, MinValue = 0, Group = "Funding/OI")]
        public int AvoidFundingMinutes { get; set; }

        [Parameter("Extreme Funding Threshold % (per 8h)", DefaultValue = 0.06, MinValue = 0, Step = 0.01, Group = "Funding/OI")]
        public double ExtremeFundingThreshold { get; set; }

        [Parameter("Manual Funding Rate % (per 8h)", DefaultValue = 0.01, Group = "Funding/OI")]
        public double ManualFundingRatePct { get; set; }

        [Parameter("Funding Settlement Hours UTC", DefaultValue = "0,8,16", Group = "Funding/OI")]
        public string FundingHoursUtc { get; set; }

        [Parameter("Manual OI Change 3h %", DefaultValue = 0.0, Group = "Funding/OI")]
        public double OiChange3hPct { get; set; }

        [Parameter("OI Compression Threshold %", DefaultValue = 3.0, MinValue = 0, Group = "Funding/OI")]
        public double OiCompressionPct { get; set; }

        [Parameter("Flat Price Hours", DefaultValue = 4, MinValue = 1, Group = "Funding/OI")]
        public int FlatPriceHours { get; set; }

        [Parameter("Flat Price Max Range %", DefaultValue = 0.6, MinValue = 0.05, Group = "Funding/OI")]
        public double FlatPriceMaxRangePct { get; set; }

        // ---------------- State ----------------
        private class SweepState
        {
            public bool Active;
            public double Extreme;
            public int BarsOutside;
            public void Reset() { Active = false; Extreme = 0; BarsOutside = 0; }
        }

        private class DayStats { public double GP, GL; public int Wins, Losses; }

        private class VTrade
        {
            public bool Long;
            public double Entry, Sl, SlB, TpA, TpB, VolA, VolB;
            public bool OpenA, OpenB, Split;
        }

        private Bars _bars;
        private bool _dry, _symbolOk, _rangeValid;
        private double _rangeHigh, _rangeLow;
        private DateTime _currentDay = DateTime.MinValue, _lastWaitLogDay = DateTime.MinValue;
        private int _tradesToday, _seq;
        private readonly SweepState _lowSweep = new SweepState();
        private readonly SweepState _highSweep = new SweepState();
        private readonly Dictionary<DateTime, DayStats> _stats = new Dictionary<DateTime, DayStats>();
        private readonly List<VTrade> _vtrades = new List<VTrade>();
        private List<int> _fundHours = new List<int>();
        private const string Prefix = "WKND_";

        private int TfMinutes { get { return TriggerTimeframe == TriggerTf.M5 ? 5 : 15; } }
        private static bool IsWeekend(DateTime t) { return t.DayOfWeek == DayOfWeek.Saturday || t.DayOfWeek == DayOfWeek.Sunday; }
        private static string F(double v) { return v.ToString("F2", CultureInfo.InvariantCulture); }
        private void Vlog(string m) { if (Verbose) Print(m); }

        protected override void OnStart()
        {
            _dry = DryRun;
            if (!DryRun && Account.IsLive && !AllowLiveAccount)
            {
                Print("SAFETY: live account detected and AllowLiveAccount=false -> forcing DRY RUN.");
                _dry = true;
            }

            var keys = (AllowedKeywords ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim().ToUpperInvariant()).ToList();
            _symbolOk = keys.Any(k => SymbolName.ToUpperInvariant().Contains(k));
            if (!_symbolOk)
                Print("SYMBOL REJECT: " + SymbolName + " is not in [" + AllowedKeywords + "]. Bot will not trade. Use BTC/ETH only.");

            _fundHours = (FundingHoursUtc ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => { int h; return int.TryParse(s.Trim(), out h) ? h : -1; })
                .Where(h => h >= 0 && h < 24).ToList();

            _bars = MarketData.GetBars(TriggerTimeframe == TriggerTf.M5 ? TimeFrame.Minute5 : TimeFrame.Minute15);
            for (int i = 0; i < 5 && _bars.Count < (RangeLookbackHours * 60 / TfMinutes) + 10; i++)
                _bars.LoadMoreHistory();
            _bars.BarOpened += OnTriggerBar;
            Positions.Closed += OnPositionClosed;

            Print("WeekendSweepReclaim started | " + SymbolName + " | TF=" + TriggerTimeframe + " | DryRun=" + _dry +
                  " | Risk=" + WeekendRiskPct + "% | RangeLookback=" + RangeLookbackHours + "h");

            RolloverIfNeeded();
            if (!IsWeekend(Server.Time))
            {
                Print("WEEKEND MODE: waiting for Saturday");
                _lastWaitLogDay = Server.Time.Date;
            }
        }

        protected override void OnStop()
        {
            if (_currentDay != DateTime.MinValue) LogBuckets(_currentDay);
        }

        protected override void OnTick()
        {
            if (_vtrades.Count > 0) ProcessVirtual();
        }

        // ------------------------------------------------------------------ bar logic
        private void OnTriggerBar(BarOpenedEventArgs e)
        {
            if (!_symbolOk) return;
            RolloverIfNeeded();

            if (!IsWeekend(Server.Time))
            {
                if (_lastWaitLogDay != Server.Time.Date)
                {
                    Print("WEEKEND MODE: waiting for Saturday");
                    _lastWaitLogDay = Server.Time.Date;
                }
                return;
            }
            if (!_rangeValid)
            {
                // retry range build (history may not have been ready on first bar of the day)
                ComputeRange();
                if (!_rangeValid) return;
            }

            int i = _bars.Count - 2; // last CLOSED trigger bar
            if (i < 1) return;
            EvaluateClosedBar(_bars[i]);
        }

        private void RolloverIfNeeded()
        {
            DateTime today = Server.Time.Date;
            if (today == _currentDay) return;
            if (_currentDay != DateTime.MinValue) LogBuckets(_currentDay);
            _currentDay = today;
            _tradesToday = 0;
            _lowSweep.Reset();
            _highSweep.Reset();
            _rangeValid = false;
            if (IsWeekend(Server.Time) && _symbolOk) ComputeRange();
        }

        // 1. DEFINE THE RANGE: High/Low of the N hours before 00:00 UTC today
        private void ComputeRange()
        {
            if (_bars == null || _bars.Count < 10) return;
            DateTime end = Server.Time.Date;
            DateTime start = end.AddHours(-RangeLookbackHours);
            double hi = double.MinValue, lo = double.MaxValue;
            int n = 0;
            for (int i = _bars.Count - 1; i >= 0; i--)
            {
                var b = _bars[i];
                if (b.OpenTime >= end) continue;
                if (b.OpenTime < start) break;
                if (b.High > hi) hi = b.High;
                if (b.Low < lo) lo = b.Low;
                n++;
            }
            int expected = RangeLookbackHours * 60 / TfMinutes;
            if (n < expected / 2)
            {
                Vlog("RANGE REJECT: only " + n + "/" + expected + " bars of history for " + SymbolName + " (increase chart history).");
                return;
            }
            double mid = (hi + lo) / 2.0;
            double widthPct = (hi - lo) / mid * 100.0;
            if (widthPct < MinRangeWidthPct)
            {
                Vlog("RANGE REJECT: width " + F(widthPct) + "% < MinRangeWidthPct " + F(MinRangeWidthPct) + "%");
                return;
            }
            _rangeHigh = hi; _rangeLow = lo; _rangeValid = true;
            Print("RANGE SET: " + SymbolName + " High=" + hi.ToString(CultureInfo.InvariantCulture) +
                  " Low=" + lo.ToString(CultureInfo.InvariantCulture) + " (" + RangeLookbackHours + "h before " +
                  end.ToString("yyyy-MM-dd HH:mm") + " UTC, width " + F(widthPct) + "%)");
        }

        // 2 + 3. SWEEP DETECTION AND RECLAIM TRIGGER (evaluated on the closed bar; entry at next open = now)
        private void EvaluateClosedBar(Bar b)
        {
            if (b.OpenTime < _currentDay) return; // ignore bars from the previous day

            bool lowBreach = b.Low < _rangeLow;
            bool highBreach = b.High > _rangeHigh;

            if (lowBreach && highBreach)
            {
                Vlog("SKIP: bar swept BOTH sides of range (ambiguous / volatile). State reset.");
                _lowSweep.Reset(); _highSweep.Reset();
                return;
            }

            // ---- LOW side -> LONG
            if (lowBreach)
            {
                if (!_lowSweep.Active)
                {
                    _lowSweep.Active = true; _lowSweep.Extreme = b.Low; _lowSweep.BarsOutside = 0;
                    Print("SWEEP DETECTED: " + SymbolName + " swept Low at price " + b.Low.ToString(CultureInfo.InvariantCulture));
                }
                else _lowSweep.Extreme = Math.Min(_lowSweep.Extreme, b.Low);
            }
            if (_lowSweep.Active)
            {
                if (b.Close >= _rangeLow && b.Close < _rangeHigh)
                {
                    Print("RECLAIM CONFIRMED: " + SymbolName + " LONG, closed " + b.Close.ToString(CultureInfo.InvariantCulture) +
                          " back above Range Low " + _rangeLow.ToString(CultureInfo.InvariantCulture));
                    double ext = _lowSweep.Extreme;
                    _lowSweep.Reset();
                    TryEnter(true, ext);
                }
                else
                {
                    _lowSweep.BarsOutside++;
                    if (_lowSweep.BarsOutside >= MaxReclaimBars)
                    {
                        Vlog("SWEEP CANCELLED (Low): closed beyond range for " + _lowSweep.BarsOutside + " bar(s) = genuine breakout, NOT traded.");
                        _lowSweep.Reset();
                    }
                }
            }

            // ---- HIGH side -> SHORT
            if (highBreach)
            {
                if (!_highSweep.Active)
                {
                    _highSweep.Active = true; _highSweep.Extreme = b.High; _highSweep.BarsOutside = 0;
                    Print("SWEEP DETECTED: " + SymbolName + " swept High at price " + b.High.ToString(CultureInfo.InvariantCulture));
                }
                else _highSweep.Extreme = Math.Max(_highSweep.Extreme, b.High);
            }
            if (_highSweep.Active)
            {
                if (b.Close <= _rangeHigh && b.Close > _rangeLow)
                {
                    Print("RECLAIM CONFIRMED: " + SymbolName + " SHORT, closed " + b.Close.ToString(CultureInfo.InvariantCulture) +
                          " back below Range High " + _rangeHigh.ToString(CultureInfo.InvariantCulture));
                    double ext = _highSweep.Extreme;
                    _highSweep.Reset();
                    TryEnter(false, ext);
                }
                else
                {
                    _highSweep.BarsOutside++;
                    if (_highSweep.BarsOutside >= MaxReclaimBars)
                    {
                        Vlog("SWEEP CANCELLED (High): closed beyond range for " + _highSweep.BarsOutside + " bar(s) = genuine breakout, NOT traded.");
                        _highSweep.Reset();
                    }
                }
            }
        }

        // ------------------------------------------------------------------ entry
        private void TryEnter(bool isLong, double sweepExtreme)
        {
            string side = isLong ? "LONG" : "SHORT";

            if (_tradesToday >= MaxTradesPerDay) { Vlog("REJECT " + side + ": MaxTradesPerDay reached."); return; }
            if (InTrade()) { Vlog("REJECT " + side + ": a trade is already open (no stacking/averaging)."); return; }

            string why;
            if (FundingBlocked(out why)) { Vlog("FUNDING REJECT: " + why); return; }

            double entry = isLong ? Symbol.Ask : Symbol.Bid;

            // Stop-loss beyond the sweep wick + % buffer
            double buffer = entry * SlBufferPct / 100.0;
            double sl = isLong ? sweepExtreme - buffer : sweepExtreme + buffer;
            double slDist = Math.Abs(entry - sl);
            if (slDist <= 0) return;

            if (slDist / entry * 100.0 > MaxSlPct)
            {
                Vlog("SL REJECT: SL distance " + F(slDist / entry * 100.0) + "% > MaxSlPct " + F(MaxSlPct) + "%");
                return;
            }

            // 5. COST-TO-TARGET (non-negotiable): friction / SL distance
            double spread = Symbol.Ask - Symbol.Bid;
            double fees = entry * FeePctRoundTrip / 100.0;
            double ratio = (spread + fees) / slDist;
            if (ratio > MaxCostToRiskRatio)
            {
                Vlog("COST REJECT: friction/risk = " + F(ratio) + " > " + F(MaxCostToRiskRatio) +
                     " (spread " + spread.ToString("G6", CultureInfo.InvariantCulture) + " + fees " + fees.ToString("G6", CultureInfo.InvariantCulture) +
                     " vs SL dist " + slDist.ToString("G6", CultureInfo.InvariantCulture) + ")");
                return;
            }

            // Targets
            double dir = isLong ? 1.0 : -1.0;
            double tp1 = entry + dir * slDist * PartialTpR;
            double tp2 = entry + dir * slDist * FinalTpR;
            if (UseOppositeRangeTarget)
            {
                double opp = isLong ? _rangeHigh : _rangeLow;
                if (Math.Abs(opp - entry) > Math.Abs(tp2 - entry) && ((opp - entry) * dir > 0)) tp2 = opp;
            }

            // 7. Position sizing from SL distance
            double slPips = slDist / Symbol.PipSize;
            double riskMoney = Account.Equity * WeekendRiskPct / 100.0;
            double rawUnits = riskMoney / (slPips * Symbol.PipValue);
            double vol = Symbol.NormalizeVolumeInUnits(rawUnits, RoundingMode.Down);
            if (vol < Symbol.VolumeInUnitsMin)
            {
                Vlog("SIZE REJECT: risk-based volume " + rawUnits.ToString("G6", CultureInfo.InvariantCulture) +
                     " < broker minimum " + Symbol.VolumeInUnitsMin + " (reduce SL distance or raise risk).");
                return;
            }
            double volA = Symbol.NormalizeVolumeInUnits(vol / 2.0, RoundingMode.Down);
            double volB = Symbol.NormalizeVolumeInUnits(vol - volA, RoundingMode.Down);
            bool split = volA >= Symbol.VolumeInUnitsMin && volB >= Symbol.VolumeInUnitsMin;
            if (!split) { volA = vol; volB = 0; }

            bool compression = IsCompression();
            string reason = "Sweep&Reclaim " + side + " | extreme=" + sweepExtreme.ToString(CultureInfo.InvariantCulture) +
                            " entry~" + entry.ToString(CultureInfo.InvariantCulture) +
                            " SL=" + sl.ToString(CultureInfo.InvariantCulture) +
                            " TP1(" + F(PartialTpR) + "R)=" + tp1.ToString(CultureInfo.InvariantCulture) +
                            " TP2(" + F(FinalTpR) + "R)=" + tp2.ToString(CultureInfo.InvariantCulture) +
                            " vol=" + vol + (split ? " (50/50 split)" : " (single, cannot split)") +
                            " cost/risk=" + F(ratio) + (compression ? " | after OI compression" : "");

            double tpA = split ? tp1 : tp2;

            if (_dry)
            {
                Print("ENTRY (DRY RUN): " + reason);
                _vtrades.Add(new VTrade
                {
                    Long = isLong, Entry = entry, Sl = sl, SlB = sl, TpA = tpA, TpB = tp2,
                    VolA = volA, VolB = volB, OpenA = true, OpenB = split, Split = split
                });
                _tradesToday++;
                return;
            }

            string baseLabel = Prefix + Server.Time.ToString("MMddHHmmss") + "_" + (++_seq);
            TradeType tt = isLong ? TradeType.Buy : TradeType.Sell;
            double slPipsOut = slDist / Symbol.PipSize;

            var rA = ExecuteMarketOrder(tt, SymbolName, volA, baseLabel + "_A", slPipsOut, Math.Abs(tpA - entry) / Symbol.PipSize);
            if (!rA.IsSuccessful) { Print("ENTRY FAILED (A): " + rA.Error); return; }
            if (split)
            {
                var rB = ExecuteMarketOrder(tt, SymbolName, volB, baseLabel + "_B", slPipsOut, Math.Abs(tp2 - entry) / Symbol.PipSize);
                if (!rB.IsSuccessful) Print("ENTRY FAILED (B): " + rB.Error);
            }
            _tradesToday++;
            Print("ENTRY: " + reason);
        }

        private bool InTrade()
        {
            if (_vtrades.Count > 0) return true;
            return Positions.Any(p => p.SymbolName == SymbolName && p.Label != null && p.Label.StartsWith(Prefix));
        }

        // 6. FUNDING FILTER
        private bool FundingBlocked(out string why)
        {
            why = "";
            if (Math.Abs(ManualFundingRatePct) <= ExtremeFundingThreshold) return false;
            DateTime now = Server.Time;
            foreach (int off in new[] { -1, 0, 1 })
            {
                foreach (int h in _fundHours)
                {
                    DateTime t = now.Date.AddDays(off).AddHours(h);
                    double diff = Math.Abs((now - t).TotalMinutes);
                    if (diff <= AvoidFundingMinutes)
                    {
                        why = "funding " + F(ManualFundingRatePct) + "% exceeds " + F(ExtremeFundingThreshold) +
                              "% and settlement " + t.ToString("HH:mm") + " UTC is " + F(diff) + " min away";
                        return true;
                    }
                }
            }
            return false;
        }

        // 6b. OI compression (OI +3% with flat price). Entries only ever occur on sweep+reclaim, so compression
        // itself is never traded; this flags that the setup arrived after compression.
        private bool IsCompression()
        {
            if (OiChange3hPct < OiCompressionPct) return false;
            int count = FlatPriceHours * 60 / TfMinutes;
            if (_bars.Count < count + 2) return false;
            double hi = double.MinValue, lo = double.MaxValue;
            for (int i = _bars.Count - 2; i >= _bars.Count - 1 - count; i--)
            {
                if (_bars[i].High > hi) hi = _bars[i].High;
                if (_bars[i].Low < lo) lo = _bars[i].Low;
            }
            double rangePct = (hi - lo) / ((hi + lo) / 2.0) * 100.0;
            bool flat = rangePct <= FlatPriceMaxRangePct;
            if (flat) Print("COMPRESSION: OI +" + F(OiChange3hPct) + "% with flat price (" + F(rangePct) + "% over " + FlatPriceHours + "h). Compression not traded; sweep+reclaim satisfied.");
            return flat;
        }

        // ------------------------------------------------------------------ live management / results
        private void OnPositionClosed(PositionClosedEventArgs a)
        {
            var p = a.Position;
            if (p.Label == null || !p.Label.StartsWith(Prefix)) return;
            RecordResult(Server.Time.Date, p.NetProfit);
            Print("CLOSED " + p.Label + " " + a.Reason + " net=$" + F(p.NetProfit));

            // Leg A hit TP1 -> breakeven on leg B (remainder)
            if (p.Label.EndsWith("_A") && a.Reason == PositionCloseReason.TakeProfit)
            {
                string bLabel = p.Label.Substring(0, p.Label.Length - 2) + "_B";
                var b = Positions.FirstOrDefault(x => x.Label == bLabel);
                if (b != null)
                {
                    var r = ModifyPosition(b, b.EntryPrice, b.TakeProfit);
                    Print(r.IsSuccessful ? "PARTIAL TP HIT: stop moved to breakeven on remainder " + bLabel
                                         : "BE MOVE FAILED: " + r.Error);
                }
            }
        }

        // ------------------------------------------------------------------ dry-run simulation
        private void ProcessVirtual()
        {
            for (int i = _vtrades.Count - 1; i >= 0; i--)
            {
                var v = _vtrades[i];
                double px = v.Long ? Symbol.Bid : Symbol.Ask;

                if (v.OpenA)
                {
                    bool slHit = v.Long ? px <= v.Sl : px >= v.Sl;
                    bool tpHit = v.Long ? px >= v.TpA : px <= v.TpA;
                    if (slHit) CloseVLeg(v, "A", v.Sl, v.VolA, "SL");
                    else if (tpHit)
                    {
                        CloseVLeg(v, "A", v.TpA, v.VolA, "TP");
                        if (v.Split) { v.SlB = v.Entry; Print("PARTIAL TP HIT (DRY): stop moved to breakeven on remainder."); }
                    }
                    if (slHit || tpHit) v.OpenA = false;
                }
                if (v.OpenB)
                {
                    bool slHit = v.Long ? px <= v.SlB : px >= v.SlB;
                    bool tpHit = v.Long ? px >= v.TpB : px <= v.TpB;
                    if (slHit) { CloseVLeg(v, "B", v.SlB, v.VolB, "SL/BE"); v.OpenB = false; }
                    else if (tpHit) { CloseVLeg(v, "B", v.TpB, v.VolB, "TP"); v.OpenB = false; }
                }
                // if leg A stopped out, leg B shares the original stop: close it too
                if (!v.OpenA && v.OpenB && v.SlB == v.Sl)
                {
                    bool slHit = v.Long ? px <= v.SlB : px >= v.SlB;
                    if (slHit) { CloseVLeg(v, "B", v.SlB, v.VolB, "SL"); v.OpenB = false; }
                }
                if (!v.OpenA && !v.OpenB) _vtrades.RemoveAt(i);
            }
        }

        private void CloseVLeg(VTrade v, string leg, double exit, double vol, string why)
        {
            double diff = v.Long ? exit - v.Entry : v.Entry - exit;
            double pnl = diff / Symbol.PipSize * Symbol.PipValue * vol;
            double cost = v.Entry * FeePctRoundTrip / 100.0 / Symbol.PipSize * Symbol.PipValue * vol;
            pnl -= cost;
            RecordResult(Server.Time.Date, pnl);
            Print("CLOSED (DRY) leg " + leg + " " + why + " net=$" + F(pnl));
        }

        // ------------------------------------------------------------------ logging
        private void RecordResult(DateTime day, double pnl)
        {
            DayStats s;
            if (!_stats.TryGetValue(day, out s)) { s = new DayStats(); _stats[day] = s; }
            if (pnl > 0) { s.GP += pnl; s.Wins++; }
            else { s.GL += -pnl; s.Losses++; }
        }

        private void LogBuckets(DateTime day)
        {
            DayStats s;
            if (!_stats.TryGetValue(day, out s)) return;
            int n = s.Wins + s.Losses;
            double net = s.GP - s.GL;
            string pf = s.GL > 0 ? F(s.GP / s.GL) : (s.GP > 0 ? "inf" : "0.00");
            string exp = n > 0 ? F(net / n) : "0.00";
            Print("WEEKEND BUCKETS: Profit=$" + F(s.GP) + " (" + s.Wins + " wins) | Loss=$" + F(s.GL) + " (" + s.Losses +
                  " losses) | Net=$" + F(net) + " | PF=" + pf + " | Exp=" + exp + " | Day=" + day.ToString("yyyy-MM-dd"));
        }
    }
}