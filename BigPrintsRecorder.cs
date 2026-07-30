#region Using declarations
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
#endregion

// BigPrintsRecorder v1.0 — manual "armed" capture of raw microstructure (tape prints
// including inside-spread ones, best bid/ask size updates, sampled L2 snapshots) around
// ONE big-print cluster per Record click, for offline diagnosis of why price reacted.
// Spec: workspace docs/superpowers/specs/2026-07-30-bigprints-event-recorder-design.md
//
// Lifecycle: Off -> (Record click) Armed [buffers fill, nothing written]
//            -> (first cluster >= MinVolume) Recording [keep buffering 120s more]
//            -> write one JSON file on a background task -> Off.
// Click while Armed cancels; click while Recording finalizes early (partial file).
// A playback rewind/jump discards everything and disarms.
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

        private const int    PostEventSeconds  = 120;  // keep recording this long after the trigger cluster
        private const int    PreContextCapSecs = 300;  // ARMED buffer cap, rolling (spec §7)
        private const int    BookSnapshotMs    = 250;
        private const int    MaxOtherClusters  = 20;
        private const double BackwardsTolSecs  = 2;    // cross-thread timestamp skew tolerance
        private const int    JumpForwardSecs   = 30;   // bigger forward gap in the tape = user jumped playback

        private struct TapeEntry   { public DateTime T; public double Price; public long Size; public int Side; public double Bid; public double Ask; }
        private struct InsideEntry { public DateTime T; public bool IsBid; public double Price; public long Size; }
        private class  BookSnap    { public DateTime T; public double[][] Bids; public double[][] Asks; }
        private class ClusterInfo
        {
            public DateTime MaxTime, TStart, TEnd;
            public bool     IsBuy;
            public double   MaxPrintPrice;
            public long     MaxPrintSize, Volume;
            public int      NPrints;
        }

        private readonly string _baseDir;
        private readonly object _lock = new object();
        private RecorderState _state = RecorderState.Off;
        private DateTime _armedAt;
        private DateTime _lastSeen;       // last event timestamp seen while active (jump detection)
        private DateTime _lastBookSnapAt;
        private Queue<TapeEntry>   _tape   = new Queue<TapeEntry>();
        private Queue<InsideEntry> _inside = new Queue<InsideEntry>();
        private Queue<BookSnap>    _book   = new Queue<BookSnap>();
        private ClusterInfo _trigger;
        private List<ClusterInfo> _others = new List<ClusterInfo>();
        private string _instrument = "", _barsText = "", _sessionText = "";

        public BigPrintsRecorder(string baseDir) { _baseDir = baseDir; }

        public RecorderState State { get { lock (_lock) return _state; } }

        public void Toggle(DateTime tapeNow)
        {
            lock (_lock)
            {
                if (_state == RecorderState.Off)
                {
                    if (tapeNow == default(DateTime)) { Log("BigPrints recorder: no tape seen yet — cannot arm."); return; }
                    ResetBuffersLocked();
                    _armedAt  = tapeNow;
                    _lastSeen = tapeNow;
                    _state    = RecorderState.Armed;
                    StateChanged(_state);
                    Log("BigPrints recorder: ARMED at " + tapeNow.ToString("HH:mm:ss") + " — waiting for the next big print.");
                }
                else if (_state == RecorderState.Armed)
                {
                    ResetBuffersLocked();
                    _state = RecorderState.Off;
                    StateChanged(_state);
                    Log("BigPrints recorder: canceled, nothing written.");
                }
                else // Recording — finalize early
                {
                    FinalizeLocked(true);
                }
            }
        }

        public void OnLast(DateTime t, double price, long size, int side, double bid, double ask)
        {
            lock (_lock)
            {
                if (!TimeCheckLocked(t)) return;
                _tape.Enqueue(new TapeEntry { T = t, Price = price, Size = size, Side = side, Bid = bid, Ask = ask });
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
                return _state != RecorderState.Off && (t - _lastBookSnapAt).TotalMilliseconds >= BookSnapshotMs;
        }

        public void OnBookSnapshot(DateTime t, double[][] bids, double[][] asks)
        {
            lock (_lock)
            {
                if (!TimeCheckLocked(t)) return;
                _lastBookSnapAt = t;
                _book.Enqueue(new BookSnap { T = t, Bids = bids, Asks = asks });
                TrimLocked(t);
            }
        }

        public void OnClusterTrigger(DateTime maxTime, bool isBuy, double maxPrintPrice, long maxPrintSize,
            long volume, int nPrints, DateTime tStart, DateTime tEnd,
            string instrument, string barsText, string sessionText)
        {
            lock (_lock)
            {
                if (!TimeCheckLocked(tEnd)) return;
                var info = new ClusterInfo
                {
                    MaxTime = maxTime, TStart = tStart, TEnd = tEnd, IsBuy = isBuy,
                    MaxPrintPrice = maxPrintPrice, MaxPrintSize = maxPrintSize,
                    Volume = volume, NPrints = nPrints,
                };
                if (_state == RecorderState.Armed)
                {
                    _trigger     = info;
                    _instrument  = instrument;
                    _barsText    = barsText;
                    _sessionText = sessionText;
                    _state       = RecorderState.Recording;
                    StateChanged(_state);
                    Log("BigPrints recorder: TRIGGERED by " + (isBuy ? "BUY " : "SELL ") + volume
                        + " @ " + maxPrintPrice.ToString("F2") + " — recording " + PostEventSeconds + "s post.");
                }
                else if (_state == RecorderState.Recording && _others.Count < MaxOtherClusters)
                {
                    _others.Add(info);
                }
            }
        }

        public void OnTerminated()
        {
            lock (_lock)
            {
                if (_state == RecorderState.Recording)
                    FinalizeLocked(true);
                else if (_state == RecorderState.Armed)
                {
                    ResetBuffersLocked();
                    _state = RecorderState.Off;
                    // no StateChanged: the chart is being torn down
                }
            }
        }

        // Returns false when the caller must drop the event (recorder off / just reset).
        // Also drives the post-window finalize and the playback-jump reset.
        private bool TimeCheckLocked(DateTime t)
        {
            if (_state == RecorderState.Off)
                return false;

            if (t < _lastSeen.AddSeconds(-BackwardsTolSecs) || (t - _lastSeen).TotalSeconds > JumpForwardSecs)
            {
                Log("BigPrints recorder: playback time jump (" + _lastSeen.ToString("HH:mm:ss")
                    + " -> " + t.ToString("HH:mm:ss") + ") — recording discarded.");
                ResetBuffersLocked();
                _state = RecorderState.Off;
                StateChanged(_state);
                return false;
            }
            if (t > _lastSeen)
                _lastSeen = t;

            if (_state == RecorderState.Recording && (t - _trigger.TEnd).TotalSeconds >= PostEventSeconds)
            {
                FinalizeLocked(false);
                return false; // this event is past the window
            }
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
        }

        // Detach the buffers, go Off, hand the payload to a background writer task.
        private void FinalizeLocked(bool partial)
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

            ResetBuffersLocked();
            _state = RecorderState.Off;
            StateChanged(_state);

            Task.Run(() =>
            {
                try
                {
                    string path = WriteFile(t0, trigger, others, tape, inside, book,
                        instrument, barsText, sessionText, partial);
                    Log("BigPrints recorder: wrote " + path
                        + " (" + tape.Count + " prints, " + inside.Count + " inside updates, "
                        + book.Count + " book snapshots" + (partial ? ", PARTIAL" : "") + ")");
                }
                catch (Exception ex)
                {
                    Log("BigPrints recorder: write FAILED — " + ex.Message);
                }
            });
        }

        // All timestamps in the file are integer ms since meta.t0 (the arm instant).
        private static long Ms(DateTime t0, DateTime t) { return (long)(t - t0).TotalMilliseconds; }

        private string WriteFile(DateTime t0, ClusterInfo trigger, List<ClusterInfo> others,
            Queue<TapeEntry> tape, Queue<InsideEntry> inside, Queue<BookSnap> book,
            string instrument, string barsText, string sessionText, bool partial)
        {
            string dir = System.IO.Path.Combine(_baseDir, trigger.MaxTime.ToString("yyyy-MM-dd"));
            System.IO.Directory.CreateDirectory(dir);
            string name = "event_" + trigger.MaxTime.ToString("HHmmss") + "_"
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
                w.WritePropertyName("partial");       w.WriteValue(partial);
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
            w.WritePropertyName("side");            w.WriteValue(c.IsBuy ? "buy" : "sell");
            w.WritePropertyName("volume");          w.WriteValue(c.Volume);
            w.WritePropertyName("max_print_price"); w.WriteValue(c.MaxPrintPrice);
            w.WritePropertyName("max_print_size");  w.WriteValue(c.MaxPrintSize);
            w.WritePropertyName("n_prints");        w.WriteValue(c.NPrints);
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
