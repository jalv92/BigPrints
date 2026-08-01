#region Using declarations
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
#endregion

// BigPrintsDiscriminator v3.0 — context-first discriminator engine behind
// BigPrintsStrategy's EntryMode = Discriminator, RE-REGISTERED FOR NQ 2026-08-01
// (spec: workspace docs/superpowers/specs/2026-08-01-bigprints-discriminator-v3-nq-design.md,
// evidence: docs/audits/2026-08-01-three-cases-analysis.md, 12-agent verified).
//
// v2's MNQ-calibrated arms are DEMOTED to logged-only on NQ: T1 throughput >=15 c/ms is
// unreachable (44x print-batching gap), T1's percentile mis-ranks on NQ's compressed
// print-size tail, T3's contract constants are degenerate. What separates reversal from
// continuation on NQ at t_end+0ms is STRUCTURAL CONTEXT + T2:
//   BALANCE-SWEEP (tested balance low, uniform break)  -> CONTINUATION, enter with sweep
//   VIRGIN-EXTENSION (deep push past everything) + T2  -> REVERSAL, fade the sweep
// Thresholds are frozen constants on purpose — changing any restarts that component's
// corpus count. Registered for NQ; the strategy warns on other instruments.
//
// Threading: fed exclusively from the strategy's OnMarketData (serial per instrument) —
// single-threaded by construction, no locks. Never submits orders. JSONL writes happen
// at per-trigger rate, synchronous under a static lock (BigPrintsAiClient precedent).
namespace NinjaTrader.NinjaScript.Strategies
{
    internal class BigPrintsDiscriminator
    {
        public Action<string> Log = delegate { };
        public bool LoggingEnabled = true;

        public enum Verdict { Abstain, Reversal, Continuation }
        public enum Context { None, BalanceSweep, VirginExtension }

        // ---- pre-registered 2026-08-01 (v3, NQ) — do not tune -----------------------
        private const int    BarWindowSec        = 900;   // context window (15 min of 1s bars)
        private const int    BalanceMinBars      = 450;   // K gates abstain under this
        private const int    VirginMinBars       = 120;   // virgin-extension floor
        private const double K1RangeMinPts       = 60.0;
        private const double K1RangeMaxPts       = 120.0;
        private const double K2TouchBandPts      = 3.0;   // "approached the level" band
        private const int    K2MinEpisodes       = 3;
        private const int    K2GapBreakSec       = 2;     // a >2s bar gap breaks an episode run
        private const long   K3MaxAbsDelta       = 250;   // contracts (NQ)
        private const double B2MinBreakPts       = 6.0;   // sweep must carry through the level
        private const double VirginMinPts        = 12.0;  // depth past the window extreme
        private const double UniMaxPrintFrac     = 0.08;  // max print / cluster volume
        private const int    UniMinPeers         = 3;     // same-side prints >= maxPrint, 300s
        // ---- carried from v2 (audit 2026-07-30), unchanged --------------------------
        private const double T2ReversalMax       = 0.5;
        private const double T2ContinuationMin   = 1.0;
        private const int    T2TargetLevels      = 16;
        private const int    T2MinLevels         = 8;
        private const int    DecisionDelayMs     = 5000;  // reversal-path cap; continuation decides at t_end
        private const int    HistorySeconds      = 300;   // print history (UNI peers + legacy T1)
        private const int    MinHistorySeconds   = 30;
        // ---- legacy, logged-only on NQ (v2 constants kept for the record) -----------
        private const double T1TopFracReversal     = 0.0005;
        private const double T1TopFracContinuation = 0.0007;
        private const double T1ThroughputSplit     = 15.0;
        private const long   T3D12ReversalMin      = 0;
        private const long   T3D25ReversalMin      = -250;
        private const long   T3D12ContinuationMax  = -200;
        // ---- outcome label (amended 2026-07-30 checkpoint rule) ----------------------
        private const double OutcomeRecoveryFrac = 0.5;
        private const int    OutcomeWindowSec    = 60;
        private const int    OutcomeCapSec       = 180;
        private const double BackwardsTolSecs    = 2;

