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
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// BigPrints v1.0.0
// Flags large aggressive market orders (buy/sell sweeps) on the Level-I tape and marks
// them on the chart at the price they printed, with the swept contract count.
//
// REAL-TIME / MARKET REPLAY ONLY. OnMarketData does not fire on historical data (NT8
// default), so this indicator shows nothing on a historical chart load — only prints
// that occur while the chart is live or replaying. No historical reconstruction is
// attempted; that is out of scope by design (see nt8-indicator: onmarketdata caveats).
//
// SOUND CRASH ROOT CAUSE — VERIFIED 2026-07-22 (WinDbg on 5 of the 8 crash dumps, 2026-07-21
// 22:57 through 2026-07-22 20:01): every NT8 Playback crash died on the SAME native stack —
// wdmaud!CWaveOutHandle::_ProcessData -> ucrtbase!memcpy access violation on the Windows audio
// worker thread. That is a use-after-free of a wave buffer owned by NT8's PlaySound()
// implementation (NAudio AudioFileReader + WaveOutEvent, fire-and-forget): under Playback load
// the managed side frees/finalizes the buffer while wdmaud is still streaming it. A cooldown
// cannot fix another component's use-after-free — the 750ms throttle was deployed and the
// crashes continued (2026-07-22 19:51 and 20:00). Fix: this file no longer calls NinjaScript's
// PlaySound() at all; it P/Invokes winmm PlaySound (SND_FILENAME|SND_ASYNC), where Windows owns
// the audio buffer and a new call safely cancels the prior sound. NOTE FOR PLAYBACK USERS: NT8's
// OWN event sounds (order filled, alerts...) go through the same crashy NAudio path — keep
// Tools > Options > Sounds event sounds off while running accelerated Playback.
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

        // Per-side label stacking: how many clusters already labeled THIS bar on this side, so a
        // second/third same-bar same-side cluster stacks further away instead of overlapping.
        private int _buyStackBar   = -1;
        private int _buyStackCount;
        private int _sellStackBar  = -1;
        private int _sellStackCount;

        // ponytail: hard cap on live draw objects — a slow steady same-side drip (prints every
        // ~150ms for minutes) would otherwise merge into one giant blob AND draw objects are
        // never removed, so chart memory grows without bound over a session. 400 clusters is
        // several hours of ES 150+ contract sweeps at normal cadence; raise if that's not enough.
        private const int MaxClusterSpanMs = 1500;
        private const int MaxDrawObjects   = 400;
        private readonly Queue<int> _drawnClusterTags = new Queue<int>();

        private Brush _buyBrush  = Brushes.Lime;
        private Brush _sellBrush = Brushes.Red;

        // Sound throttle (see SOUND CRASH ROOT CAUSE above). Shared across buy AND sell — a
        // buy chirp still playing when a sell fires is still two overlapping native audio
        // instances, so one counter gates both sides, not per-side counters.
        //
        // CLOCK NUANCE: this deliberately uses REAL wall-clock time (Environment.TickCount),
        // not tape time (e.Time) like the rest of this file. Audio overlap is a physical
        // real-time phenomenon — in accelerated Playback, tape time runs faster than real time,
        // so a tape-time cooldown would still let two PlaySound calls land within the same real
        // audio-device window. This is the one legitimate exception to "tape time only" here.
        // int, not long: NT8 targets .NET Framework 4.8, which has no TickCount64. Plain int
        // TickCount wraps every ~24.9 days; `unchecked` subtraction below is wrap-safe as long
        // as the gap between two reads is under that span — always true for a sub-10s cooldown.
        private int _lastSoundTick;

        // ---- AI Advisor data-capture layer -------------------------------------------
        // L2 book maintained from OnMarketDepth, per NinjaTrader's own SampleLevel2Book:
        // lists indexed by e.Position (NOT price-keyed — a Remove at position 0 shifts
        // every lower level up, and NT sends the matching Update/Remove sequence).
        // All mutations AND reads lock on Instrument.SyncMarketDepth — the platform's
        // own sanctioned lock object for depth state.
        private class LadderRow
        {
            public double Price;
            public long   Volume;
        }
        private readonly List<LadderRow> _askRows = new List<LadderRow>(10);
        private readonly List<LadderRow> _bidRows = new List<LadderRow>(10);

        // Recent finalized big-print clusters (the ones that were drawn), bounded.
        // Appended on the market-data thread, read on the UI thread at Analyze time.
        private class ClusterRecord
        {
            public DateTime Time;
            public bool     IsBuy;
            public double   Price;
            public long     Volume;
        }
        private readonly object _clusterMemLock = new object();
        private readonly Queue<ClusterRecord> _recentClusters = new Queue<ClusterRecord>();
        private const int MaxClusterMemory = 50;

        // Session stats since chart load (approximation of session — honest label is
        // applied at serialize time). Written on the market-data thread only; reads
        // from the UI thread tolerate tearing (doubles/longs, advisory context only).
        private long   _cumDelta;
        private double _sessionHigh = double.MinValue;
        private double _sessionLow  = double.MaxValue;

        // Direct winmm import — deliberately NOT NinjaScript's PlaySound() helper (see SOUND
        // CRASH ROOT CAUSE in the header: NT8's helper is the verified process-killer in
        // Playback). winmm loads/owns the buffer itself (SND_FILENAME) on its own thread
        // (SND_ASYNC), and a new call cancels the previous sound inside winmm with proper
        // locking — the managed heap never backs the audio stream, so there is nothing the
        // GC/finalizer can free out from under wdmaud. SND_NODEFAULT = a missing file plays
        // silence, not the Windows error ding. Fully qualified attribute (no extra usings).
        [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "PlaySoundW",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool WinmmPlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        private const uint SND_ASYNC     = 0x0001;
        private const uint SND_NODEFAULT = 0x0002;
        private const uint SND_FILENAME  = 0x00020000;

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
                TextOffsetTicks     = 10;
                EnableSound         = true;
                BuySoundFile        = "BigPrintBuy.wav";
                SellSoundFile       = "BigPrintSell.wav";
                SoundCooldownMs     = 750; // > the ~340ms WAV length, guarantees no overlap
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

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            lock (e.Instrument.SyncMarketDepth)
            {
                List<LadderRow> rows = e.MarketDataType == MarketDataType.Ask ? _askRows : _bidRows;

                if (e.Operation == Operation.Add ||
                    (e.Operation == Operation.Update && (rows.Count == 0 || rows.Count <= e.Position)))
                {
                    var row = new LadderRow { Price = e.Price, Volume = e.Volume };
                    if (rows.Count <= e.Position) rows.Add(row);
                    else                          rows.Insert(e.Position, row);
                }
                else if (e.Operation == Operation.Remove && rows.Count > e.Position)
                {
                    rows.RemoveAt(e.Position);
                }
                else if (e.Operation == Operation.Update)
                {
                    rows[e.Position].Price  = e.Price;
                    rows[e.Position].Volume = e.Volume;
                }
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

            // AI Advisor session stats — same thread as all other Last-print handling.
            _cumDelta += isBuy ? e.Volume : -e.Volume;
            if (e.Price > _sessionHigh) _sessionHigh = e.Price;
            if (e.Price < _sessionLow)  _sessionLow  = e.Price;

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

            // AI Advisor cluster memory — record every drawn cluster, bounded.
            lock (_clusterMemLock)
            {
                _recentClusters.Enqueue(new ClusterRecord
                {
                    Time   = _clusterMaxTime,
                    IsBuy  = _clusterIsBuy,
                    Price  = _clusterPrice,
                    Volume = _clusterVolume,
                });
                if (_recentClusters.Count > MaxClusterMemory)
                    _recentClusters.Dequeue();
            }

            _tagCounter++;
            string dotTag  = "BigPrintDot"  + _tagCounter;
            string textTag = "BigPrintText" + _tagCounter;

            Brush dotBrush = _clusterIsBuy ? _buyBrush : _sellBrush;

            // Dot stays glued to the exact print: (_clusterMaxTime, _clusterPrice) — the precise
            // signal, never moved. Text is pushed clear of price action (arrow-indicator style,
            // below the low for buys / above the high for sells) so it never overlaps a candle;
            // color alone ties the label back to its dot (no connector line — not worth it).
            if (bestEffort)
            {
                try
                {
                    // TickSize dereferences the instrument — inside the guard: it can throw on
                    // the Terminated teardown thread just like the bar series below.
                    double tickOffset = TextOffsetTicks * TickSize;
                    double textY;
                    try
                    {
                        textY = _clusterIsBuy ? Low[0] - tickOffset : High[0] + tickOffset;
                    }
                    catch (Exception)
                    {
                        // Bar series can be invalid on the Terminated teardown thread — fall back
                        // to an offset from the print price itself.
                        textY = _clusterIsBuy ? _clusterPrice - tickOffset : _clusterPrice + tickOffset;
                    }

                    Draw.Dot(this, dotTag, false, _clusterMaxTime, _clusterPrice, dotBrush);
                    Draw.Text(this, textTag, false, _clusterVolume.ToString(), _clusterMaxTime, textY, 0,
                        dotBrush, new SimpleFont("Arial", TextSize), TextAlignment.Center, dotBrush, Brushes.Black, 70);
                }
                catch (Exception) { /* teardown thread — chart may already be gone, nothing to do */ }
            }
            else
            {
                // CurrentBar >= 0 is already guaranteed by the OnMarketData guard that led here.
                double tickOffset = TextOffsetTicks * TickSize;
                double textY = _clusterIsBuy ? Low[0] - tickOffset : High[0] + tickOffset;

                // Stack same-bar same-side labels apart in screen pixels (scale-independent).
                // Chart pixel Y grows downward, so a positive yPixelOffset pushes further DOWN
                // (away from the low, correct for buys) and a negative one pushes further UP
                // (away from the high, correct for sells). First cluster on a bar = index 0 = no
                // offset = closest to the candle; each later same-bar same-side cluster stacks
                // one step further out, so reading order (near-to-far) is arrival order.
                int stackIndex;
                if (_clusterIsBuy)
                {
                    stackIndex = (CurrentBar == _buyStackBar) ? ++_buyStackCount : (_buyStackCount = 0);
                    _buyStackBar = CurrentBar;
                }
                else
                {
                    stackIndex = (CurrentBar == _sellStackBar) ? ++_sellStackCount : (_sellStackCount = 0);
                    _sellStackBar = CurrentBar;
                }
                int pixelOffset = _clusterIsBuy
                    ? stackIndex * (TextSize + 8)
                    : -stackIndex * (TextSize + 8);

                Draw.Dot(this, dotTag, false, _clusterMaxTime, _clusterPrice, dotBrush);
                Draw.Text(this, textTag, false, _clusterVolume.ToString(), _clusterMaxTime, textY, pixelOffset,
                    dotBrush, new SimpleFont("Arial", TextSize), TextAlignment.Center, dotBrush, Brushes.Black, 70);

                // Sound only on the live path — no audio alert firing during Terminated teardown.
                // Throttled by real wall-clock time (see _lastSoundTick field comment) — winmm
                // cancels the previous sound on each new call, so without the cooldown a fast
                // Playback tape would cut every chirp off mid-play; a skip here is silent (no
                // Print): the dot/label above already drew regardless.
                if (EnableSound)
                {
                    int nowTick = Environment.TickCount;
                    if (unchecked(nowTick - _lastSoundTick) >= SoundCooldownMs)
                    {
                        string soundFile = _clusterIsBuy ? BuySoundFile : SellSoundFile;
                        // Fully qualified (not a `using System.IO;`) — NinjaTrader.Gui brings its own
                        // Path type into scope, and a bare "Path" here would collide with it.
                        string fullPath = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "sounds", soundFile);
                        if (System.IO.File.Exists(fullPath))
                        {
                            WinmmPlaySound(fullPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                            _lastSoundTick = nowTick;
                        }
                    }
                }
            }

            _drawnClusterTags.Enqueue(_tagCounter);
            if (_drawnClusterTags.Count > MaxDrawObjects)
            {
                int oldest = _drawnClusterTags.Dequeue();
                RemoveDrawObject("BigPrintDot" + oldest);
                RemoveDrawObject("BigPrintText" + oldest);
            }
        }

        // ---- AI Advisor serializers (called on the UI thread at Analyze time) --------

        internal string SerializeLadder(int maxLevels)
        {
            var sb = new System.Text.StringBuilder();
            lock (Instrument.SyncMarketDepth)
            {
                for (int i = Math.Min(maxLevels, _askRows.Count) - 1; i >= 0; i--)
                    sb.AppendLine("ASK " + _askRows[i].Price.ToString("F2") + " x " + _askRows[i].Volume);
                for (int i = 0; i < Math.Min(maxLevels, _bidRows.Count); i++)
                    sb.AppendLine("BID " + _bidRows[i].Price.ToString("F2") + " x " + _bidRows[i].Volume);
            }
            return sb.ToString().TrimEnd();
        }

        internal string SerializeRecentClusters(int max)
        {
            var sb = new System.Text.StringBuilder();
            lock (_clusterMemLock)
            {
                int skip = Math.Max(0, _recentClusters.Count - max);
                int i = 0;
                foreach (var c in _recentClusters)
                {
                    if (i++ < skip) continue;
                    sb.AppendLine(c.Time.ToString("HH:mm:ss") + " " + (c.IsBuy ? "BUY " : "SELL")
                        + " " + c.Volume + " contracts @ " + c.Price.ToString("F2"));
                }
            }
            return sb.ToString().TrimEnd();
        }

        internal string SerializeRecentBars(int count)
        {
            // UI-thread caller: absolute accessors only (barsAgo indexer is unsafe here).
            var sb = new System.Text.StringBuilder();
            try
            {
                if (CurrentBar < 0)
                    return "bar data unavailable";
                int first = Math.Max(0, CurrentBar - count + 1);
                for (int idx = first; idx <= CurrentBar; idx++)
                {
                    sb.AppendLine(Bars.GetTime(idx).ToString("HH:mm")
                        + " O:" + Bars.GetOpen(idx).ToString("F2")
                        + " H:" + Bars.GetHigh(idx).ToString("F2")
                        + " L:" + Bars.GetLow(idx).ToString("F2")
                        + " C:" + Bars.GetClose(idx).ToString("F2")
                        + " V:" + Bars.GetVolume(idx));
                }
            }
            catch (Exception) { return "bar data unavailable"; }
            return sb.ToString().TrimEnd();
        }

        internal string SerializeSessionStats()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("cumulative delta since chart load: " + _cumDelta + " contracts (buy-aggressor minus sell-aggressor)");
            if (_sessionHigh > double.MinValue)
                sb.AppendLine("high/low since chart load: " + _sessionHigh.ToString("F2") + " / " + _sessionLow.ToString("F2"));
            sb.AppendLine("current inside market: bid " + _bid.ToString("F2") + " / ask " + _ask.ToString("F2"));
            return sb.ToString().TrimEnd();
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

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Text Offset Ticks", Description = "Ticks between the current bar's high/low and the contract-count label.", Order = 4, GroupName = "Parameters")]
        public int TextOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Sound", Description = "Play a sound alert every time a cluster is drawn.", Order = 5, GroupName = "Parameters")]
        public bool EnableSound { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Buy Sound File", Description = "WAV file name (in the NinjaTrader 8 sounds folder) played on a buy cluster.", Order = 6, GroupName = "Parameters")]
        public string BuySoundFile { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sell Sound File", Description = "WAV file name (in the NinjaTrader 8 sounds folder) played on a sell cluster.", Order = 7, GroupName = "Parameters")]
        public string SellSoundFile { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "Sound Cooldown (ms)", Description = "Minimum real-time between alert sounds. Each new sound cancels the previous one (winmm), so this mainly keeps a fast tape from cutting every chirp off mid-play. 750ms > the 340ms WAV length lets each chirp finish.", Order = 8, GroupName = "Parameters")]
        public int SoundCooldownMs { get; set; }

        [XmlIgnore]
        [Display(Name = "Buy Brush", Description = "Color for buy-aggressor clusters.", Order = 9, GroupName = "Parameters")]
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
        [Display(Name = "Sell Brush", Description = "Color for sell-aggressor clusters.", Order = 10, GroupName = "Parameters")]
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
