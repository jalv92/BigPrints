#region Using declarations
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
#endregion

// BigPrintsDiscriminator v1.0 — the pre-registered discriminator engine behind
// BigPrintsStrategy's EntryMode = Discriminator (spec: workspace
// docs/superpowers/specs/2026-07-30-bigprints-discriminator-entry-design.md).
//
// Evidence base: ONE recorded reversal (sell304 09:33:46) and ONE recorded continuation
// (sell423 09:42:34), same session, adversarially analyzed in
// docs/audits/2026-07-30-recorder-pair-analysis.md. Thresholds below are that audit's §4
// pre-registration — they are constants on purpose. Changing any of them mid-corpus is a
// NEW hypothesis and restarts that discriminator's validation count. Do not tune.
//
// Threading: this class is fed exclusively from the strategy's OnMarketData (NT8 delivers
// market data serially per instrument), so it is single-threaded by construction — no
// locks. The strategy routes trade decisions to its own strategy thread; this class never
// submits orders. JSONL writes happen at per-trigger rate (not per tick) — synchronous
// File.AppendAllText under a static lock, same precedent as BigPrintsAiClient.
namespace NinjaTrader.NinjaScript.Strategies
{
    internal class BigPrintsDiscriminator
    {
        public Action<string> Log = delegate { };
        public bool LoggingEnabled = true;

        public enum Verdict { Abstain, Reversal, Continuation }

        // ---- pre-registered 2026-07-30, audit §4 — do not tune ----------------------
        private const double T1TopFracReversal     = 0.0005; // max-print percentile <= 0.05%
        private const double T1TopFracContinuation = 0.0008; // >= 0.08%
        private const double T1ThroughputSplit     = 15.0;   // contracts per ms
        private const int    T2TargetLevels        = 16;     // evaluate at 16th distinct level...
        private const int    T2MinLevels           = 8;      // ...abstain under 8
        private const double T2ReversalMax         = 0.5;
        private const double T2ContinuationMin     = 1.0;
        private const long   T3D12ReversalMin      = 0;      // aligned delta (1,2s] >= 0
        private const long   T3D25ReversalMin      = -250;   // aligned delta (2,5s] >= -250
        private const long   T3D12ContinuationMax  = -200;   // aligned delta (1,2s] <= -200
        private const int    DecisionDelayMs       = 5000;
        private const int    HistorySeconds        = 300;    // T1 rolling window
        private const int    MinHistorySeconds     = 30;     // T1 abstains under this
        private const double OutcomeRecoveryFrac   = 0.5;
        private const int    OutcomeWindowSec      = 60;     // after the most recent extreme
        private const int    OutcomeCapSec         = 180;    // after t_end
        private const double BackwardsTolSecs      = 2;      // playback rewind guard

        public class Evaluation
        {
            public DateTime TriggerStart, TriggerTime, DecisionTime;
            public bool     IsBuySweep;
            public long     Volume, MaxPrint;
            public int      SpanMs, NPrints;
            public double   SweepExtreme;      // lowest sweep price (sell) / highest (buy)
            public double  T1TopFrac, T1Throughput;
            public Verdict T1;
            public double  T2Ratio;
            public int     T2LevelsTraded;
            public Verdict T2;
            public long    T3D12, T3D25;       // raw signed delta (buy +, sell -)
            public Verdict T3;
            public int     VotesReversal, VotesContinuation;
            public Verdict Decision;           // Abstain = no trade
            public bool    EnterLong;          // valid when Decision != Abstain
            public bool    Superseded;
            public string  Instrument;         // e.g. "NQ 09-26" — disambiguates the corpus when multiple markets log concurrently
        }

        private readonly double _tickSize;
        private readonly string _instrument;
        public BigPrintsDiscriminator(double tickSize, string instrument) { _tickSize = tickSize; _instrument = instrument; }

        // ---- rolling per-side print history (T1) ------------------------------------
        private struct Pr { public DateTime T; public long Size; }
        private readonly Queue<Pr> _buyPrints  = new Queue<Pr>();
        private readonly Queue<Pr> _sellPrints = new Queue<Pr>();
        private DateTime _historyStart = DateTime.MinValue;

