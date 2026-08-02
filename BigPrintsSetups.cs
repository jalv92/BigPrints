#region Using declarations
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
#endregion

// BigPrintsSetups v1.0 — the strategy's setup library, phase 1 (spec: workspace
// docs/superpowers/specs/2026-08-01-bigprints-setup-library-phase1.md; evidence:
// docs/audits/2026-08-01-day28-mining.md).
//
// FailedRetestLongDetector — "caso 4": failed no-touch retest of the leg low -> LONG.
// LOG-ONLY: it never submits orders. It detects, logs the signal with every qualifying
// quantity, tracks MFE/MAE for 600s and logs the outcome — the corpus decides whether
// this ever earns a Trade switch. Long side only (the studied side); the short mirror
// is deferred, unstudied.
//
// Constants are frozen 2026-08-01, calibrated on the SINGLE verified instance
// (2026-07-28 V-bottom: L0 27603.50 @10:16:52, L1 +10t @10:21:19, band delta -186,
// zero buy volume in the base's last 5 ticks, +110-delta/188c trigger bar @10:21:34).
// n=1: every input is logged so thresholds can be re-scored offline. Do not tune.
//
// Threading: fed exclusively from the strategy's OnMarketData Last path — single-
// threaded by construction, no locks. JSONL writes at signal rate (rare), synchronous
// under a static lock (DiscriminatorLog precedent).
namespace NinjaTrader.NinjaScript.Strategies
{
    // Per-setup switch (public: used by a [NinjaScriptProperty] on the strategy).
    // Off = detector disabled. LogOnly = detect + log, never trade. Trade = also enter
    // via the strategy's standard entry path — Playback evaluation; n=1, not live-validated.
    public enum BigPrintsSetupMode { Off, LogOnly, Trade }

    internal class FailedRetestLongDetector
    {
        public Action<string> Log = delegate { };
        public bool LoggingEnabled = true;
        // Fired at signal time on the market-data thread: (time, entry, stop, trigVol).
        // The strategy publishes it into its own one-slot volatile queue and trades it
        // from the strategy thread — this class itself never touches orders.
        public Action<DateTime, double, double, long> OnSignal = delegate { };

        // ---- frozen 2026-08-01 (n=1-calibrated; logged, do not tune) -----------------
        private const double RallyMinPts      = 25.0;  // arm: rally off L0
        private const double L1MinAbovePts    = 0.25;  // L1 must miss L0 by 1..12 ticks
        private const double L1MaxAbovePts    = 3.00;
        private const int    MinSecsL0toL1    = 120;
        private const double BandPts          = 12.0;  // aggressor-delta band above L0
        private const long   BandDeltaMax     = -100;  // band delta must be <= this
        private const double NearLowPts       = 1.00;  // "last 5 ticks" buy-volume band
        private const int    TriggerWindowSec = 60;    // trigger must come this soon after L1
        private const long   TriggerMinDelta  = 80;    // 1s bar delta
        private const double TriggerVolMult   = 3.0;   // vs trailing-60s per-second mean
        private const int    VolHistoryMinSec = 30;
        private const double StopOffsetPts    = 2.0;   // stop = L0 - this
        private const int    OutcomeTrackSec  = 600;
        private const double BackwardsTolSecs = 2;

        private enum Phase { Tracking, Armed, Consumed }

        private readonly double _tickSize;
        private readonly string _instrument;
        public FailedRetestLongDetector(double tickSize, string instrument) { _tickSize = tickSize; _instrument = instrument; }

        private Phase    _phase = Phase.Tracking;
        private double   _l0 = double.MaxValue;
        private DateTime _l0Time;
        private double   _h;                  // rally high since L0 (valid while Armed)
        private double   _l1;                 // pullback running min since H
        private DateTime _l1Time;
        private long     _bandDelta;          // aggressor delta at prices <= L0+BandPts, since L0
        private long     _nearLowBuyVol;      // buy-aggressor volume at prices <= L0+NearLowPts, since L0
        // Gate values FROZEN at each L1 update — testing the live accumulators would let
        // the recovery's own buys near the low veto tight (1-4 tick) retests forever
        // (review finding): the trigger judges the base AS OF the retest low, not after.
        private long _bandDeltaAtL1, _nearLowBuyVolAtL1;

