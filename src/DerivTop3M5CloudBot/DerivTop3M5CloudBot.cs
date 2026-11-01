using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class DerivTop3M5CloudBot : Robot
    {
        [Parameter("Symbols (exact broker names)", DefaultValue = "BTCUSD,ETHUSD,Volatility 5 Index,Volatility 10 Index,Volatility 15 Index,Volatility 15 (1s) Index,Volatility 25 Index")]
        public string SymbolsCsv { get; set; }

        [Parameter("Volume (units)", DefaultValue = 0, MinValue = 0)]
        public double RequestedVolume { get; set; }

        [Parameter("Maximum open trades (global)", DefaultValue = 7, MinValue = 1, MaxValue = 7)]
        public int MaximumOpenTrades { get; set; }

        [Parameter("Profit close (account currency)", DefaultValue = 0.75, MinValue = 0.75)]
        public double ProfitClose { get; set; }

        [Parameter("Loss close (account currency)", DefaultValue = 0.25, MinValue = 0.25)]
        public double LossClose { get; set; }

        [Parameter("Pullback percentage", DefaultValue = 25, MinValue = 1, MaxValue = 99)]
        public double PullbackPercent { get; set; }

        private const string BotLabel = "D3M5";
        private readonly Dictionary<string, MarketState> _markets = new Dictionary<string, MarketState>();

        private sealed class MarketState
        {
            public string Name;
            public Symbol Symbol;
            public Bars Bars;
            public DateTime BarOpen;
            public DateTime LastEntryBar = DateTime.MinValue;
            public bool ReentryAllowed;
            public double BestFavourablePrice;
            public bool Tracking;
        }

        protected override void OnStart()
        {
            foreach (var raw in SymbolsCsv.Split(','))
            {
                var name = raw.Trim();
                if (string.IsNullOrWhiteSpace(name) || _markets.ContainsKey(name))
                    continue;

                var symbol = Symbols.GetSymbol(name);
                if (symbol == null)
                {
                    Print("SYMBOL NOT FOUND - skipped, no substitution: {0}", name);
                    continue;
                }

                var bars = MarketData.GetBars(TimeFrame.Minute5, name);
                if (bars == null || bars.Count == 0)
                {
                    Print("M5 DATA NOT AVAILABLE - skipped: {0}", name);
                    continue;
                }

                _markets.Add(name, new MarketState
                {
                    Name = name,
                    Symbol = symbol,
                    Bars = bars,
                    BarOpen = bars.OpenTimes.LastValue
                });
                Print("Monitoring exact symbol {0} on M5 only", name);
            }

            if (_markets.Count == 0)
            {
                Print("No configured symbols are available. Check exact cTrader symbol names.");
                Stop();
                return;
            }

            Timer.Start(1);
            Print("Single-engine M5 candle bot started. Profit close +{0}; loss close -{1}; pullback {2}%; global cap {3}.",
                ProfitClose, LossClose, PullbackPercent, MaximumOpenTrades);
            Print("Money thresholds are in the trading account currency. No broker-side SL/TP is attached. DEMO ONLY.");
        }

        protected override void OnTimer()
        {
            foreach (var state in _markets.Values)
            {
                try
                {
                    ProcessMarket(state);
                }
                catch (Exception ex)
                {
                    Print("ERROR {0}: {1}", state.Name, ex.Message);
                }
            }
        }

        private void ProcessMarket(MarketState s)
        {
            if (s.Bars.Count == 0)
                return;

            var currentBarOpen = s.Bars.OpenTimes.LastValue;
            if (currentBarOpen != s.BarOpen)
            {
                s.BarOpen = currentBarOpen;
                s.LastEntryBar = DateTime.MinValue;
                s.ReentryAllowed = false;
                s.Tracking = false;
            }

            var position = FindBotPositionForSymbol(s.Name);
            if (position != null)
            {
                ManagePosition(s, position);
                return;
            }

            s.Tracking = false;
            var elapsed = (Server.Time - currentBarOpen).TotalSeconds;
            if (elapsed < 30 || elapsed >= 300)
                return;

            // First entry is made after observing 30-60 seconds. After an exit, allow a fresh
            // direction check and re-entry during the same still-forming M5 candle.
            if (s.LastEntryBar == currentBarOpen && !s.ReentryAllowed)
                return;
            if (s.LastEntryBar != currentBarOpen && elapsed > 60)
                return;
            TryEntry(s, currentBarOpen);
        }

        private void TryEntry(MarketState s, DateTime currentBarOpen)
        {
            if (FindBotPositionForSymbol(s.Name) != null)
                return;

            if (CountBotPositions() >= Math.Min(7, MaximumOpenTrades))
                return;

            var candleOpen = s.Bars.OpenPrices.LastValue;
            var current = s.Symbol.Bid;
            if (current == candleOpen)
                return;

            var direction = current > candleOpen ? TradeType.Buy : TradeType.Sell;
            var requested = RequestedVolume <= 0 ? s.Symbol.VolumeInUnitsMin : RequestedVolume;
            var volume = s.Symbol.NormalizeVolumeInUnits(requested, RoundingMode.Down);

            if (volume < s.Symbol.VolumeInUnitsMin || volume > s.Symbol.VolumeInUnitsMax)
            {
                Print("{0}: invalid volume; no entry.", s.Name);
                return;
            }

            var label = MakeLabel(s.Name);
            var result = ExecuteMarketOrder(direction, s.Name, volume, label, (double?)null, (double?)null);
            if (!result.IsSuccessful || result.Position == null)
            {
                Print("{0}: {1} entry failed: {2}", s.Name, direction, result.Error);
                return;
            }

            s.LastEntryBar = currentBarOpen;
            s.ReentryAllowed = false;
            s.BestFavourablePrice = result.Position.EntryPrice;
            s.Tracking = true;
            Print("{0}: opened {1} from developing M5 candle at {2}. Open bot positions: {3}/{4}.",
                s.Name, direction, result.Position.EntryPrice, CountBotPositions(), Math.Min(7, MaximumOpenTrades));
        }

        private void ManagePosition(MarketState s, Position position)
        {
            // NetProfit includes trading costs where supported by the broker.
            var net = position.NetProfit;
            if (net <= -Math.Abs(LossClose))
            {
                CloseAndReport(s, position, "loss threshold reached");
                return;
            }

            if (net >= Math.Abs(ProfitClose))
            {
                CloseAndReport(s, position, "profit target reached");
                return;
            }

            var executable = position.TradeType == TradeType.Buy ? s.Symbol.Bid : s.Symbol.Ask;
            if (!s.Tracking)
            {
                s.BestFavourablePrice = position.EntryPrice;
                s.Tracking = true;
            }

            if (position.TradeType == TradeType.Buy)
                s.BestFavourablePrice = Math.Max(s.BestFavourablePrice, executable);
            else
                s.BestFavourablePrice = Math.Min(s.BestFavourablePrice, executable);

            var range = s.Bars.HighPrices.LastValue - s.Bars.LowPrices.LastValue;
            if (range <= 0)
                return;

            var pullbackDistance = range * (PullbackPercent / 100.0);
            var pullbackHit = position.TradeType == TradeType.Buy
                ? s.BestFavourablePrice > position.EntryPrice && executable <= s.BestFavourablePrice - pullbackDistance
                : s.BestFavourablePrice < position.EntryPrice && executable >= s.BestFavourablePrice + pullbackDistance;

            if (pullbackHit)
                CloseAndReport(s, position, PullbackPercent + "% favourable-move pullback");
        }

        private void CloseAndReport(MarketState s, Position position, string reason)
        {
            var beforeCloseNet = position.NetProfit;
            var result = ClosePosition(position);
            if (result.IsSuccessful)
            {
                s.Tracking = false;
                s.ReentryAllowed = true;
                Print("{0}: closed ({1}). Net P/L at close request: {2}. Re-entry will only be considered from the live M5 candle direction, subject to the 7-trade cap.",
                    s.Name, reason, beforeCloseNet);
            }
            else
            {
                Print("{0}: close FAILED ({1}): {2}. Position remains open and will be checked again.", s.Name, reason, result.Error);
            }
        }

        private Position FindBotPositionForSymbol(string symbolName)
        {
            return Positions.FirstOrDefault(p =>
                p.SymbolName == symbolName &&
                p.Label != null &&
                p.Label.StartsWith(BotLabel + "_", StringComparison.Ordinal));
        }

        private int CountBotPositions()
        {
            return Positions.Count(p =>
                p.Label != null &&
                p.Label.StartsWith(BotLabel + "_", StringComparison.Ordinal));
        }

        private string MakeLabel(string symbolName)
        {
            var safe = new string(symbolName.Where(char.IsLetterOrDigit).ToArray());
            return BotLabel + "_" + safe;
        }
    }
}