        // ---- pending evaluation (one slot) ------------------------------------------
        private Evaluation _pending;
        private readonly Dictionary<double, long> _lvlVol   = new Dictionary<double, long>();
        private readonly List<double>             _lvlOrder = new List<double>();
        private bool _t2Frozen;
        private long _d12, _d25;

        // ---- outcome tracker (one slot) ---------------------------------------------
        private class Outcome
        {
            public DateTime TriggerTime;
            public bool     IsBuySweep;
            public double   SweepExtreme;
            public double   Extreme;        // running extension low (sell) / high (buy)
            public DateTime ExtremeTime;
        }
        private Outcome _outcome;

        private DateTime _lastSeen = DateTime.MinValue;

        public void Reset()
        {
            _buyPrints.Clear(); _sellPrints.Clear();
            _historyStart = DateTime.MinValue;
            ClearPending();
            _outcome  = null;
            _lastSeen = DateTime.MinValue;
        }

        private void ClearPending()
        {
            _pending = null;
            _lvlVol.Clear(); _lvlOrder.Clear();
            _t2Frozen = false;
            _d12 = 0; _d25 = 0;
        }

        // Playback rewind: tape time moving backwards discards in-flight state (spec §7).
        // Returns false when the caller should drop this event.
        private bool JumpCheck(DateTime t)
        {
            if (_lastSeen != DateTime.MinValue && t < _lastSeen.AddSeconds(-BackwardsTolSecs))
            {
                Log("[BigPrints/disc] tape time jumped backwards — discriminator state reset.");
                Reset();
                _lastSeen = t;
                return false;
            }
            if (t > _lastSeen) _lastSeen = t;
            return true;
        }

        public void OnPrint(DateTime t, double price, long size, bool isBuy)
        {
            if (!JumpCheck(t))
                return;

            if (_historyStart == DateTime.MinValue)
                _historyStart = t;
            Queue<Pr> q = isBuy ? _buyPrints : _sellPrints;
            q.Enqueue(new Pr { T = t, Size = size });
            DateTime cutoff = t.AddSeconds(-HistorySeconds);
            while (_buyPrints.Count  > 0 && _buyPrints.Peek().T  < cutoff) _buyPrints.Dequeue();
            while (_sellPrints.Count > 0 && _sellPrints.Peek().T < cutoff) _sellPrints.Dequeue();

            if (_pending != null)
                AccumulatePending(t, price, size, isBuy);

            if (_outcome != null)
                UpdateOutcome(t, price);
        }

        private void AccumulatePending(DateTime t, double price, long size, bool isBuy)
        {
            Evaluation p = _pending;

            // T3 windows (raw signed delta; alignment applied at verdict time).
            double ms = (t - p.TriggerTime).TotalMilliseconds;
            if (ms > 1000 && ms <= 2000) _d12 += isBuy ? size : -size;
            else if (ms > 2000 && ms <= DecisionDelayMs) _d25 += isBuy ? size : -size;

            // T2: sweep-side prints extending beyond the sweep extreme, per-level volume in
            // first-touch order, counted only until the 16th distinct level is first touched.
            if (!_t2Frozen && isBuy == p.IsBuySweep &&
                (p.IsBuySweep ? price > p.SweepExtreme : price < p.SweepExtreme))
            {
                long v;
                if (!_lvlVol.TryGetValue(price, out v))
                {
                    _lvlVol[price] = size;
                    _lvlOrder.Add(price);
                    if (_lvlOrder.Count >= T2TargetLevels)
                    {
                        ComputeT2(p);
                        _t2Frozen = true;
                    }
                }
                else
                    _lvlVol[price] = v + size;
            }
        }