        // own 1s bar + trailing per-second volume ring (for the trigger's 3x-mean test)
        private long _barSec = long.MinValue;
        private long _barDelta, _barVol;
        private double _barClose;
        private readonly Queue<long> _secVols = new Queue<long>();
        private long _secVolSum;

        // outcome mini-tracker (one slot)
        private class Track
        {
            public DateTime SignalTime;
            public double   Entry, Stop;
            public double   High, Low;
        }
        private Track _track;

        private DateTime _lastSeen = DateTime.MinValue;

        public void Reset()
        {
            _phase = Phase.Tracking;
            _l0 = double.MaxValue;
            _l0Time = default(DateTime);
            _bandDelta = 0; _nearLowBuyVol = 0;
            _barSec = long.MinValue; _barDelta = 0; _barVol = 0; _barClose = 0;
            _secVols.Clear(); _secVolSum = 0;
            if (_track != null)
                CloseTrack(_lastSeen, "reset");
            _lastSeen = DateTime.MinValue;
        }

        private static long SecOf(DateTime t) { return t.Ticks / TimeSpan.TicksPerSecond; }

        public void OnPrint(DateTime t, double price, long size, bool isBuy)
        {
            // Rewind guard (discriminator pattern): backward time discards everything.
            if (_lastSeen != DateTime.MinValue && t < _lastSeen.AddSeconds(-BackwardsTolSecs))
            {
                Log("[BigPrints/frl] tape time jumped backwards — setup state reset.");
                Reset();
            }
            if (t > _lastSeen) _lastSeen = t;

            // 1s bar close: the trigger evaluates on CLOSED bars only.
            long sec = SecOf(t);
            if (sec != _barSec)
            {
                if (_barSec != long.MinValue && sec > _barSec)
                {
                    CheckTrigger();
                    _secVols.Enqueue(_barVol); _secVolSum += _barVol;
                    // Quiet CALENDAR seconds count as zero-volume bars — otherwise the ring
                    // holds print-active seconds only and the 3x-mean gate inflates after
                    // lulls (review finding). Bounded: past 60 skipped seconds the ring is
                    // all zeros anyway.
                    for (long s = _barSec + 1; s < sec && s - _barSec <= 60; s++)
                    { _secVols.Enqueue(0); }
                    while (_secVols.Count > 60) _secVolSum -= _secVols.Dequeue();
                }
                if (sec > _barSec) { _barSec = sec; _barDelta = 0; _barVol = 0; }
                // sec < _barSec (sub-2s jitter): fold into the open bar, same rule as the
                // discriminator's bar builder.
            }
            _barDelta += isBuy ? size : -size;
            _barVol   += size;
            _barClose  = price;
            _lastBarTime = t;

            if (_track != null)
                UpdateTrack(t, price);

            // Base maintenance. A print AT or BELOW L0 voids the setup and re-bases:
            // the rule's premise is a retest that NEVER touches the low.
            if (price <= _l0)
            {
                _l0 = price; _l0Time = t;
                _bandDelta = 0; _nearLowBuyVol = 0;
                _phase = Phase.Tracking;
                return;
            }

            // Band accumulators (since L0).
            if (price <= _l0 + BandPts)
                _bandDelta += isBuy ? size : -size;
            if (price <= _l0 + NearLowPts && isBuy)
                _nearLowBuyVol += size;

            if (_phase == Phase.Tracking)
            {
                if (price >= _l0 + RallyMinPts)
                {
                    _phase = Phase.Armed;
                    _h = price;
                    _l1 = price; _l1Time = t;
                    SnapshotGates();
                }
            }
            else if (_phase == Phase.Armed)
            {
                if (price > _h)
                {
                    _h = price;
                    _l1 = price; _l1Time = t; // new rally high restarts the pullback min
                    SnapshotGates();
                }
                else if (price < _l1)
                {
                    _l1 = price; _l1Time = t;
                    SnapshotGates();
                }
            }
        }

        private void SnapshotGates()
        {
            _bandDeltaAtL1     = _bandDelta;
            _nearLowBuyVolAtL1 = _nearLowBuyVol;
        }

        private DateTime _lastBarTime;