        public class Evaluation
        {
            public DateTime TriggerStart, TriggerTime, DecisionTime;
            public bool     IsBuySweep;
            public long     Volume, MaxPrint;
            public int      SpanMs, NPrints;
            public double   SweepExtreme;
            // v3 context + uniformity
            public Context Ctx;
            public int     CtxBars;            // closed bars available in the window
            public double  CtxHigh, CtxLow, CtxRange;
            public long    CtxDelta;
            public int     CtxEpisodes;        // tests of the swept-side extreme
            public bool    B1FirstBreak;
            public double  B2BreakPts;         // depth through the level (level - extreme, sell)
            public double  VirginPts;          // same quantity, virgin naming for that path
            public bool    KOnly;              // K-context passed but UNI failed (logged, no trade)
            public double  UniMaxFrac;
            public int     UniPeers;
            public bool    UniOk;
            // legacy metrics, logged-only on NQ
            public double  T1TopFrac, T1Throughput;
            public Verdict T1;
            public double  T2Ratio;
            public int     T2LevelsTraded;
            public Verdict T2;
            public long    T3D12, T3D25;
            public Verdict T3;
            public Verdict Decision;           // Abstain = no trade
            public bool    EnterLong;          // valid when Decision != Abstain
            public bool    Superseded;
            public string  Instrument;
        }

        private readonly double _tickSize;
        private readonly string _instrument;
        public BigPrintsDiscriminator(double tickSize, string instrument) { _tickSize = tickSize; _instrument = instrument; }

        // ---- rolling per-side print history (UNI peers + legacy T1) ------------------
        private struct Pr { public DateTime T; public long Size; }
        private readonly Queue<Pr> _buyPrints  = new Queue<Pr>();
        private readonly Queue<Pr> _sellPrints = new Queue<Pr>();
        private DateTime _historyStart = DateTime.MinValue;

        // ---- rolling 1-second bars (context) -----------------------------------------
        // Closed bars only enter the queue; the open bar closes when a later-second print
        // arrives. Context computations use bars with Sec strictly BEFORE the second of
        // the cluster's first print — the anti-leak rule from the 2026-08-01 scan
        // (t_start and t_end can share a second; that second's bar must not count).
        private class SecBar { public long Sec; public double High = double.MinValue, Low = double.MaxValue; public long Delta; }
        private readonly Queue<SecBar> _bars = new Queue<SecBar>();
        private SecBar _openBar;
        // Open-second state BEFORE the most recent print — snapshotted at cluster open so
        // B1 can see whether any pre-cluster print in the same second already broke the level.
        private double _preLowBeforeLastPrint  = double.MaxValue;
        private double _preHighBeforeLastPrint = double.MinValue;
        private double _clusterPreLow  = double.MaxValue;
        private double _clusterPreHigh = double.MinValue;

        // ---- pending evaluation (one slot) ------------------------------------------
        private Evaluation _pending;
        private bool _pendingReady;            // continuation path: decided at t_end+0ms
        // A pending that was READY when a new cluster finalized in the SAME event moves
        // here instead of being superseded — TryEvaluate returns it on the next event.
        // Without this, a valid decision (e.g. T2 freezing on the print that also breaks
        // the cluster) was silently overwritten to Abstain (review finding, critical).
        private Evaluation _completed;
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
            public double   Extreme;
            public DateTime ExtremeTime;
            public double   MaxUp, MaxDn;      // excursions from SweepExtreme (label-defect fix)
        }
        private Outcome _outcome;

        private DateTime _lastSeen = DateTime.MinValue;

        public void Reset()
        {
            _buyPrints.Clear(); _sellPrints.Clear();
            _historyStart = DateTime.MinValue;
            _bars.Clear();
            _openBar = null;
            _preLowBeforeLastPrint  = double.MaxValue;
            _preHighBeforeLastPrint = double.MinValue;
            _clusterPreLow  = double.MaxValue;
            _clusterPreHigh = double.MinValue;
            ClearPending();
            _completed = null;
            _outcome  = null;
            _lastSeen = DateTime.MinValue;
        }

        private void ClearPending()
        {
            _pending = null;
            _pendingReady = false;
            _lvlVol.Clear(); _lvlOrder.Clear();
            _t2Frozen = false;
            _d12 = 0; _d25 = 0;
        }

        // Playback rewind: tape time moving backwards discards in-flight state (spec §7).
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

        private static long SecOf(DateTime t) { return t.Ticks / TimeSpan.TicksPerSecond; }

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