        // ratio = mean(per-level volume, most recent half of levels) / mean(first half).
        // n=16 gives the audit's exact 8/8 split; 8..15 levels split at n/2 (documented
        // operationalization of "most recent half" for the capped case).
        private void ComputeT2(Evaluation p)
        {
            int n = _lvlOrder.Count;
            p.T2LevelsTraded = n;
            if (n < T2MinLevels)
            {
                p.T2Ratio = 0;
                p.T2 = Verdict.Abstain;
                return;
            }
            int half = n / 2;
            double first = 0, second = 0;
            for (int i = 0; i < half; i++)     first  += _lvlVol[_lvlOrder[i]];
            for (int i = half; i < n; i++)     second += _lvlVol[_lvlOrder[i]];
            first  /= half;
            second /= (n - half);
            p.T2Ratio = second / Math.Max(first, 1e-9);
            p.T2 = p.T2Ratio <= T2ReversalMax     ? Verdict.Reversal
                 : p.T2Ratio >= T2ContinuationMin ? Verdict.Continuation
                 : Verdict.Abstain;
        }

        // Opens a new pending evaluation (computing T1 immediately) and a new outcome
        // tracker. Returns the superseded pending evaluation (partial data, Decision forced
        // to Abstain) for the caller to log, or null.
        // Note: no JumpCheck(tEnd) here — tEnd is the cluster's LAST PRINT time, which can
        // legitimately trail _lastSeen (already advanced by TryEvaluate/OnPrint on this same
        // or a later event) by more than BackwardsTolSecs on a cold cluster. That is normal
        // forward flow, not a rewind. A genuine playback rewind is always caught first by
        // JumpCheck inside TryEvaluate/OnPrint, which runs before this on the same event.
        public Evaluation OnClusterFinalized(DateTime tStart, DateTime tEnd, bool isBuy,
            long volume, long maxPrint, double sweepExtreme, int spanMs, int nPrints)
        {
            Evaluation superseded = null;
            if (_pending != null)
            {
                superseded = _pending;
                if (!_t2Frozen) ComputeT2(superseded);
                FinalizeT3AndVotes(superseded);
                superseded.Decision   = Verdict.Abstain; // superseded — never traded
                superseded.Superseded = true;
                superseded.DecisionTime = tEnd;
            }
            CloseOutcome(tEnd, "UNRESOLVED_SUPERSEDED");

            ClearPending();
            var p = new Evaluation
            {
                TriggerStart = tStart, TriggerTime = tEnd, IsBuySweep = isBuy,
                Volume = volume, MaxPrint = maxPrint, SweepExtreme = sweepExtreme,
                SpanMs = spanMs, NPrints = nPrints, Instrument = _instrument,
            };
            ComputeT1(p);
            _pending = p;

            _outcome = new Outcome
            {
                TriggerTime = tEnd, IsBuySweep = isBuy,
                SweepExtreme = sweepExtreme, Extreme = sweepExtreme, ExtremeTime = tEnd,
            };
            return superseded;
        }

        // T1 uses same-side prints STRICTLY BEFORE tStart (the audit's causal-percentile
        // fix: the trigger's own prints must not rank against themselves).
        private void ComputeT1(Evaluation p)
        {
            p.T1Throughput = p.Volume / Math.Max(p.SpanMs, 1.0);
            Queue<Pr> q = p.IsBuySweep ? _buyPrints : _sellPrints;
            int n = 0, k = 0;
            foreach (Pr pr in q)
            {
                if (pr.T >= p.TriggerStart) continue;
                n++;
                if (pr.Size >= p.MaxPrint) k++;
            }
            bool historyOk = _historyStart != DateTime.MinValue &&
                (p.TriggerStart - _historyStart).TotalSeconds >= MinHistorySeconds && n > 0;
            p.T1TopFrac = n > 0 ? (double)k / n : 1.0;
            if (!historyOk)
                p.T1 = Verdict.Abstain;
            else if (p.T1TopFrac <= T1TopFracReversal && p.T1Throughput < T1ThroughputSplit)
                p.T1 = Verdict.Reversal;
            else if (p.T1TopFrac >= T1TopFracContinuation && p.T1Throughput >= T1ThroughputSplit)
                p.T1 = Verdict.Continuation;
            else
                p.T1 = Verdict.Abstain;
        }

