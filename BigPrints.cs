#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

// BigPrints v1.0.0
// Flags large aggressive market orders (buy/sell sweeps) on the Level-I tape and marks
// them on the chart at the price they printed, with the swept contract count.
//
// REAL-TIME / MARKET REPLAY ONLY. OnMarketData does not fire on historical data (NT8
// default), so this indicator shows nothing on a historical chart load — only prints
// that occur while the chart is live or replaying. No historical reconstruction is
// attempted; that is out of scope by design (see nt8-indicator: onmarketdata caveats).
namespace NinjaTrader.NinjaScript.Indicators
{
    public class BigPrints : Indicator
    {
        // Latest inside market, updated from the same OnMarketData stream as Last prints.
        private double _bid;
        private double _ask;

        // Active cluster being accumulated (sweep of same-side prints in quick succession).
        private bool     _clusterOpen;
        private bool     _clusterIsBuy;
        private long     _clusterVolume;
        private double   _clusterPrice;      // price of the largest single print in the cluster
        private long     _clusterMaxPrint;   // volume of that largest print, to pick the anchor price
        private DateTime _clusterMaxTime;    // time of that largest print — the real anchor for the marker
        private DateTime _clusterLastTime;
        private DateTime _clusterStartTime;

        private int _tagCounter;

        // ponytail: hard cap on live draw objects — a slow steady same-side drip (prints every
        // ~150ms for minutes) would otherwise merge into one giant blob AND draw objects are
        // never removed, so chart memory grows without bound over a session. 400 clusters is
        // several hours of ES 150+ contract sweeps at normal cadence; raise if that's not enough.
        private const int MaxClusterSpanMs = 1500;
        private const int MaxDrawObjects   = 400;
        private readonly Queue<int> _drawnClusterTags = new Queue<int>();

        private Brush _buyBrush  = Brushes.Lime;
        private Brush _sellBrush = Brushes.Red;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                     = "BigPrints";
                Description              = "Marks large aggressive buy/sell prints (sweeps) on the tape, real-time / Market Replay only.";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DisplayInDataBox         = false;
                IsSuspendedWhileInactive = false;

