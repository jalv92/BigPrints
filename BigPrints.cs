#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

        // ---- Event Recorder (specs 2026-07-30 + 2026-08-01 auto-triggers) -------------
        // Null unless EnableRecorder — every hook below is a no-op via `_recorder?.`.
        private BigPrintsRecorder _recorder;
        private int _clusterPrintCount;   // prints folded into the open cluster (recorder metadata)

        // Stop-run detector (auto mode only) — rolling window of aggressor Last prints
        // with monotonic max/min deques so a tape burst never scans the window per print.
        // All on the market-data Last path, same single-threaded assumption as the
        // cluster state above; time-based eviction keeps the deques honest.
        private struct SrPrint { public DateTime T; public double P; public long V; }
        private readonly Queue<SrPrint>      _srWindow = new Queue<SrPrint>();
        private readonly LinkedList<SrPrint> _srMax    = new LinkedList<SrPrint>(); // decreasing prices
        private readonly LinkedList<SrPrint> _srMin    = new LinkedList<SrPrint>(); // increasing prices
        private long _srVolSum;
        // Epoch guard: a live Playback slider rewind does NOT re-run DataLoaded, so
        // detector memory (_recentClusters, _sr*) would splice two tape epochs — stale
        // FUTURE-stamped entries pollute the windows (review finding). Cleared on any
        // backward time regression beyond the recorder's own skew tolerance.
        private DateTime _lastDetectorTime;

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
        // ponytail: count cap, not time cap — 300 covers any realistic AccumWindowSec
        // (900s at one qualifying cluster every 3s); a time-bounded structure only if a
        // real session ever evicts in-window clusters (was 50, review finding: silent
        // accum undercount on busy tape).
        private const int MaxClusterMemory = 300;

        // Session stats since chart load (approximation of session — honest label is
        // applied at serialize time). Written on the market-data thread only; reads
        // from the UI thread tolerate tearing (doubles/longs, advisory context only).
        private long   _cumDelta;

        // Rolling delta: per-minute buckets so the AI gets a recent-flow signal whose
        // anchor is NOT the arbitrary chart-load time (the session cum-delta's flaw:
        // the same tape gave -135 or +262 depending on when the chart was loaded).
        // Written on the market-data thread, read on the UI thread at Analyze time.
        private class DeltaBucket { public DateTime Minute; public long Delta; }
        private readonly object _deltaLock = new object();
        private readonly Queue<DeltaBucket> _deltaBuckets = new Queue<DeltaBucket>();
        private DeltaBucket _currentDeltaBucket;
        private DateTime _lastTapeTime;
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

        // ---- AI Advisor UX -----------------------------------------------------------
        private BigPrintsAiClient _aiClient;
        private CancellationTokenSource _aiCts;
        private bool _analysisRunning;
        private bool _lastCaptureNoScreenshot;
        private DateTime _lastCaptureAt;
        private double   _lastCaptureClose;

        // ---- AI Advisor signal-outcome tracker ----------------------------------------
        // Watches the active signal against live bars and logs the resolution to the
        // JSONL automatically, so audits no longer depend on later Analyze clicks
        // (audit #1 blind spot: signals after the session's last click were unscoreable).
        // Armed on the UI thread (OnAnalysisComplete), read on the market-data thread
        // (OnBarUpdate) — all mutations under _signalLock.
        private class SignalTracker
        {
            public string   SignalTs;
            public bool     IsBuy;
            public bool     FillOnHigh;   // entry above market at signal time -> fills on High touch
            public double   Entry, Stop, Target;
            public bool     Filled;
            public DateTime FilledAt;
            public double   PostFillHigh, PostFillLow;   // tick extremes AFTER the fill only
        }
        private readonly object _signalLock = new object();
        private SignalTracker _activeSignal;
        private DateTime _analysisStartedUtc;
        private Grid   _analyzeGrid;
        private Button _analyzeButton;
        private Button _recordButton;
        private DispatcherTimer _elapsedTimer;

        private const string DefaultBasePrompt =