        private void FinalizeT3AndVotes(Evaluation p)
        {
            p.T3D12 = _d12; p.T3D25 = _d25;
            // Aligned so positive = flow AGAINST the sweep (reversal-favorable), matching
            // the audit's sell-sweep convention; buy sweeps mirror by sign flip.
            long a12 = p.IsBuySweep ? -_d12 : _d12;
            long a25 = p.IsBuySweep ? -_d25 : _d25;
            p.T3 = a12 >= T3D12ReversalMin && a25 >= T3D25ReversalMin ? Verdict.Reversal
                 : a12 <= T3D12ContinuationMax                        ? Verdict.Continuation
                 : Verdict.Abstain;

            p.VotesReversal     = (p.T1 == Verdict.Reversal ? 1 : 0) + (p.T2 == Verdict.Reversal ? 1 : 0) + (p.T3 == Verdict.Reversal ? 1 : 0);
            p.VotesContinuation = (p.T1 == Verdict.Continuation ? 1 : 0) + (p.T2 == Verdict.Continuation ? 1 : 0) + (p.T3 == Verdict.Continuation ? 1 : 0);
            if (p.VotesReversal >= 2 && p.VotesContinuation == 0)
                p.Decision = Verdict.Reversal;
            else if (p.VotesContinuation >= 2 && p.VotesReversal == 0)
                p.Decision = Verdict.Continuation;
            else
                p.Decision = Verdict.Abstain;
            // Sell sweep: REVERSAL -> LONG, CONTINUATION -> SHORT. Buy sweep mirrored
            // (unstudied symmetry assumption — both recorded events were sell sweeps).
            p.EnterLong = p.IsBuySweep ? p.Decision == Verdict.Continuation
                                       : p.Decision == Verdict.Reversal;
        }

        // Call on EVERY market-data event (Bid/Ask/Last). Returns the completed evaluation
        // exactly once when the pending one crosses t_end + 5 s.
        public Evaluation TryEvaluate(DateTime now)
        {
            if (!JumpCheck(now))
                return null;
            if (_pending == null || (now - _pending.TriggerTime).TotalMilliseconds < DecisionDelayMs)
                return null;

            Evaluation p = _pending;
            if (!_t2Frozen) ComputeT2(p);
            FinalizeT3AndVotes(p);
            p.DecisionTime = now;
            ClearPending();
            Log(string.Format("[BigPrints/disc] {0} sweep {1} eval: T1={2}({3:0.0000}/{4:0.0}) T2={5}({6:0.00},{7} lvls) T3={8}({9}/{10}) -> {11}{12}",
                p.IsBuySweep ? "BUY" : "SELL", p.Volume, p.T1, p.T1TopFrac, p.T1Throughput,
                p.T2, p.T2Ratio, p.T2LevelsTraded, p.T3, p.T3D12, p.T3D25, p.Decision,
                p.Decision == Verdict.Abstain ? "" : (p.EnterLong ? " -> LONG" : " -> SHORT")));
            return p;
        }

        // Outcome label (audit §4, causal operationalization): the extension extreme is a
        // running min (sell) / max (buy); every new extreme restarts the 60 s window. The
        // "no print >= 8 ticks beyond the low" clause is subsumed: any such print IS a new
        // extreme and restarts the window. REVERSAL resolves on >= 50 % recovery of
        // (sweep_extreme - extension extreme) within the window; CONTINUATION when the
        // window expires unrecovered; UNRESOLVED at t_end + 180 s.
        private void UpdateOutcome(DateTime t, double price)
        {
            Outcome o = _outcome;

            bool newExtreme = o.IsBuySweep ? price > o.Extreme : price < o.Extreme;
            if (newExtreme)
            {
                o.Extreme = price; o.ExtremeTime = t;
            }

            double ext = Math.Abs(o.SweepExtreme - o.Extreme);
            if (ext >= _tickSize / 2)
            {
                double recovery = o.IsBuySweep
                    ? (o.Extreme - price) / ext
                    : (price - o.Extreme) / ext;
                if (recovery >= OutcomeRecoveryFrac && (t - o.ExtremeTime).TotalSeconds <= OutcomeWindowSec)
                {
                    ResolveOutcome(t, "REVERSAL", recovery);
                    return;
                }
            }
            if ((t - o.ExtremeTime).TotalSeconds > OutcomeWindowSec)
            {
                double best = ext >= _tickSize / 2
                    ? (o.IsBuySweep ? (o.Extreme - price) / ext : (price - o.Extreme) / ext)
                    : 0;
                ResolveOutcome(t, "CONTINUATION", best);
                return;
            }
            if ((t - o.TriggerTime).TotalSeconds > OutcomeCapSec)
            {
                // Pre-corpus amendment (2026-07-30, Task-1 review): a move still making new
                // extremes at the cap IS a continuation — UNRESOLVED here would drop the
                // strongest continuations from the corpus scoreboard. UNRESOLVED remains only
                // for the residual case (e.g. sparse tape where neither branch fired).
                bool stillExtending = (t - o.ExtremeTime).TotalSeconds <= OutcomeWindowSec;
                double bestAtCap = ext >= _tickSize / 2
                    ? (o.IsBuySweep ? (o.Extreme - price) / ext : (price - o.Extreme) / ext)
                    : 0;
                ResolveOutcome(t, stillExtending ? "CONTINUATION" : "UNRESOLVED", bestAtCap);
            }
        }

