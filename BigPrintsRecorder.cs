#region Using declarations
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
#endregion

// BigPrintsRecorder v2.0 — capture of raw microstructure (tape prints including
// inside-spread ones, best bid/ask size updates, sampled L2 snapshots) around
// big-print events, for offline diagnosis of why price reacted.
// Specs: workspace docs/superpowers/specs/2026-07-30-bigprints-event-recorder-design.md
//        + 2026-08-01-bigprints-recorder-auto-triggers-design.md
//
// Modes:
//   Manual (v1): Off -> (Record click) Armed [buffers fill, nothing written]
//                -> (first sweep cluster >= MinVolume) Recording -> one JSON -> Off.
//                Click while Armed cancels; click while Recording finalizes early.
//   Auto: arms itself on the first market event, re-arms after every file and after
//         a playback jump/rewind discard; the indicator fires typed triggers
//         (sweep / accum / stoprun) with no click. A file cap stops disk runaway.
//
// Recording window: first trigger t_end + PostEventSeconds; every further trigger
// inside the window EXTENDS the deadline to its own t_end + PostEventSeconds,
// hard-capped at MaxRecordingSecs after the first trigger (meta.capped = true).
// While Recording the book cadence tightens to BookSnapshotRecMs, and any tape
// print making a new post-trigger extreme toward the trigger side requests an
// immediate snapshot — the sweep itself is not blind (pair audit §4.6).
//
// Threading: public methods are called from the market-data thread, the market-depth
// thread (under the indicator's SyncMarketDepth lock) and the UI thread (button).
// All state lives behind _lock. Callbacks (Log, StateChanged) ARE invoked under _lock —
// deliberate: both wired handlers (Print, Dispatcher.InvokeAsync) are non-blocking and
// never call back into the recorder, and it keeps every state transition atomic with
// its notification. Do not wire a handler that calls back into this class.
namespace NinjaTrader.NinjaScript.Indicators
{
    internal class BigPrintsRecorder
    {
        public enum RecorderState { Off, Armed, Recording }

        public Action<string>        Log          = delegate { };
        public Action<RecorderState> StateChanged = delegate { };

        private const int    PostEventSeconds  = 180;  // keep recording this long after the LATEST trigger
        private const int    MaxRecordingSecs  = 600;  // hard cap measured from the FIRST trigger's t_end
        private const int    PreContextCapSecs = 300;  // ARMED buffer cap, rolling (spec §7)
        private const int    BookSnapshotMs    = 250;  // sampling cadence while Armed
        private const int    BookSnapshotRecMs = 100;  // tighter cadence while Recording
        private const int    MaxOtherClusters  = 40;
        private const double BackwardsTolSecs  = 2;    // cross-thread timestamp skew tolerance
        private const int    JumpForwardSecs   = 30;   // bigger forward gap in the tape = user jumped playback

        private struct TapeEntry   { public DateTime T; public double Price; public long Size; public int Side; public double Bid; public double Ask; }
        private struct InsideEntry { public DateTime T; public bool IsBid; public double Price; public long Size; }
        private class  BookSnap    { public DateTime T; public double[][] Bids; public double[][] Asks; }
        private class ClusterInfo
        {
            public string   Type;            // "sweep" | "accum" | "stoprun"
            public DateTime MaxTime, TStart, TEnd;
            public bool     IsBuy;
            public double   MaxPrintPrice;
            public long     MaxPrintSize, Volume;
            public int      NPrints;         // prints (sweep/stoprun) or clusters (accum)
            public int      RangeTicks;      // stoprun displacement; 0 otherwise
        }

        private readonly string _baseDir;
        private readonly object _lock = new object();
        private RecorderState _state = RecorderState.Off;
        private bool _auto;                  // auto mode: self-arm, typed triggers, re-arm after write
        private bool _sweepInitiates = true; // false only in auto mode with AutoTriggerSweeps off
        private int  _maxFiles = int.MaxValue;
        private int  _filesWritten;
        private DateTime _armedAt;
        private DateTime _lastSeen;       // last event timestamp seen while active (jump detection)
        private DateTime _lastBookSnapAt;
        private DateTime _deadline;       // Recording ends here (extended per trigger)
        private DateTime _hardCapAt;      // ... but never past here
        private double _postExtreme;      // best post-trigger price toward the trigger side
        private bool   _snapAsap;         // a new extreme printed — snapshot on the next depth event
        private double _lastTapePrice;    // freshest Last print — seeds _postExtreme (market state, survives resets)
        private Queue<TapeEntry>   _tape   = new Queue<TapeEntry>();
        private Queue<InsideEntry> _inside = new Queue<InsideEntry>();
        private Queue<BookSnap>    _book   = new Queue<BookSnap>();
        private ClusterInfo _trigger;
        private List<ClusterInfo> _others = new List<ClusterInfo>();
        private string _instrument = "", _barsText = "", _sessionText = "";