@"Account: prop-firm evaluation; the evaluation FAILS if cumulative losses reach $2,000.
Instrument: NQ futures, 1 contract ($20 per point).
Risk per trade: up to $2,000 (100 NQ points) is the absolute hard ceiling available, but it is the entire evaluation - prefer the tightest structure-based stop that sits OUTSIDE bar noise (typically 15-50 points on the 1-minute chart) and only propose a wider stop when structure genuinely requires it. Flag setups needing more than 50 points of stop as elevated-risk in the rationale.
Trading style: intraday only, one position at a time, structure-based stops, no overnight positions.";

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

                EnableAiAdvisor      = true;
                ApiKeyFilePath       = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "claude_api_key.txt");
                ModelId              = "claude-sonnet-5";
                BasePromptFilePath   = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "bigprints_base_prompt.txt");
                ResponseLanguage     = "English";
                EnableScreenshot     = true;
                DomLevelsToSend      = 10;
                RecentClustersToSend = 20;
                BarsToSend           = 30;
                AnalysisSoundFile    = "";
                ShowFullAnalysis     = false;
                EnableRecorder       = false;
                RecorderAutoMode     = false;
                AutoTriggerSweeps    = true;
                AccumMinClusters     = 3;
                AccumWindowSec       = 180;
                StopRunTicks         = 40;
                StopRunWindowSec     = 10;
                AutoMaxFilesPerSession = 40;
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

                if (EnableAiAdvisor)
                {
                    _aiCts = new CancellationTokenSource();
                    string key = BigPrintsAiClient.LoadApiKey(ApiKeyFilePath);
                    if (key != null)
                        _aiClient = new BigPrintsAiClient(key, ModelId);
                    else
                        Print("BigPrints AI: API key file not found or empty at '" + ApiKeyFilePath + "' — Analyze disabled.");

                    // Seed the base-prompt file with the default template on first run so
                    // the trader has something concrete to edit (see LoadBasePrompt).
                    try
                    {
                        if (!System.IO.File.Exists(BasePromptFilePath))
                            System.IO.File.WriteAllText(BasePromptFilePath, DefaultBasePrompt);
                    }
                    catch (Exception ex) { Print("BigPrints AI: could not seed base prompt file — " + ex.Message); }
                }

                if (EnableRecorder)
                {
                    _recorder = new BigPrintsRecorder(System.IO.Path.Combine(
                        NinjaTrader.Core.Globals.UserDataDir, "BigPrintsAI", "recordings"));
                    _recorder.Configure(RecorderAutoMode,
                        !RecorderAutoMode || AutoTriggerSweeps, AutoMaxFilesPerSession);
                    _recorder.Log = msg => Print(msg);
                    _recorder.StateChanged = OnRecorderStateChanged;
                }

                // Detector state must not survive a Playback restart: stale clusters carry
                // FUTURE tape times after a rewind and would satisfy the accumulation
                // window instantly; the stop-run window would mix two tape epochs.
                lock (_clusterMemLock)
                    _recentClusters.Clear();
                _srWindow.Clear();
                _srMax.Clear();
                _srMin.Clear();
                _srVolSum = 0;
                _lastDetectorTime = default(DateTime);
            }
            else if (State == State.Historical)
            {
                if ((!EnableAiAdvisor && !EnableRecorder) || ChartControl == null)
                    return;

                ChartControl.Dispatcher.InvokeAsync(new Action(() =>
                {
                    // Duplicate guard — the lifecycle can re-enter Historical on the same instance.
                    if (_analyzeGrid != null && UserControlCollection.Contains(_analyzeGrid))
                        return;

                    _analyzeGrid = new Grid
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment   = VerticalAlignment.Bottom,
                        Margin              = new Thickness(0, 0, 12, 32),
                    };
                    var panel = new StackPanel { Orientation = Orientation.Horizontal };

                    if (EnableRecorder)
                    {
                        _recordButton = new Button
                        {
                            Content    = "Record",
                            Padding    = new Thickness(10, 4, 10, 4),
                            Margin     = new Thickness(0, 0, 8, 0),
                            Foreground = Brushes.White,
                            Background = Brushes.DarkSlateGray,
                            ToolTip    = "Manual: arm ~1 min BEFORE the expected big print - pre-context starts at this click; click again to cancel (armed) or finalize early (recording). Auto Mode: arms itself and captures sweep/accum/stoprun triggers - click only to finalize an active recording early.",
                        };
                        _recordButton.Click += OnRecordClick;
                        panel.Children.Add(_recordButton);
                    }

                    if (EnableAiAdvisor)
                    {
                        _analyzeButton = new Button
                        {
                            Content    = "Analyze",
                            Padding    = new Thickness(10, 4, 10, 4),
                            Foreground = Brushes.White,
                            Background = Brushes.DarkSlateGray, // predefined brush — thread-safe, no Freeze needed
                        };
                        _analyzeButton.Click += OnAnalyzeClick;
                        panel.Children.Add(_analyzeButton);

                        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                        _elapsedTimer.Tick += OnElapsedTick;
                    }

                    _analyzeGrid.Children.Add(panel);
                    UserControlCollection.Add(_analyzeGrid);
                }));
            }
            else if (State == State.Terminated)
            {
                // Flush any live signal so no outcome is silently lost at session end.
                lock (_signalLock)
                {
                    if (_activeSignal != null)
                    {
                        SignalTracker sig = _activeSignal;
                        BigPrintsAiClient.AppendOutcome(sig.SignalTs, sig.IsBuy ? "buy" : "sell",
                            sig.Entry, sig.Stop, sig.Target,
                            sig.Filled ? "open_session_end" : "no_fill_session_end",
                            sig.Filled ? (DateTime?)sig.FilledAt : null, DateTime.Now, null);
                        _activeSignal = null;
                    }
                }

                _aiCts?.Cancel();
                _aiCts?.Dispose();
                _aiCts = null;

                if (ChartControl != null)
                {
                    ChartControl.Dispatcher.InvokeAsync(new Action(() =>
                    {
                        _elapsedTimer?.Stop();
                        if (_analyzeButton != null)
                        {
                            _analyzeButton.Click -= OnAnalyzeClick;
                            _analyzeButton = null;
                        }
                        if (_recordButton != null)
                        {
                            _recordButton.Click -= OnRecordClick;
                            _recordButton = null;
                        }
                        if (_analyzeGrid != null)
                        {
                            UserControlCollection.Remove(_analyzeGrid);
                            _analyzeGrid = null;
                        }
                    }));
                }

                _recorder?.OnTerminated();

                // Flush whatever sweep was mid-cluster when the chart/session ended — otherwise
                // the very last (often largest) print of the session never gets drawn.
                FinalizeCluster(true);
            }
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (!EnableAiAdvisor && _recorder == null)
                return;

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

                // Recorder: sampled top-10 snapshot. Copy under the depth lock, at most
                // once per 250ms (WantsBookSnapshot pre-check avoids copying otherwise).
                if (_recorder != null && _recorder.WantsBookSnapshot(e.Time))
                {
                    int nb = Math.Min(10, _bidRows.Count), na = Math.Min(10, _askRows.Count);
                    var bids = new double[nb][];
                    var asks = new double[na][];
                    for (int i = 0; i < nb; i++) bids[i] = new double[] { _bidRows[i].Price, _bidRows[i].Volume };
                    for (int i = 0; i < na; i++) asks[i] = new double[] { _askRows[i].Price, _askRows[i].Volume };
                    _recorder.OnBookSnapshot(e.Time, bids, asks);
                }
            }
        }

        // Bar-driven work is not needed — all detection happens in OnMarketData.
        protected override void OnBarUpdate()
        {
            // Signal-outcome tracker. Cheap fast path: unsynchronized null read is fine
            // (reference reads are atomic; worst case is a one-tick delay after arming).
            if (_activeSignal == null || State != State.Realtime || CurrentBar < 0)
                return;

            lock (_signalLock)
            {
                SignalTracker sig = _activeSignal;
                if (sig == null)
                    return;

                // Tick-based tracking via Close[0] (latest price) ONLY. Bar extremes
                // (High[0]/Low[0]) include pre-arm/pre-fill price action and would
                // fabricate fills and outcomes (review finding — cf. audited signal #11,
                // where the bar's open-side high sat beyond the target before the fill).
                double px = Close[0];

                if (!sig.Filled)
                {
                    bool filled = sig.FillOnHigh ? px >= sig.Entry : px <= sig.Entry;
                    if (!filled)
                        return;
                    sig.Filled       = true;
                    sig.FilledAt     = Time[0];
                    sig.PostFillHigh = px;
                    sig.PostFillLow  = px;
                    // fall through: a gap-through tick can fill and resolve at once
                }
                else
                {
                    if (px > sig.PostFillHigh) sig.PostFillHigh = px;
                    if (px < sig.PostFillLow)  sig.PostFillLow  = px;
                }

                // Stop evaluated first: if a single tick jumps through both levels,
                // the conservative call is stop (matches the manual-audit convention).
                bool stopHit = sig.IsBuy ? sig.PostFillLow  <= sig.Stop   : sig.PostFillHigh >= sig.Stop;
                bool tgtHit  = sig.IsBuy ? sig.PostFillHigh >= sig.Target : sig.PostFillLow  <= sig.Target;
                if (!stopHit && !tgtHit)
                    return;

                string status = stopHit ? "stop" : "target";
                // ponytail: File.AppendAllText under _signalLock on the data thread — fires
                // once per resolution, not per tick; a disk hiccup briefly stalls tick
                // processing. Queue + background writer only if that ever measurably matters.
                BigPrintsAiClient.AppendOutcome(sig.SignalTs, sig.IsBuy ? "buy" : "sell",
                    sig.Entry, sig.Stop, sig.Target, status,
                    sig.FilledAt, Time[0], stopHit ? sig.Stop : sig.Target);
                _activeSignal = null;
                Print("BigPrints AI: signal outcome logged — " + status);
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (CurrentBar < 0 || State != State.Realtime)
                return;

            if (e.MarketDataType == MarketDataType.Bid)
            {
                _bid = e.Price;
                _recorder?.OnInside(e.Time, true, e.Price, e.Volume);
                return;
            }
            if (e.MarketDataType == MarketDataType.Ask)
            {
                _ask = e.Price;
                _recorder?.OnInside(e.Time, false, e.Price, e.Volume);
                return;
            }
            if (e.MarketDataType != MarketDataType.Last)
                return;

            // Detector epoch guard: on a live Playback rewind (slider back, no DataLoaded)
            // detector memory holds FUTURE-stamped entries from the abandoned tape segment —
            // they would pollute the accum window and break the stop-run deques' monotonic
            // assumption (review finding). Forward gaps need nothing: time eviction and the
            // accum cutoff already handle them.
            // The default(DateTime) check is load-bearing: MinValue.AddSeconds(-2) THROWS
            // (verified against the runtime) and this line runs before every other guard.
            if (_lastDetectorTime != default(DateTime) && e.Time < _lastDetectorTime.AddSeconds(-2))
            {
                lock (_clusterMemLock) _recentClusters.Clear();
                _srWindow.Clear(); _srMax.Clear(); _srMin.Clear(); _srVolSum = 0;
            }
            if (e.Time > _lastDetectorTime)
                _lastDetectorTime = e.Time;

            if (_bid <= 0 || _ask <= 0)
                return; // inside market not established yet

            bool isBuy;
            if (e.Price >= _ask)
                isBuy = true;
            else if (e.Price <= _bid)
                isBuy = false;
            else
            {
                // No clear aggressor for detection — but inside-spread prints are the
                // iceberg evidence the recorder exists for, so record before skipping.
                _recorder?.OnLast(e.Time, e.Price, e.Volume, 0, _bid, _ask);
                return;
            }
            _recorder?.OnLast(e.Time, e.Price, e.Volume, isBuy ? 1 : -1, _bid, _ask);

            // Stop-run detector (auto mode). Aggressor prints only: during a flush every
            // print is at bid/ask, so inside-spread prints never extend the range.
            if (_recorder != null && RecorderAutoMode && StopRunTicks > 0)
                CheckStopRun(e.Time, e.Price, e.Volume);

            // AI Advisor session stats — same thread as all other Last-print handling.
            _cumDelta += isBuy ? e.Volume : -e.Volume;
            lock (_deltaLock)
            {
                DateTime minute = new DateTime(e.Time.Year, e.Time.Month, e.Time.Day, e.Time.Hour, e.Time.Minute, 0);
                if (_currentDeltaBucket == null || _currentDeltaBucket.Minute != minute)
                {
                    _currentDeltaBucket = new DeltaBucket { Minute = minute };
                    _deltaBuckets.Enqueue(_currentDeltaBucket);
                    while (_deltaBuckets.Count > 15)   // keep ~15 min; 10 used at serialize time
                        _deltaBuckets.Dequeue();
                }
                _currentDeltaBucket.Delta += isBuy ? e.Volume : -e.Volume;
                _lastTapeTime = e.Time;
            }
            if (e.Price > _sessionHigh) _sessionHigh = e.Price;
            if (e.Price < _sessionLow)  _sessionLow  = e.Price;

            if (_clusterOpen &&
                isBuy == _clusterIsBuy &&
                (e.Time - _clusterLastTime).TotalMilliseconds <= ClusterMilliseconds &&
                (e.Time - _clusterStartTime).TotalMilliseconds <= MaxClusterSpanMs)
            {
                // Same side, within the sweep window and the hard wall-clock cap — fold into the cluster.
                _clusterVolume  += e.Volume;
                _clusterPrintCount++;
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
            _clusterPrintCount = 1;
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

            // Recorder trigger — live path only; the Terminated/bestEffort path is handled
            // by OnTerminated() (bar/session serializers are unsafe on the teardown thread).
            // Gated on recorder state (not just non-null) so the bar/session serializers below
            // aren't built and thrown away on every cluster while the recorder sits Off.
            if (!bestEffort && _recorder != null && _recorder.State != BigPrintsRecorder.RecorderState.Off)
            {
                _recorder.OnTrigger("sweep", _clusterMaxTime, _clusterIsBuy, _clusterPrice,
                    _clusterMaxPrint, _clusterVolume, _clusterPrintCount, 0,
                    _clusterStartTime, _clusterLastTime,
                    Instrument.FullName, SerializeRecentBars(10), SerializeSessionStats());

                // Accumulation detector (auto mode): N same-side clusters >= MinVolume
                // inside the rolling window -> "accum" trigger. The cluster just finalized
                // is already in _recentClusters. n_prints carries the CLUSTER count here;
                // max_print fields carry the largest cluster of the window.
                if (RecorderAutoMode && AccumMinClusters > 0)
                {
                    int n = 0; long sum = 0, maxV = 0; double maxP = _clusterPrice;
                    DateTime oldest = _clusterMaxTime;
                    lock (_clusterMemLock)
                    {
                        DateTime cutoff = _clusterLastTime.AddSeconds(-AccumWindowSec);
                        foreach (ClusterRecord c in _recentClusters)
                        {
                            if (c.IsBuy != _clusterIsBuy || c.Time < cutoff) continue;
                            n++; sum += c.Volume;
                            if (c.Time < oldest) oldest = c.Time;
                            if (c.Volume > maxV) { maxV = c.Volume; maxP = c.Price; }
                        }
                    }
                    if (n >= AccumMinClusters)
                        _recorder.OnTrigger("accum", _clusterMaxTime, _clusterIsBuy, maxP, maxV,
                            sum, n, 0, oldest, _clusterLastTime,
                            Instrument.FullName, SerializeRecentBars(10), SerializeSessionStats());
                }
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

        // ---- Stop-run detector (auto mode) --------------------------------------------
        // Rolling window over aggressor prints; fires when price has traveled
        // StopRunTicks within StopRunWindowSec AND the current print is the leading
        // edge of the move (window max above for a down run / min below for an up run).
        // Monotonic deques give O(1) amortized max/min; time-based eviction keeps them
        // consistent with the window queue. Cleared on fire so one flush = one trigger.
        private void CheckStopRun(DateTime t, double price, long size)
        {
            DateTime cutoff = t.AddSeconds(-StopRunWindowSec);
            while (_srWindow.Count > 0 && _srWindow.Peek().T < cutoff)
                _srVolSum -= _srWindow.Dequeue().V;
            while (_srMax.Count > 0 && _srMax.First.Value.T < cutoff) _srMax.RemoveFirst();
            while (_srMin.Count > 0 && _srMin.First.Value.T < cutoff) _srMin.RemoveFirst();

            while (_srMax.Count > 0 && _srMax.Last.Value.P <= price) _srMax.RemoveLast();
            _srMax.AddLast(new SrPrint { T = t, P = price });
            while (_srMin.Count > 0 && _srMin.Last.Value.P >= price) _srMin.RemoveLast();
            _srMin.AddLast(new SrPrint { T = t, P = price });
            _srWindow.Enqueue(new SrPrint { T = t, P = price, V = size });
            _srVolSum += size;

            double range = StopRunTicks * TickSize;
            double hi = _srMax.First.Value.P, lo = _srMin.First.Value.P;
            bool down = hi - price >= range;    // current print at the low edge of a flush
            bool up   = price - lo >= range;
            if (down && up) { if (hi - price >= price - lo) up = false; else down = false; }
            if (!down && !up)
                return;

            DateTime tStart = _srWindow.Peek().T;
            int  nPrints    = _srWindow.Count;
            long vol        = _srVolSum;
            int  rangeTicks = (int)Math.Round((down ? hi - price : price - lo) / TickSize);
            _srWindow.Clear(); _srMax.Clear(); _srMin.Clear(); _srVolSum = 0;

            _recorder.OnTrigger("stoprun", t, up, price, size, vol, nPrints, rangeTicks,
                tStart, t, Instrument.FullName, SerializeRecentBars(10), SerializeSessionStats());
        }

        // ---- Event Recorder UX --------------------------------------------------------

        private void OnRecordClick(object sender, RoutedEventArgs e)
        {
            if (_recorder == null || State == State.Terminated)
                return;
            DateTime tapeNow;
            lock (_deltaLock)
                tapeNow = _lastTapeTime;
            _recorder.Toggle(tapeNow);
        }

        // Called by the recorder under its own lock (from data/UI threads) — only queues
        // a dispatcher delegate, never blocks, never calls back into the recorder.
        private void OnRecorderStateChanged(BigPrintsRecorder.RecorderState state)
        {
            ChartControl?.Dispatcher.InvokeAsync(new Action(() =>
            {
                if (_recordButton == null)
                    return;
                switch (state)
                {
                    case BigPrintsRecorder.RecorderState.Armed:
                        _recordButton.Content = RecorderAutoMode ? "AUTO…" : "ARMED…";
                        _recordButton.Background = Brushes.DarkGoldenrod; break;
                    case BigPrintsRecorder.RecorderState.Recording:
                        _recordButton.Content = "REC…";   _recordButton.Background = Brushes.DarkRed;      break;
                    default:
                        _recordButton.Content = "Record";      _recordButton.Background = Brushes.DarkSlateGray; break;
                }
            }));
        }

        // ---- AI Advisor: click → capture → pipeline → render -------------------------
        // Click fires on the ChartControl UI thread (WPF routed event) — capture and
        // screenshot run here directly; only the HTTP pipeline goes to Task.Run.

        private void OnAnalyzeClick(object sender, RoutedEventArgs e)
        {
            if (_analysisRunning)
                return;

            CancellationTokenSource cts = _aiCts;
            if (cts == null || State == State.Terminated)
                return;

            if (_aiClient == null)
            {
                DrawAiPanel("AI: API key not loaded\ncheck 'API Key File Path' parameter", Brushes.Orange);
                return;
            }

            _analysisRunning    = true;
            _analysisStartedUtc = DateTime.UtcNow;
            _analyzeButton.IsEnabled = false;
            _elapsedTimer.Start();
            DrawAiPanel("Analyzing... 0s", Brushes.Gainsboro);

            ContextSnapshot ctx = CaptureContext();
            CancellationToken ct = cts.Token;

            Task.Run(async () =>
            {
                AiVerdict verdict;
                try
                {
                    verdict = await _aiClient.AnalyzeAsync(ctx, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    verdict = new AiVerdict { Decision = "error", Error = ex.Message };
                }
                ChartControl?.Dispatcher.InvokeAsync(new Action(() => OnAnalysisComplete(verdict)));
            });
        }

        private void OnElapsedTick(object sender, EventArgs e)
        {
            if (State == State.Terminated)
                return;
            if (!_analysisRunning)
                return;

            int secs = (int)(DateTime.UtcNow - _analysisStartedUtc).TotalSeconds;
            DrawAiPanel("Analyzing... " + secs + "s", Brushes.Gainsboro);
        }

        // Read fresh on every Analyze click so the trader can edit the file mid-session.
        // Lives in a file because the NT8 property grid is single-line and rejects
        // multiline pastes (same pattern as the API key file).
        private string LoadBasePrompt()
        {
            try
            {
                string text = System.IO.File.ReadAllText(BasePromptFilePath).Trim();
                if (text.Length > 0)
                    return text;
            }
            catch (Exception) { /* fall through to the built-in default */ }
            return DefaultBasePrompt;
        }

        private ContextSnapshot CaptureContext()
        {
            string screenshotB64 = null;
            if (EnableScreenshot)
            {
                try
                {
                    // NT8-internal API (same mechanism as the Share feature). Works only
                    // when this chart tab is active — always true on a manual click.
                    // GetScreenshot(NinjaTrader.NinjaScript.ShareScreenshotType, FrameworkElement) — the
                    // enum lives in NinjaTrader.Core under NinjaTrader.NinjaScript (already `using`d
                    // above), not NinjaTrader.Gui.Chart; the second argument is the element to capture.
                    var chartWindow = System.Windows.Window.GetWindow(ChartControl) as NinjaTrader.Gui.Chart.Chart;
                    var bmp = chartWindow == null ? null
                        : chartWindow.GetScreenshot(ShareScreenshotType.Chart, ChartControl);
                    if (bmp != null)
                    {
                        bmp.Freeze();
                        using (var ms = new System.IO.MemoryStream())
                        {
                            var enc = new PngBitmapEncoder();
                            enc.Frames.Add(BitmapFrame.Create(bmp));
                            enc.Save(ms);
                            screenshotB64 = Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }
                catch (Exception ex)
                {
                    Print("BigPrints AI: screenshot failed, sending without image — " + ex.Message);
                }
            }
            _lastCaptureNoScreenshot = EnableScreenshot && screenshotB64 == null;
            _lastCaptureAt    = DateTime.Now;
            _lastCaptureClose = (_bid > 0 && _ask > 0) ? (_bid + _ask) / 2.0 : 0;

            return new ContextSnapshot
            {
                Instrument       = Instrument.FullName,
                ChartTimeframe   = BarsPeriod.ToString(),
                LadderText       = SerializeLadder(DomLevelsToSend),
                ClustersText     = SerializeRecentClusters(RecentClustersToSend),
                BarsText         = SerializeRecentBars(BarsToSend),
                SessionText      = SerializeSessionStats(),
                BasePrompt       = LoadBasePrompt(),
                ResponseLanguage = ResponseLanguage,
                ScreenshotBase64 = screenshotB64,
                CapturedAt       = _lastCaptureAt,
            };
        }

        private void OnAnalysisComplete(AiVerdict verdict)
        {
            if (State == State.Terminated)
                return;

            _elapsedTimer?.Stop();
            _analysisRunning = false;
            if (_analyzeButton != null)
                _analyzeButton.IsEnabled = true;

            // bestEffort try/catch mirroring FinalizeCluster: this runs off a queued dispatcher
            // delegate, which can still fire after Terminated tears the chart down (NT8 does not
            // wrap exceptions in user-posted dispatcher delegates) — swallow, nothing to do.
            try
            {
                RemoveDrawObject("BigPrintsAiEntry");
                RemoveDrawObject("BigPrintsAiStop");
                RemoveDrawObject("BigPrintsAiTarget");

                if (verdict.Error != null)
                {
                    DrawAiPanel("AI ERROR\n" + WrapText(verdict.Error, 60), Brushes.Orange);
                    return;
                }

                Brush decisionBrush =
                    verdict.Decision == "buy"  ? Brushes.Lime :
                    verdict.Decision == "sell" ? Brushes.Red  : Brushes.Gray;
                string decisionWord = verdict.Decision == "sell" ? "SHORT" : verdict.Decision.ToUpper();

                if (ShowFullAnalysis)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("BIG PRINTS AI  " + DateTime.Now.ToString("HH:mm:ss"));
                    sb.AppendLine(decisionWord + "  (confidence " + verdict.Confidence + ")");
                    if (verdict.Entry.HasValue)
                        sb.AppendLine("Entry " + verdict.Entry.Value.ToString("F2")
                            + " | Stop " + (verdict.Stop.HasValue ? verdict.Stop.Value.ToString("F2") : "-")
                            + " | Target " + (verdict.Target.HasValue ? verdict.Target.Value.ToString("F2") : "-"));
                    sb.AppendLine(WrapText(verdict.Rationale ?? "", 60));
                    if (_lastCaptureNoScreenshot)
                        sb.AppendLine("(no screenshot — analysis ran without chart image)");
                    sb.Append("tokens " + verdict.InputTokens + " in / " + verdict.OutputTokens + " out");
                    DrawAiPanel(sb.ToString(), decisionBrush);
                }
                else
                {
                    // Minimal display by request: just the action. Full reasoning stays
                    // in the JSONL log for auditing; levels are drawn as lines below.
                    DrawAiPanel(DateTime.Now.ToString("HH:mm:ss") + "  " + decisionWord
                        + "  (" + verdict.Confidence + ")", decisionBrush, 22);
                }

                if (verdict.Decision == "buy" || verdict.Decision == "sell")
                {
                    if (verdict.Entry.HasValue)
                        Draw.HorizontalLine(this, "BigPrintsAiEntry", false, verdict.Entry.Value, decisionBrush, DashStyleHelper.Solid, 2);
                    if (verdict.Stop.HasValue)
                        Draw.HorizontalLine(this, "BigPrintsAiStop", false, verdict.Stop.Value, Brushes.OrangeRed, DashStyleHelper.Dash, 2);
                    if (verdict.Target.HasValue)
                        Draw.HorizontalLine(this, "BigPrintsAiTarget", false, verdict.Target.Value, Brushes.DeepSkyBlue, DashStyleHelper.Dash, 2);

                    // Arm the outcome tracker; a new signal supersedes (and logs) the old one.
                    // No inside market at capture -> cannot classify the entry side -> do not
                    // arm (a mislabeled fill test would fabricate outcomes; review finding #3).
                    if (verdict.Entry.HasValue && verdict.Stop.HasValue && verdict.Target.HasValue
                        && _lastCaptureClose > 0)
                    {
                        lock (_signalLock)
                        {
                            SignalTracker old = _activeSignal;
                            if (old != null)
                                BigPrintsAiClient.AppendOutcome(old.SignalTs, old.IsBuy ? "buy" : "sell",
                                    old.Entry, old.Stop, old.Target,
                                    old.Filled ? "open_superseded" : "no_fill_superseded",
                                    old.Filled ? (DateTime?)old.FilledAt : null, _lastCaptureAt, null);

                            bool isBuy = verdict.Decision == "buy";
                            _activeSignal = new SignalTracker
                            {
                                SignalTs   = _lastCaptureAt.ToString("o"),
                                IsBuy      = isBuy,
                                Entry      = verdict.Entry.Value,
                                Stop       = verdict.Stop.Value,
                                Target     = verdict.Target.Value,
                                FillOnHigh = _lastCaptureClose > 0 && verdict.Entry.Value > _lastCaptureClose,
                            };
                        }
                    }
                }
            }
            catch (Exception) { /* teardown thread — chart may already be gone, nothing to do */ }

            if (!string.IsNullOrEmpty(AnalysisSoundFile))
            {
                string fullPath = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "sounds", AnalysisSoundFile);
                if (System.IO.File.Exists(fullPath))
                    WinmmPlaySound(fullPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
            }
        }

        private void DrawAiPanel(string text, Brush brush, int fontSize = 12)
        {
            Draw.TextFixed(this, "BigPrintsAiPanel", text, TextPosition.TopRight,
                brush, new SimpleFont("Consolas", fontSize), Brushes.Transparent, Brushes.Black, 60);
        }

        // TextFixed does not word-wrap — insert newlines at word boundaries.
        private static string WrapText(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new System.Text.StringBuilder();
            int lineLen = 0;
            foreach (string word in text.Split(' '))
            {
                if (lineLen + word.Length + 1 > width) { sb.Append('\n'); lineLen = 0; }
                else if (lineLen > 0)                  { sb.Append(' ');  lineLen++; }
                sb.Append(word);
                lineLen += word.Length;
            }
            return sb.ToString();
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
            long rolling10 = 0;
            bool haveTape = false;
            lock (_deltaLock)
            {
                if (_lastTapeTime != default(DateTime))
                {
                    haveTape = true;
                    DateTime cutoff = _lastTapeTime.AddMinutes(-10);
                    foreach (DeltaBucket b in _deltaBuckets)
                        if (b.Minute >= cutoff)
                            rolling10 += b.Delta;
                }
            }
            sb.AppendLine(haveTape
                ? "rolling 10-minute delta: " + rolling10 + " contracts (recent aggressor flow - primary flow signal)"
                : "rolling 10-minute delta: unavailable");
            sb.AppendLine("cumulative delta since chart load: " + _cumDelta + " contracts (background context only - its anchor is the arbitrary chart-load time)");
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

        [NinjaScriptProperty]
        [Display(Name = "Enable AI Advisor", Description = "Master switch for the Analyze button and AI analysis.", Order = 20, GroupName = "AI Advisor")]
        public bool EnableAiAdvisor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "API Key File Path", Description = "Text file containing ONLY the Anthropic API key. The key itself is never stored in the indicator.", Order = 21, GroupName = "AI Advisor")]
        public string ApiKeyFilePath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Model Id", Description = "Anthropic model id used for all calls.", Order = 22, GroupName = "AI Advisor")]
        public string ModelId { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Base Prompt File Path", Description = "Text file with the account context sent to the AI (account, instrument, max risk per trade). Edit it in any text editor - it is re-read on every Analyze click. Created with a default template on first run. The NT8 property grid cannot hold multiline text, which is why this lives in a file.", Order = 23, GroupName = "AI Advisor")]
        public string BasePromptFilePath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Response Language", Description = "Language of the rationale shown on the chart.", Order = 24, GroupName = "AI Advisor")]
        public string ResponseLanguage { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Send Chart Screenshot", Description = "Attach a screenshot of this chart to the analysis.", Order = 25, GroupName = "AI Advisor")]
        public bool EnableScreenshot { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "DOM Levels To Send", Description = "L2 ladder depth per side.", Order = 26, GroupName = "AI Advisor")]
        public int DomLevelsToSend { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Recent Clusters To Send", Description = "How many recent big-print clusters to include.", Order = 27, GroupName = "AI Advisor")]
        public int RecentClustersToSend { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Bars To Send", Description = "How many recent bars (OHLCV) to include.", Order = 28, GroupName = "AI Advisor")]
        public int BarsToSend { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Analysis Sound File", Description = "WAV in the NT8 sounds folder played when an analysis completes. Empty = silent.", Order = 29, GroupName = "AI Advisor")]
        public string AnalysisSoundFile { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Full Analysis", Description = "Off (default): the panel shows only BUY/SHORT/HOLD with confidence. On: full rationale, levels and token usage on the panel. The JSONL log always keeps the full analysis either way.", Order = 30, GroupName = "AI Advisor")]
        public bool ShowFullAnalysis { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Recorder", Description = "Adds a Record button: captures tape + inside sizes + L2 snapshots around big-print events (180s post, extended while new triggers land, 10 min cap) to JSON under BigPrintsAI/recordings. Playback tool.", Order = 40, GroupName = "Recorder")]
        public bool EnableRecorder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Auto Mode", Description = "The recorder arms itself and captures every trigger without clicking Record: sweep clusters (toggle below), same-side accumulations and stop-run flushes. Off = manual v1 behavior (arm by click, one file per click).", Order = 41, GroupName = "Recorder")]
        public bool RecorderAutoMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Auto: Capture Sweeps", Description = "Auto mode: every cluster >= Min Volume starts a capture (the denominator the pair audit asked for). Off = only accumulation / stop-run triggers start files; sweeps still get logged inside open recordings.", Order = 42, GroupName = "Recorder")]
        public bool AutoTriggerSweeps { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Auto: Accumulation Clusters", Description = "Auto mode: this many same-side clusters >= Min Volume within the accumulation window fire an 'accum' trigger (0 = off). Targets the observed pattern: repeated big sells, then the drop.", Order = 43, GroupName = "Recorder")]
        public int AccumMinClusters { get; set; }

        [NinjaScriptProperty]
        [Range(10, 900)]
        [Display(Name = "Auto: Accumulation Window (s)", Description = "Rolling window for the accumulation trigger.", Order = 44, GroupName = "Recorder")]
        public int AccumWindowSec { get; set; }

        [NinjaScriptProperty]
        [Range(0, 400)]
        [Display(Name = "Auto: Stop-Run Ticks", Description = "Auto mode: price traveling this many ticks within the stop-run window, with the current print at the leading edge, fires a 'stoprun' trigger (0 = off).", Order = 45, GroupName = "Recorder")]
        public int StopRunTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 120)]
        [Display(Name = "Auto: Stop-Run Window (s)", Description = "Rolling window for the stop-run trigger.", Order = 46, GroupName = "Recorder")]
        public int StopRunWindowSec { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Auto: Max Files Per Session", Description = "Disk guard: auto mode stops capturing after this many files (one log line in the output window).", Order = 47, GroupName = "Recorder")]
        public int AutoMaxFilesPerSession { get; set; }
        #endregion
    }
}