        // Evaluated when a 1s bar CLOSES (the next second's first print arrives).
        private void CheckTrigger()
        {
            if (_phase != Phase.Armed || _track != null)
                return;
            if (_secVols.Count < VolHistoryMinSec)
                return;

            double gap = _l1 - _l0;
            if (gap < L1MinAbovePts || gap > L1MaxAbovePts) return;
            if ((_l1Time - _l0Time).TotalSeconds < MinSecsL0toL1) return;
            if (_bandDeltaAtL1 > BandDeltaMax) return;
            if (_nearLowBuyVolAtL1 > 0) return;
            if ((_lastBarTime - _l1Time).TotalSeconds > TriggerWindowSec) return;

            double meanVol = (double)_secVolSum / _secVols.Count;
            if (_barDelta < TriggerMinDelta || _barVol < TriggerVolMult * Math.Max(meanVol, 1)) return;

            double entry = _barClose;
            double stop  = _l0 - StopOffsetPts;
            _phase = Phase.Consumed;
            _track = new Track { SignalTime = _lastBarTime, Entry = entry, Stop = stop, High = entry, Low = entry };
            Log(string.Format("[BigPrints/frl] SIGNAL LONG (log-only): entry {0:0.00} stop {1:0.00} | L0 {2:0.00} L1 {3:0.00} (+{4:0}t, {5:0}s) bandDelta {6} nearLowBuy {7} trigDelta {8} trigVol {9} ({10:0.0}x)",
                entry, stop, _l0, _l1, gap / _tickSize, (_l1Time - _l0Time).TotalSeconds,
                _bandDelta, _nearLowBuyVol, _barDelta, _barVol, _barVol / Math.Max(meanVol, 1)));
            if (LoggingEnabled)
                SetupLog.AppendSignal(_lastBarTime, _instrument, entry, stop, _l0, _l1,
                    gap / _tickSize, (_l1Time - _l0Time).TotalSeconds, _bandDelta, _nearLowBuyVol,
                    _barDelta, _barVol, _barVol / Math.Max(meanVol, 1));
            OnSignal(_lastBarTime, entry, stop, _barVol);
        }

        private void UpdateTrack(DateTime t, double price)
        {
            Track k = _track;
            if (price > k.High) k.High = price;
            if (price < k.Low)  k.Low  = price;
            if ((t - k.SignalTime).TotalSeconds >= OutcomeTrackSec)
                CloseTrack(t, "tracked");
        }

        // ponytail: no Terminated-time flush — OnStateChange runs on a different thread
        // than OnMarketData and this class is single-threaded by construction (review
        // finding; same teardown decision the strategy already documents for clusters).
        // An in-flight track at teardown becomes an orphan signal in the JSONL, which the
        // validator surfaces — visible, not silent.

        private void CloseTrack(DateTime t, string how)
        {
            Track k = _track;
            _track = null;
            if (k == null || !LoggingEnabled)
                return;
            SetupLog.AppendOutcome(k.SignalTime, _instrument, how,
                (k.High - k.Entry) / _tickSize, (k.Entry - k.Low) / _tickSize,
                k.Low <= k.Stop, t);
        }
    }

    // UnsponsoredExtremeDetector v1.0 — "UE": fade a new 900s extreme made by a 1s bar
    // whose own delta DISAGREES with the break (new high on net selling / new low on net
    // buying), an60-vetoed. LOG-ONLY: registration and thresholds frozen verbatim in
    // docs/audits/2026-08-01-batch8-research.md §Decision 3 — the pre-registered
    // 10-session gate must pass before this ever earns a Trade switch. Same threading
    // model as the other detectors: fed exclusively from OnMarketData, no locks.
    // Honest provenance note: found after ~60 parameter cells on 3 quiet days —
    // the gate is the protection against that search cost. Do not tune.
    internal class UnsponsoredExtremeDetector
    {
        public Action<string> Log = delegate { };
        public bool LoggingEnabled = true;

        // ---- frozen 2026-08-01 (batch-8 registration; do not tune) -------------------
        private const int    WarmupSec    = 1800;
        private const int    WindowSec    = 900;
        private const long   MinBarVol    = 10;
        private const double AnVetoMax    = 0.10;  // an60 x push must be BELOW this
        private const double ArmARangeMax = 120.0; // ARM-A flag only; both arms logged
        private const int    CooldownSec  = 120;   // same-direction, from last fire
        private const double Stop1Pts = 20.0, Tgt1Pts = 40.0; // primary bracket
        private const double Stop2Pts = 15.0, Tgt2Pts = 30.0; // secondary bracket
        private const int    TrackCapSec = 600;
        private const int    ClusterNoteSec = 90;
        private const double BackwardsTolSecs = 2;