        public BigPrintsRecorder(string baseDir) { _baseDir = baseDir; }

        // Called once from DataLoaded, before any market event reaches the recorder.
        public void Configure(bool autoMode, bool sweepInitiates, int maxFiles)
        {
            lock (_lock)
            {
                _auto           = autoMode;
                _sweepInitiates = sweepInitiates;
                _maxFiles       = maxFiles;
            }
        }

        public RecorderState State { get { lock (_lock) return _state; } }

        public void Toggle(DateTime tapeNow)
        {
            lock (_lock)
            {
                if (_state == RecorderState.Off)
                {
                    if (_auto)
                    {
                        Log(_filesWritten >= _maxFiles
                            ? "BigPrints recorder: auto file cap reached (" + _filesWritten + ") — reload the indicator to record more."
                            : "BigPrints recorder: auto mode arms itself — nothing to click.");
                        return;
                    }
                    if (tapeNow == default(DateTime)) { Log("BigPrints recorder: no tape seen yet — cannot arm."); return; }
                    ResetBuffersLocked();
                    // t0 sits BackwardsTolSecs behind the arm instant so cross-thread
                    // events skewed within tolerance never get a negative ms-since-t0.
                    _armedAt  = tapeNow.AddSeconds(-BackwardsTolSecs);
                    _lastSeen = tapeNow;
                    _state    = RecorderState.Armed;
                    StateChanged(_state);
                    Log("BigPrints recorder: ARMED at " + tapeNow.ToString("HH:mm:ss") + " — waiting for the next big print.");
                }
                else if (_state == RecorderState.Armed)
                {
                    if (_auto) { Log("BigPrints recorder: auto mode is always armed — turn Auto Mode off for manual control."); return; }
                    ResetBuffersLocked();
                    _state = RecorderState.Off;
                    StateChanged(_state);
                    Log("BigPrints recorder: canceled, nothing written.");
                }
                else // Recording — finalize early (useful in both modes)
                {
                    FinalizeLocked(true, false, true, tapeNow == default(DateTime) ? _lastSeen : tapeNow);
                }
            }
        }

        public void OnLast(DateTime t, double price, long size, int side, double bid, double ask)
        {
            lock (_lock)
            {
                _lastTapePrice = price;
                if (!TimeCheckLocked(t)) return;
                _tape.Enqueue(new TapeEntry { T = t, Price = price, Size = size, Side = side, Bid = bid, Ask = ask });
                if (_state == RecorderState.Recording && _trigger != null &&
                    (_trigger.IsBuy ? price > _postExtreme : price < _postExtreme))
                {
                    _postExtreme = price;
                    _snapAsap    = true;
                }
                TrimLocked(t);
            }
        }

        public void OnInside(DateTime t, bool isBid, double price, long size)
        {
            lock (_lock)
            {
                if (!TimeCheckLocked(t)) return;
                _inside.Enqueue(new InsideEntry { T = t, IsBid = isBid, Price = price, Size = size });
                TrimLocked(t);
            }
        }

        // Cheap pre-check so the indicator only copies the book when a snapshot is due.
        public bool WantsBookSnapshot(DateTime t)
        {
            lock (_lock)
            {
                if (_state == RecorderState.Off && !(_auto && _filesWritten < _maxFiles))
                    return false;
                if (_snapAsap)
                    return true;
                int cadence = _state == RecorderState.Recording ? BookSnapshotRecMs : BookSnapshotMs;
                return (t - _lastBookSnapAt).TotalMilliseconds >= cadence;
            }
        }

        public void OnBookSnapshot(DateTime t, double[][] bids, double[][] asks)
        {
            lock (_lock)
            {
                if (!TimeCheckLocked(t)) return;
                _lastBookSnapAt = t;
                _snapAsap       = false;
                _book.Enqueue(new BookSnap { T = t, Bids = bids, Asks = asks });
                TrimLocked(t);
            }
        }