                MinVolume           = 150;
                ClusterMilliseconds = 150;
                TextSize            = 14;
            }
            else if (State == State.Configure)
            {
                // no-op: no additional data series needed
            }
            else if (State == State.DataLoaded)
            {
                _bid = 0;
                _ask = 0;
                _clusterOpen = false;
            }
            else if (State == State.Terminated)
            {
                // Flush whatever sweep was mid-cluster when the chart/session ended — otherwise
                // the very last (often largest) print of the session never gets drawn.
                FinalizeCluster(true);
            }
        }

        // Bar-driven work is not needed — all detection happens in OnMarketData.
        protected override void OnBarUpdate() { }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (CurrentBar < 0 || State != State.Realtime)
                return;

            if (e.MarketDataType == MarketDataType.Bid)
            {
                _bid = e.Price;
                return;
            }
            if (e.MarketDataType == MarketDataType.Ask)
            {
                _ask = e.Price;
                return;
            }
            if (e.MarketDataType != MarketDataType.Last)
                return;

            if (_bid <= 0 || _ask <= 0)
                return; // inside market not established yet

            bool isBuy;
            if (e.Price >= _ask)
                isBuy = true;
            else if (e.Price <= _bid)
                isBuy = false;
            else
                return; // print landed strictly between bid/ask — no clear aggressor, skip

            if (_clusterOpen &&
                isBuy == _clusterIsBuy &&
                (e.Time - _clusterLastTime).TotalMilliseconds <= ClusterMilliseconds &&
                (e.Time - _clusterStartTime).TotalMilliseconds <= MaxClusterSpanMs)
            {
                // Same side, within the sweep window and the hard wall-clock cap — fold into the cluster.
                _clusterVolume  += e.Volume;
                _clusterLastTime = e.Time;
                if (e.Volume > _clusterMaxPrint)
                {
                    _clusterMaxPrint = e.Volume;
                    _clusterPrice    = e.Price;
                    _clusterMaxTime  = e.Time;
                }
                return;
            }

            // Opposite side, gap exceeded, or max span exceeded — finalize the open cluster, start a new one.
            FinalizeCluster(false);

            _clusterOpen      = true;
            _clusterIsBuy     = isBuy;
            _clusterVolume    = e.Volume;
            _clusterPrice     = e.Price;
            _clusterMaxPrint  = e.Volume;
            _clusterMaxTime   = e.Time;
            _clusterLastTime  = e.Time;
            _clusterStartTime = e.Time;
        }

        // bestEffort: true only when called from State.Terminated, where the chart/ChartControl
        // may already be torn down on that thread — draw failures there are swallowed, not fatal.
        private void FinalizeCluster(bool bestEffort)
        {
            if (!_clusterOpen)
                return;

            _clusterOpen = false;

            if (_clusterVolume < MinVolume)
                return;

            _tagCounter++;
            string dotTag  = "BigPrintDot"  + _tagCounter;
            string textTag = "BigPrintText" + _tagCounter;

            Brush dotBrush = _clusterIsBuy ? _buyBrush : _sellBrush;
            double offset  = 4 * TickSize;
            double textY   = _clusterIsBuy ? _clusterPrice + offset : _clusterPrice - offset;

            // Anchor at the largest print's own price AND time — not the cluster's last print time,
            // which can be a different (smaller) print later in the same sweep.
            if (bestEffort)
            {
                try
                {
                    Draw.Dot(this, dotTag, false, _clusterMaxTime, _clusterPrice, dotBrush);
                    Draw.Text(this, textTag, false, _clusterVolume.ToString(), _clusterMaxTime, textY, 0,
                        dotBrush, new SimpleFont("Arial", TextSize), TextAlignment.Center, null, null, 0);
                }
                catch (Exception) { /* teardown thread — chart may already be gone, nothing to do */ }
            }
            else
            {
                Draw.Dot(this, dotTag, false, _clusterMaxTime, _clusterPrice, dotBrush);
                Draw.Text(this, textTag, false, _clusterVolume.ToString(), _clusterMaxTime, textY, 0,
                    dotBrush, new SimpleFont("Arial", TextSize), TextAlignment.Center, null, null, 0);
            }

            _drawnClusterTags.Enqueue(_tagCounter);
            if (_drawnClusterTags.Count > MaxDrawObjects)
            {
                int oldest = _drawnClusterTags.Dequeue();
                RemoveDrawObject("BigPrintDot" + oldest);
                RemoveDrawObject("BigPrintText" + oldest);
            }
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Min Volume", Description = "Minimum total contracts in a cluster to draw it.", Order = 1, GroupName = "Parameters")]
        public int MinVolume { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Cluster Milliseconds", Description = "Max gap between same-side prints to still count as one sweep.", Order = 2, GroupName = "Parameters")]
        public int ClusterMilliseconds { get; set; }

        [NinjaScriptProperty]
        [Range(4, 72)]
        [Display(Name = "Text Size", Description = "Font size of the contract-count label.", Order = 3, GroupName = "Parameters")]
        public int TextSize { get; set; }

        [XmlIgnore]
        [Display(Name = "Buy Brush", Description = "Color for buy-aggressor clusters.", Order = 4, GroupName = "Parameters")]
        public Brush BuyBrush
        {
            get { return _buyBrush; }
            set { _buyBrush = value; }
        }

        [Browsable(false)]
        public string BuyBrushSerialize
        {
            get { return Serialize.BrushToString(BuyBrush); }
            set { BuyBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Sell Brush", Description = "Color for sell-aggressor clusters.", Order = 5, GroupName = "Parameters")]
        public Brush SellBrush
        {
            get { return _sellBrush; }
            set { _sellBrush = value; }
        }

        [Browsable(false)]
        public string SellBrushSerialize
        {
            get { return Serialize.BrushToString(SellBrush); }
            set { SellBrush = Serialize.StringToBrush(value); }
        }
        #endregion
    }
}