            // 1s bars. Capture the open-second extremes BEFORE folding this print in —
            // OnClusterOpened (called right after this print when it starts a cluster)
            // snapshots them for B1. Backward jitter WITHIN JumpCheck's 2s tolerance
            // (routine cross-source wobble, not a rewind) folds into the OPEN bar: opening
            // a stale-second bar would discard the open bar's accumulation and break the
            // closed-queue time ordering ComputeContext relies on (review finding).
            long sec = SecOf(t);
            if (_openBar != null && _openBar.Sec >= sec)
            {
                _preLowBeforeLastPrint  = _openBar.Low;
                _preHighBeforeLastPrint = _openBar.High;
            }
            else
            {
                _preLowBeforeLastPrint  = double.MaxValue;
                _preHighBeforeLastPrint = double.MinValue;
                if (_openBar != null)
                    _bars.Enqueue(_openBar);
                _openBar = new SecBar { Sec = sec };
                long barCutoff = sec - BarWindowSec;
                while (_bars.Count > 0 && _bars.Peek().Sec < barCutoff) _bars.Dequeue();
            }
            if (price > _openBar.High) _openBar.High = price;
            if (price < _openBar.Low)  _openBar.Low  = price;
            _openBar.Delta += isBuy ? size : -size;

            if (_pending != null)
                AccumulatePending(t, price, size, isBuy);