        private readonly double _tickSize;
        private readonly string _instrument;
        public UnsponsoredExtremeDetector(double tickSize, string instrument) { _tickSize = tickSize; _instrument = instrument; }

        private class Bar { public long Sec; public double High = double.MinValue, Low = double.MaxValue; public long Delta, Vol; }
        private readonly Queue<Bar> _bars = new Queue<Bar>();
        private Bar _open;

        private DateTime _firstPrint = DateTime.MinValue;
        private DateTime _lastSeen   = DateTime.MinValue;
        private DateTime _lastLongFire  = DateTime.MinValue;
        private DateTime _lastShortFire = DateTime.MinValue;

        // Most recent BigPrints cluster (>= MinVolume), for the pre-fire covariate.
        private DateTime _lastClusterT = DateTime.MinValue;
        private bool _lastClusterBuy;
        private long _lastClusterVol;

        private class Track
        {
            public DateTime SignalTime;
            public bool     IsLong;
            public double   Entry;
            public double   Fav, Adv;              // points
            public string   Res1, Res2;            // null until resolved: "target"/"stop"
        }
        private Track _trackLong, _trackShort;

        public void Reset()
        {
            _bars.Clear(); _open = null;
            _firstPrint = DateTime.MinValue;
            if (_trackLong  != null) CloseTrack(_trackLong,  _lastSeen, "reset");
            if (_trackShort != null) CloseTrack(_trackShort, _lastSeen, "reset");
            _trackLong = null; _trackShort = null;
            _lastLongFire = DateTime.MinValue; _lastShortFire = DateTime.MinValue;
            _lastClusterT = DateTime.MinValue;
            _lastSeen = DateTime.MinValue;
        }

        public void NoteCluster(DateTime t, bool isBuy, long volume)
        {
            _lastClusterT = t; _lastClusterBuy = isBuy; _lastClusterVol = volume;
        }

        private static long SecOf(DateTime t) { return t.Ticks / TimeSpan.TicksPerSecond; }

        public void OnPrint(DateTime t, double price, long size, bool isBuy)
        {
            if (_lastSeen != DateTime.MinValue && t < _lastSeen.AddSeconds(-BackwardsTolSecs))
            {
                Log("[BigPrints/ue] tape time jumped backwards — UE state reset.");
                Reset();
            }
            if (t > _lastSeen) _lastSeen = t;
            if (_firstPrint == DateTime.MinValue) _firstPrint = t;

            long sec = SecOf(t);
            if (_open != null && sec > _open.Sec)
            {
                // The just-closed bar is evaluated against the STRICTLY PRIOR window,
                // then enqueued. This rolling print is the next bar's open = the entry.
                EvaluateClosedBar(_open, t, price);
                _bars.Enqueue(_open);
                long cutoff = sec - WindowSec - 1;
                while (_bars.Count > 0 && _bars.Peek().Sec < cutoff) _bars.Dequeue();
                _open = null;
            }
            if (_open == null || _open.Sec != sec)
            {
                if (_open != null && sec < _open.Sec) { /* sub-tolerance jitter: fold in */ }
                else _open = new Bar { Sec = sec };
            }
            if (price > _open.High) _open.High = price;
            if (price < _open.Low)  _open.Low  = price;
            _open.Delta += isBuy ? size : -size;
            _open.Vol   += size;

            if (_trackLong  != null) UpdateTrack(_trackLong,  t, price);
            if (_trackShort != null) UpdateTrack(_trackShort, t, price);
        }