        // Typed trigger from the indicator. Armed -> start Recording (sweeps only if they
        // may initiate); Recording -> log it and extend the window.
        public void OnTrigger(string type, DateTime maxTime, bool isBuy, double maxPrintPrice, long maxPrintSize,
            long volume, int nPrints, int rangeTicks, DateTime tStart, DateTime tEnd,
            string instrument, string barsText, string sessionText)
        {
            lock (_lock)
            {
                if (!TimeCheckLocked(tEnd)) return;
                var info = new ClusterInfo
                {
                    Type = type, MaxTime = maxTime, TStart = tStart, TEnd = tEnd, IsBuy = isBuy,
                    MaxPrintPrice = maxPrintPrice, MaxPrintSize = maxPrintSize,
                    Volume = volume, NPrints = nPrints, RangeTicks = rangeTicks,
                };
                if (_state == RecorderState.Armed)
                {
                    if (type == "sweep" && !_sweepInitiates)
                        return; // context only — still fully visible in the buffered tape
                    _trigger     = info;
                    _instrument  = instrument;
                    _barsText    = barsText;
                    _sessionText = sessionText;
                    // Seed from the freshest print, not the trigger's max-print price:
                    // for "accum" that price can be minutes old and would anchor the
                    // new-extreme snapshot logic to a stale level (review finding).
                    _postExtreme = _lastTapePrice != 0 ? _lastTapePrice : maxPrintPrice;
                    _deadline    = tEnd.AddSeconds(PostEventSeconds);
                    _hardCapAt   = tEnd.AddSeconds(MaxRecordingSecs);
                    _state       = RecorderState.Recording;
                    StateChanged(_state);
                    Log("BigPrints recorder: TRIGGERED [" + type + "] " + (isBuy ? "BUY " : "SELL ") + volume
                        + " @ " + maxPrintPrice.ToString("F2") + " — recording " + PostEventSeconds + "s post (extendable).");
                }
                else if (_state == RecorderState.Recording)
                {
                    if (_others.Count < MaxOtherClusters)
                        _others.Add(info);
                    DateTime d = tEnd.AddSeconds(PostEventSeconds);
                    if (d > _deadline)
                        _deadline = d; // hard cap enforced in TimeCheckLocked
                }
            }
        }

        public void OnTerminated()
        {
            lock (_lock)
            {
                if (_state == RecorderState.Recording)
                    FinalizeLocked(true, false, false, default(DateTime)); // no re-arm: teardown
                if (_state != RecorderState.Off)
                {
                    ResetBuffersLocked();
                    _state = RecorderState.Off;
                    // no StateChanged: teardown
                }
            }
        }

        // Returns false when the caller must drop the event (recorder off / just reset).
        // Also drives auto-arming, the post-window finalize and the playback-jump reset.
        private bool TimeCheckLocked(DateTime t)
        {
            if (_state == RecorderState.Off)
            {
                if (!_auto || _filesWritten >= _maxFiles)
                    return false;
                ResetBuffersLocked();
                _armedAt  = t.AddSeconds(-BackwardsTolSecs);
                _lastSeen = t;
                _state    = RecorderState.Armed;
                StateChanged(_state);
                Log("BigPrints recorder: AUTO-ARMED at " + t.ToString("HH:mm:ss") + ".");
                return true;
            }

            // Deadline BEFORE the jump check: a quiet tape gap can exceed JumpForwardSecs
            // right at window expiry, and the jump branch would then DISCARD a fully-formed
            // capture (review finding). A backward jump cannot satisfy t >= _deadline, so
            // rewinds still fall through to the jump handling below.
            if (_state == RecorderState.Recording && (t >= _deadline || t >= _hardCapAt))
            {
                bool capped = t >= _hardCapAt && _deadline > _hardCapAt;
                FinalizeLocked(false, capped, true, t);
                // In auto mode FinalizeLocked re-armed us — this event belongs to the new pre-context.
                return _state == RecorderState.Armed;
            }

            if (t < _lastSeen.AddSeconds(-BackwardsTolSecs) || (t - _lastSeen).TotalSeconds > JumpForwardSecs)
            {
                Log("BigPrints recorder: playback time jump (" + _lastSeen.ToString("HH:mm:ss")
                    + " -> " + t.ToString("HH:mm:ss") + ") — recording discarded.");
                ResetBuffersLocked();
                if (_auto && _filesWritten < _maxFiles)
                {
                    _armedAt  = t.AddSeconds(-BackwardsTolSecs);
                    _lastSeen = t;
                    _state    = RecorderState.Armed;
                    StateChanged(_state);
                    return true; // this event opens the new pre-context
                }
                _state = RecorderState.Off;
                StateChanged(_state);
                return false;
            }
            if (t > _lastSeen)
                _lastSeen = t;
            return true;
        }