            if (_outcome != null)
                UpdateOutcome(t, price);
        }

        // Called by the strategy the moment a NEW cluster opens (its first print has just
        // gone through OnPrint). Snapshots the open-second pre-cluster extremes for B1.
        public void OnClusterOpened()
        {
            _clusterPreLow  = _preLowBeforeLastPrint;
            _clusterPreHigh = _preHighBeforeLastPrint;
        }

        private void AccumulatePending(DateTime t, double price, long size, bool isBuy)
        {
            Evaluation p = _pending;

            double ms = (t - p.TriggerTime).TotalMilliseconds;
            if (ms > 1000 && ms <= 2000) _d12 += isBuy ? size : -size;
            else if (ms > 2000 && ms <= DecisionDelayMs) _d25 += isBuy ? size : -size;

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
                        // Reversal path can conclude as soon as T2 is known.
                        if (p.Ctx == Context.VirginExtension)
                            _pendingReady = true;
                    }
                }
                else
                    _lvlVol[price] = v + size;
            }
        }

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

        public Evaluation OnClusterFinalized(DateTime tStart, DateTime tEnd, bool isBuy,
            long volume, long maxPrint, double sweepExtreme, int spanMs, int nPrints)
        {
            Evaluation superseded = null;
            if (_pending != null)
            {
                if (_pendingReady)
                {
                    // Already fully decided, just not yet picked up by TryEvaluate — a new
                    // cluster on the same event must NOT erase the decision (review finding:
                    // T2 can freeze on the very print that breaks the cluster). Park it for
                    // pickup on the next event; the strategy trades/logs it there.
                    Evaluation done = _pending;
                    if (!_t2Frozen) ComputeT2(done);
                    FinalizeDecision(done,
                        (tEnd - done.TriggerTime).TotalMilliseconds < DecisionDelayMs);
                    done.DecisionTime = tEnd;
                    if (_completed != null)
                        Log("[BigPrints/disc] WARNING: completed-decision slot overwritten — should be unreachable.");
                    _completed = done;
                }
                else
                {
                    superseded = _pending;
                    if (!_t2Frozen) ComputeT2(superseded);
                    FinalizeDecision(superseded, earlyFinal: true);
                    superseded.Decision   = Verdict.Abstain; // superseded — never traded
                    superseded.Superseded = true;
                    superseded.DecisionTime = tEnd;
                }
            }
            CloseOutcome(tEnd, "UNRESOLVED_SUPERSEDED");

            ClearPending();
            var p = new Evaluation
            {
                TriggerStart = tStart, TriggerTime = tEnd, IsBuySweep = isBuy,
                Volume = volume, MaxPrint = maxPrint, SweepExtreme = sweepExtreme,
                SpanMs = spanMs, NPrints = nPrints, Instrument = _instrument,
            };
            ComputeT1AndUni(p);
            ComputeContext(p);
            _pending = p;
            // Only BalanceSweep decides at t_end+0ms (the case-3 entry was AT t_end; a 5s
            // wait is pure cost there). None/Virgin hold the 5s window so T2/T3 covariates
            // accumulate for the corpus — the first Playback run of v3 finalized None
            // instantly and logged T2=0 levels on every trigger of the session.
            _pendingReady = p.Ctx == Context.BalanceSweep;

            _outcome = new Outcome
            {
                TriggerTime = tEnd, IsBuySweep = isBuy,
                SweepExtreme = sweepExtreme, Extreme = sweepExtreme, ExtremeTime = tEnd,
            };
            return superseded;
        }

        // UNI peers reuse T1's causal count: same-side prints of size >= maxPrint strictly
        // before tStart within the 300s history window.
        private void ComputeT1AndUni(Evaluation p)
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

            p.UniMaxFrac = p.Volume > 0 ? (double)p.MaxPrint / p.Volume : 1.0;
            p.UniPeers   = k;
            p.UniOk      = historyOk && p.UniMaxFrac <= UniMaxPrintFrac && k >= UniMinPeers;
        }

        // Context from closed 1s bars strictly before the cluster's first-print second.
        private void ComputeContext(Evaluation p)
        {
            long cut = SecOf(p.TriggerStart);
            double hi = double.MinValue, lo = double.MaxValue;
            long delta = 0;
            int nBars = 0;
            foreach (SecBar b in _bars)
            {
                if (b.Sec >= cut) break; // queue is time-ordered
                nBars++;
                if (b.High > hi) hi = b.High;
                if (b.Low  < lo) lo = b.Low;
                delta += b.Delta;
            }
            p.CtxBars  = nBars;
            p.Ctx      = Context.None;
            if (nBars == 0)
                return;
            p.CtxHigh  = hi; p.CtxLow = lo;
            p.CtxRange = hi - lo;
            p.CtxDelta = delta;

            // Swept-side depth through the level (sell sweeps break L15, buys break H15).
            double breakPts = p.IsBuySweep ? p.SweepExtreme - hi : lo - p.SweepExtreme;
            p.B2BreakPts = breakPts;
            p.VirginPts  = breakPts;

            // K2 episodes: contiguous qualifying runs; a non-qualifying bar or a >2s gap
            // in bar seconds breaks the run.
            int episodes = 0;
            bool inRun = false;
            long prevSec = long.MinValue;
            foreach (SecBar b in _bars)
            {
                if (b.Sec >= cut) break;
                bool qual = p.IsBuySweep ? b.High >= hi - K2TouchBandPts
                                         : b.Low  <= lo + K2TouchBandPts;
                if (qual)
                {
                    if (!inRun || b.Sec - prevSec > K2GapBreakSec) episodes++;
                    inRun = true;
                }
                else
                    inRun = false;
                prevSec = b.Sec;
            }
            p.CtxEpisodes = episodes;

            // B1: the cluster contains the FIRST print beyond the level. Closed bars cannot
            // sit beyond their own extreme by construction; only same-second pre-cluster
            // prints (snapshotted at cluster open) can front-run the break.
            p.B1FirstBreak = p.IsBuySweep ? _clusterPreHigh <= hi : _clusterPreLow >= lo;

            bool balance = nBars >= BalanceMinBars
                && p.CtxRange >= K1RangeMinPts && p.CtxRange <= K1RangeMaxPts
                && episodes >= K2MinEpisodes
                && Math.Abs(delta) <= K3MaxAbsDelta
                && p.B1FirstBreak
                && breakPts >= B2MinBreakPts;
            if (balance)
            {
                p.Ctx = Context.BalanceSweep;
                return;
            }
            if (nBars >= VirginMinBars && breakPts >= VirginMinPts)
                p.Ctx = Context.VirginExtension;
        }

        // v3 decision table. earlyFinal: evaluation is being closed before its T3 windows
        // filled (supersede or t_end+0ms continuation decision) — T3 must abstain, not
        // read empty windows as a Reversal vote.
        private void FinalizeDecision(Evaluation p, bool earlyFinal)
        {
            p.T3D12 = _d12; p.T3D25 = _d25;
            long a12 = p.IsBuySweep ? -_d12 : _d12;
            long a25 = p.IsBuySweep ? -_d25 : _d25;
            p.T3 = earlyFinal ? Verdict.Abstain
                 : a12 >= T3D12ReversalMin && a25 >= T3D25ReversalMin ? Verdict.Reversal
                 : a12 <= T3D12ContinuationMax                        ? Verdict.Continuation
                 : Verdict.Abstain;

            if (p.Ctx == Context.BalanceSweep)
            {
                if (p.UniOk)
                    p.Decision = Verdict.Continuation;
                else
                {
                    p.Decision = Verdict.Abstain;
                    p.KOnly    = true; // context passed alone — scored in parallel by the corpus
                }
            }
            else if (p.Ctx == Context.VirginExtension && p.T2 == Verdict.Reversal)
                p.Decision = Verdict.Reversal;
            else
                p.Decision = Verdict.Abstain;

            // Sell sweep: REVERSAL -> LONG (fade), CONTINUATION -> SHORT (follow).
            // Buy sweep mirrored (unstudied symmetry — all studied events were sell sweeps).
            p.EnterLong = p.IsBuySweep ? p.Decision == Verdict.Continuation
                                       : p.Decision == Verdict.Reversal;
        }

        // Call on EVERY market-data event. Returns the completed evaluation exactly once:
        // immediately after t_end for balance/none contexts, at T2-freeze or t_end+5s for
        // the virgin/reversal path.
        public Evaluation TryEvaluate(DateTime now)
        {
            if (!JumpCheck(now))
                return null;
            if (_completed != null)
            {
                Evaluation done = _completed;
                _completed = null;
                LogEvaluation(done);
                return done;
            }
            if (_pending == null)
                return null;
            bool timeUp = (now - _pending.TriggerTime).TotalMilliseconds >= DecisionDelayMs;
            if (!_pendingReady && !timeUp)
                return null;

            Evaluation p = _pending;
            bool early = !timeUp; // decided before the 5s windows filled
            if (!_t2Frozen) ComputeT2(p);
            FinalizeDecision(p, early);
            p.DecisionTime = now;
            ClearPending();
            LogEvaluation(p);
            return p;
        }

        private void LogEvaluation(Evaluation p)
        {
            Log(string.Format("[BigPrints/disc v3] {0} sweep {1} ctx={2}({3} bars, rng {4:0.00}, ep {5}, d15 {6}, brk {7:0.00}{8}) uni={9}({10:0.000}/{11}p) T2={12}({13:0.00},{14} lvls) -> {15}{16}",
                p.IsBuySweep ? "BUY" : "SELL", p.Volume, p.Ctx, p.CtxBars, p.CtxRange,
                p.CtxEpisodes, p.CtxDelta, p.B2BreakPts, p.KOnly ? ", K-ONLY" : "",
                p.UniOk ? "OK" : "no", p.UniMaxFrac, p.UniPeers,
                p.T2, p.T2Ratio, p.T2LevelsTraded, p.Decision,
                p.Decision == Verdict.Abstain ? "" : (p.EnterLong ? " -> LONG" : " -> SHORT")));
        }

        // Session-boundary flush (DailyReset): hand back the in-flight pending so the
        // strategy can log it before Reset() wipes it — an unlogged disappearance
        // undercounts the corpus (review finding). Also closes the outcome tracker.
        public Evaluation FlushPending(DateTime t)
        {
            Evaluation p = _completed;
            _completed = null;
            if (p == null && _pending != null)
            {
                p = _pending;
                if (!_t2Frozen) ComputeT2(p);
                FinalizeDecision(p, (t - p.TriggerTime).TotalMilliseconds < DecisionDelayMs);
                p.Decision   = Verdict.Abstain; // never traded across a session boundary
                p.Superseded = true;
                p.DecisionTime = t;
                ClearPending();
            }
            CloseOutcome(t, "UNRESOLVED_SUPERSEDED");
            return p;
        }

        // Outcome label — checkpoint rule (amended 2026-07-30), plus the 2026-08-01
        // label-defect fix: track both excursions from the sweep extreme so the corpus
        // scorer can see the MAE a "correct" label would have cost.
        private void UpdateOutcome(DateTime t, double price)
        {
            Outcome o = _outcome;

            double up = price - o.SweepExtreme, dn = o.SweepExtreme - price;
            if (up > o.MaxUp) o.MaxUp = up;
            if (dn > o.MaxDn) o.MaxDn = dn;

            bool newExtreme = o.IsBuySweep ? price > o.Extreme : price < o.Extreme;
            if (newExtreme)
            {
                o.Extreme = price; o.ExtremeTime = t;
            }

            double ext = Math.Abs(o.SweepExtreme - o.Extreme);

            if ((t - o.ExtremeTime).TotalSeconds > OutcomeWindowSec)
            {
                if (ext < _tickSize / 2) { ResolveOutcome(t, "UNRESOLVED", 0); return; }
                double recovery = o.IsBuySweep ? (o.Extreme - price) / ext : (price - o.Extreme) / ext;
                ResolveOutcome(t, recovery >= OutcomeRecoveryFrac ? "REVERSAL" : "CONTINUATION", recovery);
                return;
            }
            if ((t - o.TriggerTime).TotalSeconds > OutcomeCapSec)
            {
                double bestAtCap = o.IsBuySweep ? (o.Extreme - price) / ext : (price - o.Extreme) / ext;
                ResolveOutcome(t, "CONTINUATION", bestAtCap);
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
                DiscriminatorLog.AppendOutcome(o.TriggerTime, _instrument, label, extTicks, recovery * 100,
                    o.Extreme, t, o.MaxUp / _tickSize, o.MaxDn / _tickSize);
        }

        private void CloseOutcome(DateTime t, string label)
        {
            if (_outcome == null)
                return;
            Outcome o = _outcome;
            _outcome = null;
            double extTicks = Math.Abs(o.SweepExtreme - o.Extreme) / _tickSize;
            if (LoggingEnabled)
                DiscriminatorLog.AppendOutcome(o.TriggerTime, _instrument, label, extTicks, 0,
                    o.Extreme, t, o.MaxUp / _tickSize, o.MaxDn / _tickSize);
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
                ["v"] = 3,
                ["ts"] = e.TriggerTime.ToString("o"),
                ["instrument"] = e.Instrument,
                ["mode"] = mode,
                ["side"] = e.IsBuySweep ? "buy" : "sell",
                ["volume"] = e.Volume,
                ["max_print"] = e.MaxPrint,
                ["span_ms"] = e.SpanMs,
                ["n_prints"] = e.NPrints,
                ["sweep_extreme"] = e.SweepExtreme,
                ["ctx"] = new JObject
                {
                    ["verdict"] = e.Ctx.ToString(),
                    ["bars"] = e.CtxBars,
                    ["range"] = e.CtxRange,
                    ["episodes"] = e.CtxEpisodes,
                    ["delta15"] = e.CtxDelta,
                    ["b1_first_break"] = e.B1FirstBreak,
                    ["break_pts"] = e.B2BreakPts,
                    ["k_only"] = e.KOnly,
                },
                ["uni"] = new JObject { ["max_frac"] = e.UniMaxFrac, ["peers"] = e.UniPeers, ["ok"] = e.UniOk },
                ["t1"] = new JObject { ["top_frac"] = e.T1TopFrac, ["throughput"] = e.T1Throughput, ["verdict"] = e.T1.ToString() },
                ["t2"] = new JObject { ["ratio"] = e.T2Ratio, ["levels_traded"] = e.T2LevelsTraded, ["verdict"] = e.T2.ToString() },
                ["t3"] = new JObject { ["d12"] = e.T3D12, ["d25"] = e.T3D25, ["verdict"] = e.T3.ToString() },
                ["decision"] = e.Decision.ToString(),
                ["action"] = action,
            });
        }

        public static void AppendOutcome(DateTime triggerTs, string instrument, string label,
            double extensionTicks, double recoveryPct, double extremePrice, DateTime resolvedTs,
            double maxUpTicks, double maxDnTicks)
        {
            Append(new JObject
            {
                ["type"] = "outcome",
                ["v"] = 3,
                ["trigger_ts"] = triggerTs.ToString("o"),
                ["instrument"] = instrument,
                ["label"] = label,
                ["extension_ticks"] = extensionTicks,
                ["recovery_pct"] = recoveryPct,
                ["low_price"] = extremePrice,
                ["max_up_ticks"] = maxUpTicks,
                ["max_dn_ticks"] = maxDnTicks,
                ["resolved_ts"] = resolvedTs.ToString("o"),
            });
        }
    }
}