        private void ResolveOutcome(DateTime t, string label, double recovery)
        {
            Outcome o = _outcome;
            _outcome = null;
            double extTicks = Math.Abs(o.SweepExtreme - o.Extreme) / _tickSize;
            Log(string.Format("[BigPrints/disc] outcome {0}: extension {1:0}t, recovery {2:0}%",
                label, extTicks, recovery * 100));
            if (LoggingEnabled)
                DiscriminatorLog.AppendOutcome(o.TriggerTime, _instrument, label, extTicks, recovery * 100, o.Extreme, t);
        }

        private void CloseOutcome(DateTime t, string label)
        {
            if (_outcome == null)
                return;
            Outcome o = _outcome;
            _outcome = null;
            double extTicks = Math.Abs(o.SweepExtreme - o.Extreme) / _tickSize;
            if (LoggingEnabled)
                DiscriminatorLog.AppendOutcome(o.TriggerTime, _instrument, label, extTicks, 0, o.Extreme, t);
        }
    }

    // JSONL logger — same append pattern as BigPrintsAiClient (static lock +
    // File.AppendAllText; per-trigger rate, synchronous writes acceptable).
    internal static class DiscriminatorLog
    {
        private static readonly object LogLock = new object();

        private static string PathFor()
        {
            string dir = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "BigPrintsAI");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "discriminator_log.jsonl");
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

        public static void AppendTrigger(BigPrintsDiscriminator.Evaluation e, string mode, string action)
        {
            Append(new JObject
            {
                ["type"] = "trigger",
                ["ts"] = e.TriggerTime.ToString("o"),
                ["instrument"] = e.Instrument,
                ["mode"] = mode,
                ["side"] = e.IsBuySweep ? "buy" : "sell",
                ["volume"] = e.Volume,
                ["max_print"] = e.MaxPrint,
                ["span_ms"] = e.SpanMs,
                ["n_prints"] = e.NPrints,
                ["sweep_extreme"] = e.SweepExtreme,
                ["t1"] = new JObject { ["top_frac"] = e.T1TopFrac, ["throughput"] = e.T1Throughput, ["verdict"] = e.T1.ToString() },
                ["t2"] = new JObject { ["ratio"] = e.T2Ratio, ["levels_traded"] = e.T2LevelsTraded, ["verdict"] = e.T2.ToString() },
                ["t3"] = new JObject { ["d12"] = e.T3D12, ["d25"] = e.T3D25, ["verdict"] = e.T3.ToString() },
                ["votes"] = new JObject { ["reversal"] = e.VotesReversal, ["continuation"] = e.VotesContinuation },
                ["action"] = action,
            });
        }

        public static void AppendOutcome(DateTime triggerTs, string instrument, string label,
            double extensionTicks, double recoveryPct, double extremePrice, DateTime resolvedTs)
        {
            Append(new JObject
            {
                ["type"] = "outcome",
                ["trigger_ts"] = triggerTs.ToString("o"),
                ["instrument"] = instrument,
                ["label"] = label,
                ["extension_ticks"] = extensionTicks,
                ["recovery_pct"] = recoveryPct,
                ["low_price"] = extremePrice,
                ["resolved_ts"] = resolvedTs.ToString("o"),
            });
        }
    }
}