        private void EvaluateClosedBar(Bar b, DateTime now, double nextOpen)
        {
            if (_firstPrint == DateTime.MinValue || (now - _firstPrint).TotalSeconds < WarmupSec)
                return;
            if (b.Vol < MinBarVol)
                return;

            double h15 = double.MinValue, l15 = double.MaxValue;
            long d60 = 0, v60 = 0;
            long lo900 = b.Sec - WindowSec, lo60 = b.Sec - 60;
            int n = 0;
            foreach (Bar x in _bars)
            {
                if (x.Sec < lo900 || x.Sec >= b.Sec) continue;
                n++;
                if (x.High > h15) h15 = x.High;
                if (x.Low  < l15) l15 = x.Low;
                if (x.Sec > lo60) { d60 += x.Delta; v60 += x.Vol; }
            }
            if (n == 0) return;
            // an60 window is (t-60, t] — the trigger bar itself included (registration).
            d60 += b.Delta; v60 += b.Vol;

            bool shortCand = b.High > h15;                 // high precedence per registration
            bool longCand  = !shortCand && b.Low < l15;
            if (!shortCand && !longCand) return;

            int push = shortCand ? 1 : -1;
            if (v60 <= 0) return;
            double anPush = ((double)d60 / v60) * push;
            if (anPush >= AnVetoMax) return;               // sponsored push — never fade it

            // Unsponsored: the trigger bar's own delta disagrees with the break.
            if (shortCand && b.Delta >= 0) return;
            if (longCand  && b.Delta <= 0) return;

            bool isLong = longCand;
            Track slot = isLong ? _trackLong : _trackShort;
            DateTime lastFire = isLong ? _lastLongFire : _lastShortFire;
            if (slot != null) return;                       // one live track per direction
            if (lastFire != DateTime.MinValue && (now - lastFire).TotalSeconds < CooldownSec) return;

            double r15 = h15 - l15;
            bool armA = r15 <= ArmARangeMax;
            double extreme = isLong ? b.Low : b.High;
            var k = new Track { SignalTime = now, IsLong = isLong, Entry = nextOpen, Fav = 0, Adv = 0 };
            if (isLong) _trackLong = k; else _trackShort = k;
            if (isLong) _lastLongFire = now; else _lastShortFire = now;

            double clusterAgo = _lastClusterT != DateTime.MinValue ? (now - _lastClusterT).TotalSeconds : -1;
            Log(string.Format("[BigPrints/ue] SIGNAL {0} (log-only): entry {1:0.00} | r15 {2:0.00} armA={3} barDelta {4} barVol {5} anPush {6:0.000} extreme {7:0.00}",
                isLong ? "LONG" : "SHORT", nextOpen, r15, armA, b.Delta, b.Vol, anPush, extreme));
            if (LoggingEnabled)
                SetupLog.AppendUeSignal(now, _instrument, isLong, nextOpen, h15, l15, r15, armA,
                    b.Delta, b.Vol, anPush, Math.Abs(nextOpen - extreme) / _tickSize,
                    clusterAgo >= 0 && clusterAgo <= ClusterNoteSec ? (_lastClusterBuy ? "buy" : "sell") : "",
                    clusterAgo >= 0 && clusterAgo <= ClusterNoteSec ? _lastClusterVol : 0,
                    clusterAgo >= 0 && clusterAgo <= ClusterNoteSec ? clusterAgo : -1);
        }

        private void UpdateTrack(Track k, DateTime t, double price)
        {
            double fav = k.IsLong ? price - k.Entry : k.Entry - price;
            double adv = -fav;
            if (fav > k.Fav) k.Fav = fav;
            if (adv > k.Adv) k.Adv = adv;
            // First-touch bracket results, tick path, stop-first ordering within a print.
            if (k.Res1 == null) k.Res1 = adv >= Stop1Pts ? "stop" : (fav >= Tgt1Pts ? "target" : null);
            if (k.Res2 == null) k.Res2 = adv >= Stop2Pts ? "stop" : (fav >= Tgt2Pts ? "target" : null);
            if ((t - k.SignalTime).TotalSeconds >= TrackCapSec)
            {
                CloseTrack(k, t, "tracked");
                if (k.IsLong) _trackLong = null; else _trackShort = null;
            }
        }

        private void CloseTrack(Track k, DateTime t, string how)
        {
            if (!LoggingEnabled) return;
            SetupLog.AppendUeOutcome(k.SignalTime, _instrument, k.IsLong, how,
                k.Fav / _tickSize, k.Adv / _tickSize,
                k.Res1 ?? "cap", k.Res2 ?? "cap", t);
        }
    }

    // JSONL logger for the setup library — separate file from the discriminator corpus.
    internal static class SetupLog
    {
        private static readonly object LogLock = new object();