        // While ARMED (no trigger yet) keep only the last PreContextCapSecs of buffer.
        private void TrimLocked(DateTime now)
        {
            if (_state != RecorderState.Armed)
                return;
            DateTime cutoff = now.AddSeconds(-PreContextCapSecs);
            while (_tape.Count   > 0 && _tape.Peek().T   < cutoff) _tape.Dequeue();
            while (_inside.Count > 0 && _inside.Peek().T < cutoff) _inside.Dequeue();
            while (_book.Count   > 0 && _book.Peek().T   < cutoff) _book.Dequeue();
        }

        private void ResetBuffersLocked()
        {
            _tape   = new Queue<TapeEntry>();
            _inside = new Queue<InsideEntry>();
            _book   = new Queue<BookSnap>();
            _others = new List<ClusterInfo>();
            _trigger = null;
            _lastBookSnapAt = default(DateTime);
            _deadline    = default(DateTime);
            _hardCapAt   = default(DateTime);
            _postExtreme = 0;
            _snapAsap    = false;
        }

        // Detach the buffers, hand the payload to a background writer task, then either
        // re-arm (auto mode, under the file cap) or go Off. rearmAt seeds the next
        // epoch's clock — the FINALIZING event's own time, not the stale _lastSeen,
        // so a post-finalize quiet gap or cross-thread skew can't corrupt the new
        // file's t0 or trip a spurious jump discard (review finding).
        private void FinalizeLocked(bool partial, bool capped, bool rearm, DateTime rearmAt)
        {
            Queue<TapeEntry>   tape    = _tape;
            Queue<InsideEntry> inside  = _inside;
            Queue<BookSnap>    book    = _book;
            List<ClusterInfo>  others  = _others;
            ClusterInfo trigger        = _trigger;
            DateTime t0                = _armedAt;
            string instrument          = _instrument;
            string barsText            = _barsText;
            string sessionText         = _sessionText;
            bool auto                  = _auto;

            _filesWritten++;
            ResetBuffersLocked();
            if (auto && rearm && _filesWritten < _maxFiles)
            {
                DateTime seed = rearmAt == default(DateTime) ? _lastSeen : rearmAt;
                _armedAt  = seed.AddSeconds(-BackwardsTolSecs);
                _lastSeen = seed > _lastSeen ? seed : _lastSeen;
                _state    = RecorderState.Armed;
            }
            else
            {
                _state = RecorderState.Off;
                if (auto && _filesWritten >= _maxFiles)
                    Log("BigPrints recorder: auto file cap reached (" + _filesWritten + ") — capture stopped for this session.");
            }
            StateChanged(_state);

            Task.Run(() =>
            {
                try
                {
                    string path = WriteFile(t0, trigger, others, tape, inside, book,
                        instrument, barsText, sessionText, partial, capped, auto);
                    Log("BigPrints recorder: wrote " + path
                        + " (" + tape.Count + " prints, " + inside.Count + " inside updates, "
                        + book.Count + " book snapshots" + (partial ? ", PARTIAL" : "") + (capped ? ", CAPPED" : "") + ")");
                }
                catch (Exception ex)
                {
                    Log("BigPrints recorder: write FAILED — " + ex.Message);
                    lock (_lock) _filesWritten--; // a failed write must not consume the file cap
                }
            });
        }

        // All timestamps in the file are integer ms since meta.t0 (the arm instant).
        private static long Ms(DateTime t0, DateTime t) { return (long)(t - t0).TotalMilliseconds; }

