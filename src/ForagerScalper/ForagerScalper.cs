using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
// ---------------------------------------------------------------------------
// ForagerScalper - 1-minute multi-symbol scalping cBot for cTrader Automate.
// Strategy: 9/21 EMA cross + 50 EMA trend filter + RSI filter, ATR-based SL,
// 1:2 RR, 15-minute time exit. Auto-ranks a watchlist every 60s and trades
// the top N open, liquid, tight-spread symbols.
//
// DEFAULTS ARE SAFE: DryRun = true, AllowLiveAccount = false.
// This is a framework, not a proven edge. Test on a demo account first.
// ---------------------------------------------------------------------------
namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class ForagerScalper : Robot
    {
        #region Parameters (this is the "config file": save/load as a cTrader parameter preset)
        // ---- Safety / mode
        [Parameter("Dry run (log only, no orders)", Group = "Safety", DefaultValue = true)]
        public bool DryRun { get; set; }
        [Parameter("Allow LIVE account", Group = "Safety", DefaultValue = false)]
        public bool AllowLiveAccount { get; set; }
        [Parameter("Close all positions on stop", Group = "Safety", DefaultValue = false)]
        public bool CloseAllOnStop { get; set; }
        [Parameter("Log to file (Documents/ForagerScalper)", Group = "Safety", DefaultValue = true)]
        public bool LogToFile { get; set; }
        // ---- Universe / forager
        [Parameter("Watchlist (comma-separated, broker names)", Group = "Universe",
            DefaultValue = 
"EURUSD,GBPUSD,USDJPY,AUDUSD,USDCAD,USDCHF,NZDUSD,EURJPY,GBPJPY,XAUUSD,US500,US30,NAS100,GER40")]
        public string Watchlist { get; set; }
        [Parameter("Top N symbols to trade", Group = "Universe", DefaultValue = 5, MinValue = 1, 
MaxValue = 30)]
        public int TopN { get; set; }
        [Parameter("Re-rank interval (seconds)", Group = "Universe", DefaultValue = 60, MinValue = 
10)]
        public int RankIntervalSec { get; set; }
        [Parameter("No entries within X min of market close", Group = "Universe", DefaultValue = 20, 

MinValue = 0)]
        public int MinMinutesToClose { get; set; }
        // ---- Strategy
        [Parameter("EMA fast", Group = "Strategy", DefaultValue = 9, MinValue = 2)]
        public int EmaFastPeriod { get; set; }
        [Parameter("EMA slow", Group = "Strategy", DefaultValue = 21, MinValue = 3)]
        public int EmaSlowPeriod { get; set; }
        [Parameter("EMA trend", Group = "Strategy", DefaultValue = 50, MinValue = 5)]
        public int EmaTrendPeriod { get; set; }
        [Parameter("RSI period", Group = "Strategy", DefaultValue = 14, MinValue = 2)]
        public int RsiPeriod { get; set; }
        [Parameter("RSI long upper limit", Group = "Strategy", DefaultValue = 70)]
        public double RsiLongMax { get; set; }
        [Parameter("RSI short lower limit", Group = "Strategy", DefaultValue = 30)]
        public double RsiShortMin { get; set; }
        [Parameter("ATR period", Group = "Strategy", DefaultValue = 14, MinValue = 2)]
        public int AtrPeriod { get; set; }
        [Parameter("Stop loss (x ATR)", Group = "Strategy", DefaultValue = 1.2, MinValue = 0.2)]
        public double SlAtrMult { get; set; }
        [Parameter("Take profit (x risk distance)", Group = "Strategy", DefaultValue = 2.0, MinValue 
= 0.5)]
        public double RewardRisk { get; set; }
        [Parameter("Time exit (minutes)", Group = "Strategy", DefaultValue = 15, MinValue = 1)]
        public int MaxHoldMinutes { get; set; }
        // ---- Trailing (optional)
        [Parameter("Enable trailing stop", Group = "Trailing", DefaultValue = false)]
        public bool EnableTrailing { get; set; }
        [Parameter("Trail starts at (R multiples)", Group = "Trailing", DefaultValue = 1.0, MinValue 
= 0.2)]
        public double TrailStartR { get; set; }
        [Parameter("Trail distance (R multiples)", Group = "Trailing", DefaultValue = 0.7, MinValue 
= 0.1)]
        public double TrailDistR { get; set; }
        // ---- Risk
        [Parameter("Risk per trade (% equity)", Group = "Risk", DefaultValue = 0.5, MinValue = 0.01, 
MaxValue = 5)]
        public double RiskPct { get; set; }
        [Parameter("Max total open risk (% equity)", Group = "Risk", DefaultValue = 2.0, MinValue = 
0.1, MaxValue = 20)]
        public double MaxTotalRiskPct { get; set; }
        [Parameter("Max margin used (% equity)", Group = "Risk", DefaultValue = 30, MinValue = 1, 
MaxValue = 100)]

        public double MaxMarginPct { get; set; }
        [Parameter("Max concurrent trades", Group = "Risk", DefaultValue = 5, MinValue = 1, MaxValue 
= 50)]
        public int MaxConcurrent { get; set; }
        [Parameter("Daily loss limit (% start equity)", Group = "Risk", DefaultValue = 3.0, MinValue 
= 0.1)]
        public double DailyLossPct { get; set; }
        [Parameter("Daily trade cap", Group = "Risk", DefaultValue = 30, MinValue = 1)]
        public int DailyTradeCap { get; set; }
        [Parameter("Cooldown after loss (minutes)", Group = "Risk", DefaultValue = 20, MinValue = 
0)]
        public int CooldownMin { get; set; }
        // ---- Execution filters
        [Parameter("Max spread (% of price)", Group = "Execution", DefaultValue = 0.05, MinValue = 
0.0001)]
        public double MaxSpreadPct { get; set; }
        [Parameter("Max spread (fraction of ATR)", Group = "Execution", DefaultValue = 0.2, MinValue 
= 0.01, MaxValue = 1)]
        public double MaxSpreadAtrFrac { get; set; }
        [Parameter("Order label", Group = "Execution", DefaultValue = "FSCALP")]
        public string OrderLabel { get; set; }
        #endregion
        #region State
        private class SymCtx
        {
            public string Name;
            public Symbol Sym;
            public Bars Bars;
            public ExponentialMovingAverage Fast, Slow, Trend;
            public RelativeStrengthIndex Rsi;
            public AverageTrueRange Atr;
            public DateTime LastBarTime;
        }
        private class RankRow
        {
            public SymCtx Ctx;
            public double Liquidity, Volatility, SpreadPct, Ready, Score;
        }
        private class CloseState
        {
            public int Attempts;
            public DateTime Next;
        }
        private readonly List<SymCtx> _all = new List<SymCtx>();
        private readonly HashSet<string> _selected = new HashSet<string>();

        private readonly Dictionary<string, DateTime> _cooldownUntil = new Dictionary<string, 
DateTime>();
        private readonly Dictionary<int, CloseState> _closeState = new Dictionary<int, CloseState>
();
        private DateTime _lastRank = DateTime.MinValue;
        private DateTime _day;
        private double _dayStartEquity;
        private double _dayRealized;
        private int _dayCompleted;
        private bool _lossLimitLogged, _capLogged;
        private DateTime _pauseUntil = DateTime.MinValue;
        private string _logPath;
        private string _flagPath;
        private bool _running;
        #endregion
        protected override void OnStart()
        {
            string dir = 
Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ForagerScalper");
            try { Directory.CreateDirectory(dir); } catch { }
            _logPath = Path.Combine(dir, "ForagerScalper_log.txt");
            _flagPath = Path.Combine(dir, "STOP_NEW_ENTRIES.flag");
            Log("=== ForagerScalper starting | account " + Account.Number + " | live=" + 
Account.IsLive +
                " | dryRun=" + DryRun + " ===");
            if (Account.IsLive && !AllowLiveAccount)
            {
                Log("ABORT: this is a LIVE account and 'Allow LIVE account' is false. Stopping.");
                Stop();
                return;
            }
            if (EmaFastPeriod >= EmaSlowPeriod)
            {
                Log("ABORT: EMA fast must be smaller than EMA slow.");
                Stop();
                return;
            }
            foreach (string raw in Watchlist.Split(new[] { ',', ';' }, 
StringSplitOptions.RemoveEmptyEntries))
            {
                string name = raw.Trim();
                if (name.Length == 0 || _all.Any(x => x.Name == name)) continue;
                if (!Symbols.Exists(name)) { Log("Watchlist: '" + name + "' not found at this broker - skipped."); continue; }
                try
                {
                    Symbol sym = Symbols.GetSymbol(name);
                    Bars bars = MarketData.GetBars(TimeFrame.Minute, name);
                    for (int i = 0; i < 3 && bars.Count < 250; i++) bars.LoadMoreHistory();
                    if (bars.Count < 200) { Log("Watchlist: '" + name + "' has too little history - skipped."); continue; }

                    var c = new SymCtx
                    {
                        Name = name,
                        Sym = sym,
                        Bars = bars,
                        Fast = Indicators.ExponentialMovingAverage(bars.ClosePrices, EmaFastPeriod),
                        Slow = Indicators.ExponentialMovingAverage(bars.ClosePrices, EmaSlowPeriod),
                        Trend = Indicators.ExponentialMovingAverage(bars.ClosePrices, 
EmaTrendPeriod),
                        Rsi = Indicators.RelativeStrengthIndex(bars.ClosePrices, RsiPeriod),
                        Atr = Indicators.AverageTrueRange(bars, AtrPeriod, 
MovingAverageType.Simple),
                        LastBarTime = bars.OpenTimes.LastValue
                    };
                    _all.Add(c);
                }
                catch (Exception ex)
                {
                    Log("Watchlist: failed to load '" + name + "': " + ex.Message);
                }
            }
            if (_all.Count == 0)
            {
                Log("ABORT: no usable symbols. Fix the watchlist names to match your broker.");
                Stop();
                return;
            }
            _day = Server.Time.Date;
            _dayStartEquity = Account.Equity;
            Positions.Closed += OnPositionClosed;
            Log("Loaded " + _all.Count + " symbols. Start equity " + _dayStartEquity.ToString("F2") 
+ ".");
            _running = true;
            RankSymbols();      // immediate first ranking, no waiting
            Timer.Start(1);     // main loop, once per second
        }
        protected override void OnTimer()
        {
            if (!_running) return;
            try
            {
                RollDay();
                if (Server.Time.DayOfWeek == DayOfWeek.Saturday || Server.Time.DayOfWeek == DayOfWeek.Sunday)
                {
                    ManagePositions();
                    return; // weekday board is entry-disabled on weekends
                }
                if ((Server.Time - _lastRank).TotalSeconds >= RankIntervalSec) RankSymbols();
                ManagePositions();
                ScanSignals();
            }
            catch (Exception ex)
            {
                Log("LOOP ERROR (continuing): " + ex.Message);
            }
        }
        protected override void OnStop()

        {
            _running = false;
            Positions.Closed -= OnPositionClosed;
            Log("Stopping: no new entries will be opened.");
            if (CloseAllOnStop && !DryRun)
            {
                foreach (Position p in Positions.FindAll(OrderLabel))
                {
                    for (int attempt = 1; attempt <= 5; attempt++)
                    {
                        TradeResult r = ClosePosition(p);
                        if (r.IsSuccessful) { Log("STOP-CLOSE " + p.SymbolName + " id=" + p.Id); 
break; }
                        Log("STOP-CLOSE failed " + p.SymbolName + " attempt " + attempt + ": " + 
r.Error);
                        Thread.Sleep(500 * attempt);
                    }
                }
            }
            else
            {
                int open = Positions.FindAll(OrderLabel).Length;
                if (open > 0) Log("NOTE: " + open + " position(s) remain open with server-side SL/TP.");
            }
        }
        // ------------------------------------------------------------------
        // Daily bookkeeping
        // ------------------------------------------------------------------
        private void RollDay()
        {
            DateTime d = Server.Time.Date;
            if (d == _day) return;
            _day = d;
            _dayStartEquity = Account.Equity;
            _dayRealized = 0;
            _dayCompleted = 0;
            _lossLimitLogged = false;
            _capLogged = false;
            Log("NEW DAY: counters reset. Start equity " + _dayStartEquity.ToString("F2"));
        }
        private double FloatingPnl()
        {
            double sum = 0;
            foreach (Position p in Positions.FindAll(OrderLabel)) sum += p.NetProfit;
            return sum;
        }
        // ------------------------------------------------------------------
        // Forager: rank the watchlist, keep top N
        // ------------------------------------------------------------------
        private void RankSymbols()
        {
            _lastRank = Server.Time;
            if (Server.Time.DayOfWeek == DayOfWeek.Saturday || Server.Time.DayOfWeek == DayOfWeek.Sunday)
            {
                _selected.Clear();
                Log("WEEKDAY BOARD: weekend detected; new entries disabled.");
                return;
            }
            var rows = new List<RankRow>();

            foreach (SymCtx c in _all)
            {
                RankRow r = BuildRow(c);
                if (r != null) rows.Add(r);
            }
            _selected.Clear();
            if (rows.Count == 0)
            {
                Log("RANK: no eligible symbols right now (markets closed / spreads too wide).");
                return;
            }
            double[] liq = PercentRanks(rows.Select(x => x.Liquidity).ToArray());
            double[] vol = PercentRanks(rows.Select(x => x.Volatility).ToArray());
            double[] spr = PercentRanks(rows.Select(x => x.SpreadPct).ToArray());
            for (int i = 0; i < rows.Count; i++)
                rows[i].Score = 0.25 * (liq[i] + vol[i] + (1.0 - spr[i]) + rows[i].Ready);
            var top = rows.OrderByDescending(x => x.Score).Take(TopN).ToList();
            foreach (var t in top) _selected.Add(t.Ctx.Name);
            Log("RANK: " + string.Join(" | ", top.Select(t =>
                t.Ctx.Name + " score=" + t.Score.ToString("F2") +
                " spr%=" + t.SpreadPct.ToString("F4") + " rdy=" + t.Ready.ToString("F2"))));
        }
        private RankRow BuildRow(SymCtx c)
        {
            Symbol s = c.Sym;
            if (!s.MarketHours.IsOpened()) return null;
            TimeSpan tillClose = s.MarketHours.TimeTillClose();
            if (tillClose > TimeSpan.Zero && tillClose < TimeSpan.FromMinutes(MinMinutesToClose)) 
return null;
            // stale data / feed problem guard
            if (Server.Time - c.Bars.OpenTimes.LastValue > TimeSpan.FromMinutes(3)) return null;
            if (s.Bid <= 0 || s.Ask <= 0) return null;
            double atr = c.Atr.Result.Last(1);
            if (double.IsNaN(atr) || atr <= 0) return null;
            double spread = s.Ask - s.Bid;
            double mid = (s.Ask + s.Bid) / 2.0;
            double spreadPct = spread / mid * 100.0;
            if (spreadPct > MaxSpreadPct) return null;
            if (spread > MaxSpreadAtrFrac * atr) return null;
            // liquidity proxy: average tick volume of last 30 closed bars
            int n = Math.Min(30, c.Bars.Count - 2);
            double v = 0;
            for (int i = 1; i <= n; i++) v += c.Bars.TickVolumes.Last(i);
            v = n > 0 ? v / n : 0;
            // signal readiness: how close EMA fast/slow are to crossing, aligned with trend
            double f = c.Fast.Result.Last(0), sl = c.Slow.Result.Last(0), tr = 
c.Trend.Result.Last(0);

            double close = c.Bars.ClosePrices.Last(0);
            double gap = Math.Abs(f - sl);
            double ready = 1.0 - Math.Min(1.0, gap / Math.Max(atr, s.TickSize));
            bool pendingLong = close > tr && f < sl;
            bool pendingShort = close < tr && f > sl;
            if (!pendingLong && !pendingShort) ready *= 0.3;
            return new RankRow
            {
                Ctx = c,
                Liquidity = v,
                Volatility = atr / mid * 100.0,
                SpreadPct = spreadPct,
                Ready = ready
            };
        }
        private static double[] PercentRanks(double[] v)
        {
            int n = v.Length;
            var r = new double[n];
            if (n == 1) { r[0] = 1.0; return r; }
            for (int i = 0; i < n; i++)
            {
                int less = 0, eq = 0;
                for (int j = 0; j < n; j++)
                {
                    if (v[j] < v[i]) less++;
                    else if (j != i && v[j] == v[i]) eq++;
                }
                r[i] = (less + eq / 2.0) / (n - 1);
            }
            return r;
        }
        // ------------------------------------------------------------------
        // Signals
        // ------------------------------------------------------------------
        private void ScanSignals()
        {
            foreach (SymCtx c in _all)
            {
                DateTime t = c.Bars.OpenTimes.LastValue;
                if (t == c.LastBarTime) continue;   // no new candle yet
                c.LastBarTime = t;                  // a candle just closed -> new one opened
                if (!_selected.Contains(c.Name)) continue;
                int sig = GetSignal(c);
                if (sig != 0) TryEnter(c, sig);
            }
        }
        // Evaluated on the candle that just CLOSED (index 1); entry is at the new candle's open.
        private int GetSignal(SymCtx c)
        {
            double close = c.Bars.ClosePrices.Last(1);
            double trend = c.Trend.Result.Last(1);

            double f1 = c.Fast.Result.Last(1), f2 = c.Fast.Result.Last(2);
            double s1 = c.Slow.Result.Last(1), s2 = c.Slow.Result.Last(2);
            double rsi = c.Rsi.Result.Last(1);
            bool crossUp = f2 <= s2 && f1 > s1;
            bool crossDown = f2 >= s2 && f1 < s1;
            if (crossUp && close > trend && rsi > 50 && rsi < RsiLongMax) return 1;
            if (crossDown && close < trend && rsi < 50 && rsi > RsiShortMin) return -1;
            return 0;
        }
        // ------------------------------------------------------------------
        // Entry with all guardrails
        // ------------------------------------------------------------------
        private string EntryBlockReason(SymCtx c)
        {
            if (Server.Time.DayOfWeek == DayOfWeek.Saturday || Server.Time.DayOfWeek == DayOfWeek.Sunday)
                return "weekday board is disabled on weekends";
            if (File.Exists(_flagPath)) return "stop flag file present";
            if (Server.Time < _pauseUntil) return "paused after execution error";
            double startEq = Math.Max(_dayStartEquity, 1e-9);
            double dayPnl = _dayRealized + FloatingPnl();
            if (dayPnl / startEq * 100.0 <= -DailyLossPct)
            {
                if (!_lossLimitLogged)
                {
                    _lossLimitLogged = true;
                    Log("GUARDRAIL: daily loss limit hit (" + (dayPnl / startEq * 
100.0).ToString("F2") +
                        "%). No new entries today; existing trades keep being managed.");
                }
                return "daily loss limit";
            }
            int open = Positions.FindAll(OrderLabel).Length;
            if (_dayCompleted + open >= DailyTradeCap)
            {
                if (!_capLogged)
                {
                    _capLogged = true;
                    Log("GUARDRAIL: daily trade cap reached (" + DailyTradeCap + ").");
                }
                return "daily trade cap";
            }
            if (open >= MaxConcurrent) return "max concurrent trades";
            if (Positions.FindAll(OrderLabel, c.Name).Length > 0) return "position already open on symbol";
            DateTime cd;
            if (_cooldownUntil.TryGetValue(c.Name, out cd) && Server.Time < cd)
                return "cooldown until " + cd.ToString("HH:mm");
            if (!c.Sym.MarketHours.IsOpened()) return "market closed";
            return null;
        }
        private void TryEnter(SymCtx c, int dir)

        {
            Symbol s = c.Sym;
            TradeType type = dir > 0 ? TradeType.Buy : TradeType.Sell;
            string tag = c.Name + " " + type;
            string block = EntryBlockReason(c);
            if (block != null) { Log("SKIP " + tag + ": " + block); return; }
            double atr = c.Atr.Result.Last(1);
            double spread = s.Ask - s.Bid;
            double spreadPct = spread / ((s.Ask + s.Bid) / 2.0) * 100.0;
            if (spreadPct > MaxSpreadPct || spread > MaxSpreadAtrFrac * atr)
            {
                Log("SKIP " + tag + ": spread too wide (" + spreadPct.ToString("F4") + "% / " +
                    (spread / atr).ToString("F2") + " ATR)");
                return;
            }
            // --- SL/TP in pips from ATR
            double stopDist = SlAtrMult * atr;
            double slPips = Math.Round(stopDist / s.PipSize, 1);
            double tpPips = Math.Round(slPips * RewardRisk, 1);
            if (slPips <= 0) { Log("SKIP " + tag + ": invalid stop distance"); return; }
            // --- position size from risk
            double equity = Account.Equity;
            double riskAmount = equity * RiskPct / 100.0;
            double units = riskAmount / (slPips * s.PipValue);
            units = s.NormalizeVolumeInUnits(units, RoundingMode.Down);
            if (units < s.VolumeInUnitsMin)
            {
                Log("SKIP " + tag + ": risk budget too small for broker minimum volume (" + 
s.VolumeInUnitsMin + ")");
                return;
            }
            units = Math.Min(units, s.VolumeInUnitsMax);
            double actualRisk = units * slPips * s.PipValue;
            // --- total exposure controls
            double openRisk = OpenRiskAmount();
            if (openRisk + actualRisk > equity * MaxTotalRiskPct / 100.0)
            {
                Log("SKIP " + tag + ": total open risk cap (" + MaxTotalRiskPct + "%) would be exceeded");
                return;
            }
            double estMargin = s.GetEstimatedMargin(type, units);
            if (Account.Margin + estMargin > equity * MaxMarginPct / 100.0)
            {
                Log("SKIP " + tag + ": margin usage cap (" + MaxMarginPct + "%) would be exceeded");
                return;
            }
            string detail = tag + " units=" + units + " SL=" + slPips + "p TP=" + tpPips +
                            "p risk=" + actualRisk.ToString("F2") + " ATR=" + atr.ToString("F5") +
                            " spread%=" + spreadPct.ToString("F4");
            if (DryRun)

            {
                Log("[DRY-RUN] would ENTER " + detail);
                return;
            }
            TradeResult res = ExecuteMarketOrder(type, c.Name, units, OrderLabel, slPips, tpPips);
            if (res.IsSuccessful)
            {
                Log("ENTRY " + detail + " id=" + res.Position.Id + " fill=" + 
res.Position.EntryPrice);
                if (res.Position.VolumeInUnits < units)
                    Log("PARTIAL FILL " + c.Name + ": " + res.Position.VolumeInUnits + " of " + 
units +
                        " (SL/TP apply to filled volume)");
            }
            else
            {
                Log("ENTRY FAILED " + tag + ": " + res.Error + " - pausing entries 10s");
                _pauseUntil = Server.Time.AddSeconds(10);
            }
        }
        private double OpenRiskAmount()
        {
            double sum = 0;
            foreach (Position p in Positions.FindAll(OrderLabel))
            {
                if (!p.StopLoss.HasValue) continue;
                bool protectedPos = p.TradeType == TradeType.Buy
                    ? p.StopLoss.Value >= p.EntryPrice
                    : p.StopLoss.Value <= p.EntryPrice;
                if (protectedPos) continue; // trailed to breakeven or better = no risk left
                Symbol s = Symbols.GetSymbol(p.SymbolName);
                double pips = Math.Abs(p.EntryPrice - p.StopLoss.Value) / s.PipSize;
                sum += pips * s.PipValue * p.VolumeInUnits;
            }
            return sum;
        }
        // ------------------------------------------------------------------
        // Trade management: time exit, trailing, close retries
        // ------------------------------------------------------------------
        private void ManagePositions()
        {
            foreach (Position p in Positions.FindAll(OrderLabel))
            {
                if ((Server.Time - p.EntryTime).TotalMinutes >= MaxHoldMinutes)
                {
                    TryClose(p, "TIME_EXIT");
                    continue;
                }
                if (EnableTrailing) Trail(p);
            }
        }
        private void Trail(Position p)
        {
            if (!p.TakeProfit.HasValue) return;

            Symbol s = Symbols.GetSymbol(p.SymbolName);
            bool buy = p.TradeType == TradeType.Buy;
            // original risk distance recovered from the (never-moved) take profit
            double r0 = Math.Abs(p.TakeProfit.Value - p.EntryPrice) / RewardRisk;
            if (r0 <= 0) return;
            double price = buy ? s.Bid : s.Ask;
            double profitDist = buy ? price - p.EntryPrice : p.EntryPrice - price;
            if (profitDist < TrailStartR * r0) return;
            double newSl = Math.Round(buy ? price - TrailDistR * r0 : price + TrailDistR * r0, 
s.Digits);
            bool improves = !p.StopLoss.HasValue ||
                            (buy ? newSl > p.StopLoss.Value + 0.1 * r0 : newSl < p.StopLoss.Value - 
0.1 * r0);
            if (!improves) return;
            TradeResult r = ModifyPosition(p, newSl, p.TakeProfit);
            if (r.IsSuccessful) Log("TRAIL " + p.SymbolName + " id=" + p.Id + " SL -> " + newSl);
            else Log("TRAIL failed " + p.SymbolName + ": " + r.Error);
        }
        private void TryClose(Position p, string reason)
        {
            if (DryRun) return;
            CloseState st;
            if (!_closeState.TryGetValue(p.Id, out st))
            {
                st = new CloseState { Attempts = 0, Next = DateTime.MinValue };
                _closeState[p.Id] = st;
            }
            if (Server.Time < st.Next) return;
            TradeResult r = ClosePosition(p);
            if (r.IsSuccessful)
            {
                Log("EXIT " + reason + " " + p.SymbolName + " id=" + p.Id);
                _closeState.Remove(p.Id);
            }
            else
            {
                st.Attempts++;
                int delay = (int)Math.Min(60, Math.Pow(2, st.Attempts));  // 2,4,8,...,60s backoff
                st.Next = Server.Time.AddSeconds(delay);
                Log("CLOSE FAILED " + p.SymbolName + " id=" + p.Id + " (" + reason + ") attempt " +
                    st.Attempts + ": " + r.Error + " - retry in " + delay + "s");
            }
        }
        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            Position p = args.Position;
            if (p.Label != OrderLabel) return;
            _closeState.Remove(p.Id);
            _dayRealized += p.NetProfit;

            _dayCompleted++;
            if (p.NetProfit < 0 && CooldownMin > 0)
                _cooldownUntil[p.SymbolName] = Server.Time.AddMinutes(CooldownMin);
            Log("CLOSED " + p.SymbolName + " " + p.TradeType + " id=" + p.Id + " reason=" + 
args.Reason +
                " net=" + p.NetProfit.ToString("F2") + " | day realized=" + 
_dayRealized.ToString("F2") +
                " trades=" + _dayCompleted);
        }
        // ------------------------------------------------------------------
        private void Log(string msg)
        {
            string line = Server.Time.ToString("yyyy-MM-dd HH:mm:ss") + " | " + msg;
            Print(line);
            if (!LogToFile || _logPath == null) return;
            try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
        }
    }
}