        private static string PathFor()
        {
            string dir = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "BigPrintsAI");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "setup_log.jsonl");
        }

        private static void Append(JObject o)
        {
            try
            {
                lock (LogLock)
                    System.IO.File.AppendAllText(PathFor(),
                        o.ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine);
            }
            catch (Exception) { /* logging must never break trading */ }
        }

        public static void AppendSignal(DateTime ts, string instrument, double entry, double stop,
            double l0, double l1, double gapTicks, double secsL0toL1, long bandDelta, long nearLowBuyVol,
            long trigDelta, long trigVol, double trigVolMult)
        {
            Append(new JObject
            {
                ["type"] = "signal",
                ["setup"] = "failed_retest_long",
                ["v"] = 1,
                ["ts"] = ts.ToString("o"),
                ["instrument"] = instrument,
                ["entry"] = entry,
                ["stop"] = stop,
                ["l0"] = l0,
                ["l1"] = l1,
                ["gap_ticks"] = gapTicks,
                ["secs_l0_l1"] = secsL0toL1,
                ["band_delta"] = bandDelta,
                ["near_low_buyvol"] = nearLowBuyVol,
                ["trig_delta"] = trigDelta,
                ["trig_vol"] = trigVol,
                ["trig_vol_mult"] = trigVolMult,
            });
        }

        public static void AppendAction(DateTime signalTs, string instrument, string action)
        {
            Append(new JObject
            {
                ["type"] = "signal_action",
                ["setup"] = "failed_retest_long",
                ["v"] = 1,
                ["signal_ts"] = signalTs.ToString("o"),
                ["instrument"] = instrument,
                ["action"] = action, // "entered" or the TryEnter rejection reason; LogOnly never writes this
            });
        }

        public static void AppendUeSignal(DateTime ts, string instrument, bool isLong, double entry,
            double h15, double l15, double r15, bool armA, long barDelta, long barVol, double anPush,
            double distExtremeTicks, string clusterSide, long clusterVol, double clusterSecsAgo)
        {
            Append(new JObject
            {
                ["type"] = "signal",
                ["setup"] = "unsponsored_extreme",
                ["v"] = 1,
                ["ts"] = ts.ToString("o"),
                ["instrument"] = instrument,
                ["dir"] = isLong ? "long" : "short",
                ["entry"] = entry,
                ["h15"] = h15,
                ["l15"] = l15,
                ["r15"] = r15,
                ["arm_a"] = armA,
                ["bar_delta"] = barDelta,
                ["bar_vol"] = barVol,
                ["an_push"] = anPush,
                ["dist_extreme_ticks"] = distExtremeTicks,
                ["cluster_side"] = clusterSide,       // most recent cluster <=90s BEFORE the fire; "" = none
                ["cluster_vol"] = clusterVol,
                ["cluster_secs_ago"] = clusterSecsAgo, // -1 = none
            });
        }

        public static void AppendUeOutcome(DateTime signalTs, string instrument, bool isLong, string how,
            double mfeTicks, double maeTicks, string res2040, string res1530, DateTime resolvedTs)
        {
            Append(new JObject
            {
                ["type"] = "signal_outcome",
                ["setup"] = "unsponsored_extreme",
                ["v"] = 1,
                ["signal_ts"] = signalTs.ToString("o"),
                ["instrument"] = instrument,
                ["dir"] = isLong ? "long" : "short",
                ["how"] = how,
                ["mfe_ticks"] = mfeTicks,
                ["mae_ticks"] = maeTicks,
                ["res_20_40"] = res2040,  // target | stop | cap
                ["res_15_30"] = res1530,
                ["resolved_ts"] = resolvedTs.ToString("o"),
            });
        }

        public static void AppendOutcome(DateTime signalTs, string instrument, string how,
            double mfeTicks, double maeTicks, bool stopHit, DateTime resolvedTs)
        {
            Append(new JObject
            {
                ["type"] = "signal_outcome",
                ["setup"] = "failed_retest_long",
                ["v"] = 1,
                ["signal_ts"] = signalTs.ToString("o"),
                ["instrument"] = instrument,
                ["how"] = how,
                ["mfe_ticks"] = mfeTicks,
                ["mae_ticks"] = maeTicks,
                ["stop_hit"] = stopHit,
                ["resolved_ts"] = resolvedTs.ToString("o"),
            });
        }
    }
}