        private string WriteFile(DateTime t0, ClusterInfo trigger, List<ClusterInfo> others,
            Queue<TapeEntry> tape, Queue<InsideEntry> inside, Queue<BookSnap> book,
            string instrument, string barsText, string sessionText, bool partial, bool capped, bool auto)
        {
            string dir = System.IO.Path.Combine(_baseDir, trigger.MaxTime.ToString("yyyy-MM-dd"));
            System.IO.Directory.CreateDirectory(dir);
            // Milliseconds + type in the name: same-second events no longer overwrite each other.
            string name = "event_" + trigger.MaxTime.ToString("HHmmssfff") + "_" + trigger.Type + "_"
                + (trigger.IsBuy ? "buy" : "sell") + trigger.Volume + ".json";
            string path = System.IO.Path.Combine(dir, name);

            using (var sw = new System.IO.StreamWriter(path))
            using (var w = new JsonTextWriter(sw))
            {
                w.WriteStartObject();

                w.WritePropertyName("meta");
                w.WriteStartObject();
                w.WritePropertyName("instrument");    w.WriteValue(instrument);
                w.WritePropertyName("t0");            w.WriteValue(t0.ToString("yyyy-MM-ddTHH:mm:ss.fff"));
                w.WritePropertyName("t_unit");        w.WriteValue("ms since t0 (playback tape time)");
                w.WritePropertyName("recorder_mode"); w.WriteValue(auto ? "auto" : "manual");
                w.WritePropertyName("partial");       w.WriteValue(partial);
                w.WritePropertyName("capped");        w.WriteValue(capped);
                w.WritePropertyName("trigger");       WriteCluster(w, t0, trigger);
                w.WritePropertyName("other_clusters");
                w.WriteStartArray();
                foreach (ClusterInfo c in others) WriteCluster(w, t0, c);
                w.WriteEndArray();
                w.WritePropertyName("bars");          w.WriteValue(barsText);
                w.WritePropertyName("session");       w.WriteValue(sessionText);
                w.WritePropertyName("tape_columns");  w.WriteValue("t, price, size, side(1=buy,-1=sell,0=inside), bid, ask");
                w.WritePropertyName("inside_columns");w.WriteValue("t, type(0=bid,1=ask), price, size");
                w.WriteEndObject();

                w.WritePropertyName("tape");
                w.WriteStartArray();
                foreach (TapeEntry e in tape)
                {
                    w.WriteStartArray();
                    w.WriteValue(Ms(t0, e.T)); w.WriteValue(e.Price); w.WriteValue(e.Size);
                    w.WriteValue(e.Side); w.WriteValue(e.Bid); w.WriteValue(e.Ask);
                    w.WriteEndArray();
                }
                w.WriteEndArray();

                w.WritePropertyName("inside");
                w.WriteStartArray();
                foreach (InsideEntry e in inside)
                {
                    w.WriteStartArray();
                    w.WriteValue(Ms(t0, e.T)); w.WriteValue(e.IsBid ? 0 : 1);
                    w.WriteValue(e.Price); w.WriteValue(e.Size);
                    w.WriteEndArray();
                }
                w.WriteEndArray();

                w.WritePropertyName("book");
                w.WriteStartArray();
                foreach (BookSnap s in book)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("t"); w.WriteValue(Ms(t0, s.T));
                    w.WritePropertyName("bids"); WriteRows(w, s.Bids);
                    w.WritePropertyName("asks"); WriteRows(w, s.Asks);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteEndObject();
            }
            return path;
        }

        private static void WriteCluster(JsonTextWriter w, DateTime t0, ClusterInfo c)
        {
            w.WriteStartObject();
            w.WritePropertyName("type");            w.WriteValue(c.Type);
            w.WritePropertyName("side");            w.WriteValue(c.IsBuy ? "buy" : "sell");
            w.WritePropertyName("volume");          w.WriteValue(c.Volume);
            w.WritePropertyName("max_print_price"); w.WriteValue(c.MaxPrintPrice);
            w.WritePropertyName("max_print_size");  w.WriteValue(c.MaxPrintSize);
            w.WritePropertyName("n_prints");        w.WriteValue(c.NPrints);
            w.WritePropertyName("range_ticks");     w.WriteValue(c.RangeTicks);
            w.WritePropertyName("t_start_ms");      w.WriteValue(Ms(t0, c.TStart));
            w.WritePropertyName("t_end_ms");        w.WriteValue(Ms(t0, c.TEnd));
            w.WriteEndObject();
        }

        private static void WriteRows(JsonTextWriter w, double[][] rows)
        {
            w.WriteStartArray();
            foreach (double[] r in rows)
            {
                w.WriteStartArray();
                w.WriteValue(r[0]); w.WriteValue(r[1]);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
    }
}
