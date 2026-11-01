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

        [Parameter("Volume (units per strategy trade)", DefaultValue = 0, MinValue = 0)]
        public double RequestedVolume { get; set; }

        [Parameter("75% engine enabled", DefaultValue = true)]
        public bool Enable75 { get; set; }

        [Parameter("25% engine enabled", DefaultValue = true)]
        public bool Enable25 { get; set; }

        private const string BotLabel = "D3M5";
        private readonly Dictionary<string, MarketState> _markets = new Dictionary<string, MarketState>();

        private sealed class MarketState
        {
            public string Name;
            public Symbol Symbol;
            public Bars Bars;
            public DateTime BarOpen;
            public DateTime Last75Attempt = DateTime.MinValue;
            public DateTime Last25Attempt = DateTime.MinValue;
            public double EntryPrice25;
            public double BestPrice25;
            public TradeType Direction25;
            public bool Tracking25;
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

                var state = new MarketState
                {
                    Name = name,
                    Symbol = symbol,
                    Bars = bars,
                    BarOpen = bars.OpenTimes.LastValue
                };

                _markets.Add(name, state);
                Print("Monitoring {0} on M5", name);
            }

            if (_markets.Count == 0)
            {
                Print("No configured symbols are available. Check exact cTrader symbol names.");
                Stop();
                return;
            }

            Timer.Start(1);
            Print("DerivTop3M5CloudBot started. No stop-loss is attached. DEMO TESTING ONLY.");
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
                s.Tracking25 = false;
            }

            var now = Server.Time;
            var elapsed = (now - currentBarOpen).TotalSeconds;
            if (elapsed < 0)
                return;

            // Engine 75: sample the developing candle at 30 seconds.
            // If it is still exactly flat at 30s, allow one final decision at 60s.
            if (Enable75 && s.Last75Attempt != currentBarOpen)
            {
                if (elapsed >= 30 && elapsed < 60)
                    Try75Entry(s, currentBarOpen);
                else if (elapsed >= 60)
                    Try75Entry(s, currentBarOpen);
            }

            // Engine 25: enter on the first observable non-zero movement from candle open.
            if (Enable25 && s.Last25Attempt != currentBarOpen && elapsed < 300)
                Try25Entry(s, currentBarOpen);

            Manage75Exit(s, currentBarOpen, now);
            Manage25Exit(s);
        }

        private void Try75Entry(MarketState s, DateTime barOpen)
        {
            var open = s.Bars.OpenPrices.LastValue;
            var current = s.Symbol.Bid;
            if (current == open)
            {
                if ((Server.Time - barOpen).TotalSeconds < 60)
                    return;

                s.Last75Attempt = barOpen;
                Print("{0} 75% engine: candle remained flat at 60s; no entry this candle.", s.Name);
                return;
            }

            var direction = current > open ? TradeType.Buy : TradeType.Sell;
            s.Last75Attempt = barOpen;
            OpenOwnedPosition(s, direction, "75");
        }

        private void Try25Entry(MarketState s, DateTime barOpen)
        {
            var open = s.Bars.OpenPrices.LastValue;
            var current = s.Symbol.Bid;
            if (current == open)
                return;

            var direction = current > open ? TradeType.Buy : TradeType.Sell;
            s.Last25Attempt = barOpen;

            var result = OpenOwnedPosition(s, direction, "25");
            if (result != null && result.IsSuccessful && result.Position != null)
            {
                s.Direction25 = direction;
                s.EntryPrice25 = result.Position.EntryPrice;
                s.BestPrice25 = result.Position.EntryPrice;
                s.Tracking25 = true;
            }
        }

        private TradeResult OpenOwnedPosition(MarketState s, TradeType direction, string engine)
        {
            var label = MakeLabel(s.Name, engine);
            if (Positions.FindAll(label, s.Name).Length > 0)
            {
                Print("{0} {1}% engine already has an open position; no duplicate entry.", s.Name, engine);
                return null;
            }

            var requested = RequestedVolume <= 0 ? s.Symbol.VolumeInUnitsMin : RequestedVolume;
            var volume = s.Symbol.NormalizeVolumeInUnits(requested, RoundingMode.Down);
            if (volume < s.Symbol.VolumeInUnitsMin || volume > s.Symbol.VolumeInUnitsMax)
            {
                Print("{0} {1}% engine: requested volume is invalid for this symbol. No trade opened.", s.Name, engine);
                return null;
            }

            // No SL or TP is attached: exits are controlled by the agreed M5 strategy rules.
            var result = ExecuteMarketOrder(direction, s.Name, volume, label, null, null);
            if (!result.IsSuccessful)
                Print("{0} {1}% order failed: {2}", s.Name, engine, result.Error);
            else
                Print("{0} {1}% opened {2} at {3}", s.Name, engine, direction, result.Position.EntryPrice);

            return result;
        }

        private void Manage75Exit(MarketState s, DateTime barOpen, DateTime now)
        {
            foreach (var position in Positions.FindAll(MakeLabel(s.Name, "75"), s.Name))
            {
                var originatingOpen = GetM5Open(position.EntryTime);
                if (now < originatingOpen.AddMinutes(5))
                    continue;

                var result = ClosePosition(position);
                if (result.IsSuccessful)
                    Print("{0} 75% engine closed at originating M5 candle end.", s.Name);
                else
                    Print("{0} 75% close failed: {1}", s.Name, result.Error);
            }
        }

        private void Manage25Exit(MarketState s)
        {
            var positions = Positions.FindAll(MakeLabel(s.Name, "25"), s.Name);
            if (positions.Length == 0)
            {
                s.Tracking25 = false;
                return;
            }

            var position = positions[0];
            if (Server.Time >= GetM5Open(position.EntryTime).AddMinutes(5))
            {
                var timeExit = ClosePosition(position);
                if (timeExit.IsSuccessful)
                    Print("{0} 25% engine closed at originating M5 candle end (pullback not reached).", s.Name);
                else
                    Print("{0} 25% candle-end close failed: {1}", s.Name, timeExit.Error);
                s.Tracking25 = false;
                return;
            }

            var bid = s.Symbol.Bid;
            var ask = s.Symbol.Ask;
            var executable = position.TradeType == TradeType.Buy ? bid : ask;

            if (!s.Tracking25)
            {
                s.Direction25 = position.TradeType;
                s.EntryPrice25 = position.EntryPrice;
                s.BestPrice25 = executable;
                s.Tracking25 = true;
            }

            if (position.TradeType == TradeType.Buy)
                s.BestPrice25 = Math.Max(s.BestPrice25, executable);
            else
                s.BestPrice25 = Math.Min(s.BestPrice25, executable);

            var candleHigh = s.Bars.HighPrices.LastValue;
            var candleLow = s.Bars.LowPrices.LastValue;
            var developingRange = candleHigh - candleLow;
            if (developingRange <= 0)
                return;

            var pullback = developingRange * 0.25;
            var triggered = position.TradeType == TradeType.Buy
                ? executable <= s.BestPrice25 - pullback && s.BestPrice25 > s.EntryPrice25
                : executable >= s.BestPrice25 + pullback && s.BestPrice25 < s.EntryPrice25;

            if (!triggered)
                return;

            var result = ClosePosition(position);
            if (result.IsSuccessful)
            {
                s.Tracking25 = false;
                Print("{0} 25% engine closed after 25% pullback of developing M5 range.", s.Name);
            }
            else
            {
                Print("{0} 25% close failed: {1}", s.Name, result.Error);
            }
        }

        private DateTime GetM5Open(DateTime time)
        {
            var minute = (time.Minute / 5) * 5;
            return new DateTime(time.Year, time.Month, time.Day, time.Hour, minute, 0, time.Kind);
        }

        private string MakeLabel(string symbolName, string engine)
        {
            var safe = new string(symbolName.Where(char.IsLetterOrDigit).ToArray());
            return BotLabel + "_" + safe + "_" + engine;
        }
    }
}
