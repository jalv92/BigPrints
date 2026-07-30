#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

// BigPrintsStrategy v1.0.0
// Trades in the direction of large aggressive prints (tape sweeps) detected live from the
// Level-I (bid/ask/last) tape. Same cluster-detection idea as the BigPrints indicator in this
// repo (BigPrints.cs), but this file deliberately does NOT reference/import that indicator:
// its signals are event-driven (fired from inside OnMarketData) and are not exposed as a bar
// Series a strategy could read via indicator[0], so the detection logic below is a duplicate,
// trimmed to what trading needs (no draw-anchor price/time tracking, no per-cluster labeling).
// BigPrints.cs remains the reference implementation — if the clustering rules change there,
// mirror the change here too. One deliberate divergence: this strategy finalizes a cluster on
// TAPE-QUOTE TIMEOUT (any Bid/Ask/Last event more than ClusterMilliseconds after the cluster's
// last print — see OnMarketData) for lower entry latency, since a completed sweep often isn't
// followed by another Last print for a while. BigPrints.cs the indicator still waits for the
// next breaking print to finalize and draw — fine there, since a marker showing up a beat late
// is cosmetic, but unacceptable for an entry signal. Bar clocks (Time[0]) must NEVER drive
// cluster lifecycle — on a time-based chart Time[0] of the developing bar is a FUTURE timestamp
// (the bar's eventual close), so comparing it against a tick time overstates elapsed time by the
// remainder of the bar and destroys every cluster before it can finalize.
//
// REAL-TIME / MARKET REPLAY ONLY. OnMarketData does not fire on historical data, so this
// strategy CANNOT be backtested or optimized in the Strategy Analyzer — there is no historical
// tape to replay the aggressor logic against. It only trades while running live or in Market
// Replay (both report State.Realtime, which the code below relies on).
//
// HYBRID ATM MODE (AtmTemplateName parameter — pattern mirrored from TBStrategy.cs in this repo):
//   - Empty (default): NATIVE mode — EnterLong/EnterShort managed orders with ATR brackets.
//   - Set to an ATM template name: ATM mode — entries go through AtmStrategyCreate(); the
//     template's own stop/target manage the exit. Reversal = AtmStrategyClose() on the live ATM,
//     then a new AtmStrategyCreate() once it reports flat (see TryEnterAtm/PollAtmState). ATM
//     order quantity comes from the TEMPLATE's own Quantity setting, NOT the Contracts property
//     (Contracts only applies in native mode — AtmStrategyCreate() has no quantity parameter).
//   - CAVEAT: in ATM mode, Position.MarketPosition (this NinjaScript strategy's own position)
//     stays Flat the whole time — the ATM owns the real position at the account level, so the
//     strategy's chart position/PnL display will NOT reflect it. Use GetAtmStrategyMarketPosition
//     / the ATM's own reporting to see the real state.
//
// THREADING: order submissions are deliberately marshalled from OnMarketData onto the strategy
// thread (OnBarUpdate) via a one-slot signal queue (_signalQueued/_signalIsBuy/_signalTime),
// NOT submitted directly from OnMarketData. Calling AtmStrategyCreate/EnterLong straight off the
// market-data thread crashed NT8 twice in Playback on 2026-07-21 (crash dumps 22:57 and 23:04,
// trace ending in NT8's internal "SQLite error (21): bind on a busy prepared statement" on the
// Strategy2Order association write) — submitting orders while the ATM engine's own Stop1/Target1
// bracket creation lands within milliseconds on a different thread hits a thread-safety bug in
// NT8's internal DB layer. Every NT8 sample (SampleAtmStrategy, TBStrategy) submits from
// OnBarUpdate for the same reason. OnMarketData must stay purely computational — read/compute
// only, zero Enter*/AtmStrategy*/Exit* calls — forever. Latency cost of the queue is <= 1 tick.
//
// PLAYBACK CRASH ROOT CAUSE — CORRECTED 2026-07-22 (WinDbg on 5 of the 8 crash dumps): the
// 2026-07-21 advisory here blamed NT8's ATM engine (traces always ended at SubmitEntryOrders /
// the Strategy2Order SQLite write), but the dumps prove otherwise — EVERY crash, from the first
// (21st 22:57) to the last (22nd 20:01), died on the SAME native stack:
// wdmaud!CWaveOutHandle::_ProcessData -> ucrtbase!memcpy AV on the Windows audio worker thread,
// i.e. a use-after-free of a sound buffer inside NT8's PlaySound path (NAudio, fire-and-forget).
// The ATM correlation was a proxy: the same big print that triggers an entry also triggers the
// BigPrints indicator's chirp (and any NT8 order-event sounds), so death always LOOKED like an
// order-path crash. The SQLite "bind on a busy prepared statement" error was real but non-fatal.
// FIXES: BigPrints.cs now plays sound via winmm PlaySound P/Invoke (Windows owns the buffer);
// keep NT8's OWN event sounds (Tools > Options > Sounds) OFF while running accelerated Playback
// — they use the same crashy NAudio path and nothing in NinjaScript can shield them. ATM mode
// in Playback is NOT the process-killer — but NT staff still call ATM-from-NinjaScript
// unsupported in Playback (forum 1298259: "ATM strategies only work in realtime... use managed
// orders instead"), so native brackets (the ATR stop/target) remain the recommended
// Playback mode. The threading marshal above stays (correct per NT8 rules).
//
// LIVE-MONEY GATE — read before enabling on a funded account:
//   - NATIVE mode brackets are always on since v2: stop = AtrStopMult x ATR(AtrPeriod) with NO
//     tick cap (accepted risk — on a violent day the stop is wide and the Daily Loss Limit
//     governor is the only USD backstop), target = stop x RewardMultiple. ATM mode gets its
//     stop/target from the template instead — see the PLAYBACK CRASH ROOT CAUSE above (ATM in
//     Playback is fine; NT8 event sounds are not).
//   - Daily PnL baseline (_dayStartRealized / _atmRealizedPnLToday) resets on strategy restart,
//     not only on a new trading day — restarting mid-session re-baselines and can hide same-day PnL.
//   - No account-position adoption (StartBehavior.WaitUntilFlat) — a manually-opened position
//     on this account is invisible to this strategy.
//   - ConnectionLossHandling left at its NT8 default — no custom reconnect/flatten policy.
namespace NinjaTrader.NinjaScript.Strategies
{
    public enum BigPrintsEntryMode { Immediate, Discriminator }

    public class BigPrintsStrategy : Strategy
    {
        // --- Level-I state (mirrors BigPrints.cs) ---
        private double _bid;
        private double _ask;

        // --- Cluster engine (mirrors BigPrints.cs, trading-only fields) ---
        private bool     _clusterOpen;
        private bool     _clusterIsBuy;
        private long     _clusterVolume;
        private DateTime _clusterLastTime;
        private DateTime _clusterStartTime;

        // ponytail: same 1500ms hard cap as BigPrints.cs (MaxClusterSpanMs) — not exposed as a
        // parameter there either; keep both in sync if this ever needs tuning.
        private const int MaxClusterSpanMs = 1500;

        // --- Daily risk governor ---
        private double _dayStartRealized;
        private bool   _dailyLockout;
        private int    _lastResetBar = -1;

        // Shared (account-wide) daily PnL mode: all chart instances of this strategy run on the
        // SAME account, so the account's realized+unrealized PnL IS the sum across markets.
        // STATIC registry = shared across every instance in the NT8 process (all NinjaScript
        // lives in one AppDomain): one entry per account holding the trading day, the day's
        // baseline (first instance to see the day writes it, later ones ADOPT it — identical
        // budgets even across mid-session enables), and the breach broadcast. The broadcast is
        // what makes a breach reach every instance: a per-instance watermark can miss a peak
        // that happened between its own instrument's ticks (audit 2026-07-29), but each
        // instance re-reads the shared entry under lock on every one of its ticks — NEVER a
        // cached reference, so a wipe-and-recreate (another instance enabling) can't split the
        // group. DataLoaded wipes this account's entry so a Playback rewind (which resets the
        // account) starts clean; live consequence, documented: restarting ONE instance
        // mid-session re-baselines the shared day budget (matches the existing per-strategy
        // restart semantics), and already-locked instances stay locked via their local
        // _dailyLockout.
        private sealed class AcctDayGov
        {
            public DateTime Day;
            public double   Baseline;
            public volatile bool Breached;
        }
        private static readonly object _acctGovLock = new object();
        private static readonly Dictionary<string, AcctDayGov> _acctGov = new Dictionary<string, AcctDayGov>();
        private DateTime _acctSessionDay; // this instance's current trading day; MinValue = not established -> per-strategy fallback

        // ── Prop governor (min-of-multipliers) — native mode only: SystemPerformance
        // does not track ATM trades, so DD/streak arms are blind in ATM mode (the
        // existing daily lockout still covers ATM via _atmRealizedPnLToday).
        private int    _consecLosses;
        private double _cumRealized;
        private double _equityHigh;
        private bool   _govHaltedToday;
        private int    _lastGovTradeCount;

        // Set BEFORE every Enter/Exit (native) or AtmStrategyCreate/Close (ATM) submission via
        // MarkOrderPending(), cleared once that submission's outcome is confirmed —
        // OnExecutionUpdate in native mode, PollAtmState() in ATM mode (OnExecutionUpdate does
        // NOT fire for ATM-managed orders). Position.MarketPosition is stale until the fill
        // confirms, and NT8's duplicate-order safety net does not cover market orders — without
        // this gate a rapid opposite cluster (or the risk governor's per-tick retry) would
        // double-submit. ORDERING IS LOAD-BEARING: order events arrive on another thread and can
        // process BEFORE the statement after Enter*() runs (documented NT8 race — see the
        // SampleOnOrderUpdate note about assignments not being complete). Setting the flag AFTER
        // the call overwrote the fill's clear and kept it stuck for the entire trade, which
        // blocked reversals AND the daily-target flatten until found on 2026-07-29.
        private volatile bool _orderPending;
        private string   _pendingSignal;        // Name of the tracked in-flight order — gate clears ONLY on ITS terminal event, never on a racing bracket's fill (audit 2026-07-29)
        private DateTime _orderPendingSinceUtc; // WALL clock, not tape time: the watchdog measures order-event latency (a system property), and under accelerated Playback tape time runs many times faster than the events it would be waiting on

        // --- ATM state (Hybrid mode — pattern mirrored from TBStrategy.cs) ---
        private string _atmStrategyId  = string.Empty;
        private string _atmOrderId     = string.Empty;
        private bool   _atmPending;         // AtmStrategyCreate submitted, entry fill not yet confirmed
        private bool   _atmPositionOpen;    // ATM reports a live (non-flat) market position
        private bool   _atmClosing;         // AtmStrategyClose submitted, flat not yet confirmed
        private bool?  _atmReverseToBuy;    // set only when the close is to make room for a reversal
        private double _atmRealizedPnLToday; // SystemPerformance does not track ATM trades — summed by hand

        private bool IsAtmMode => !string.IsNullOrEmpty(AtmTemplateName);

        // ALWAYS call this BEFORE the Enter*/Exit*/AtmStrategy* submission it guards — never
        // after (see the ordering note on _orderPending). signal = the Name the submitted order
        // will carry; only that order's terminal event may clear the gate. ATM submissions pass
        // "ATM" (their gate is cleared by PollAtmState/the create callback, never by name).
        private void MarkOrderPending(string signal)
        {
            _pendingSignal        = signal;
            _orderPending         = true;
            _orderPendingSinceUtc = DateTime.UtcNow;
        }

        // One-slot signal queue — see THREADING note in the header. OnMarketData (market-data
        // thread) writes; OnBarUpdate (strategy thread) reads and clears. Overwrite semantics:
        // a newer cluster replaces an undrained older one, matching "always follow the latest
        // big entry" — there is never more than 1 tick between write and drain anyway.
        // volatile flag + payload-first write order in FinalizeCluster = a true flag always
        // publishes a complete payload to the draining thread (release/acquire pairing).
        private volatile bool _signalQueued;
        private bool     _signalIsBuy;
        private long     _signalVolume;
        private DateTime _signalTime;

        // ---- Discriminator entry (spec 2026-07-30) ----------------------------------
        // Engine exists whenever it has work (Discriminator mode OR logging); null
        // otherwise so every hook is a no-op via `_disc?.`. Fed ONLY from OnMarketData.
        private BigPrintsDiscriminator _disc;
        private long   _clusterMaxPrint;   // largest single print in the open cluster
        private int    _clusterPrintCount;
        private double _clusterExtreme;    // lowest price in a sell cluster / highest in a buy

        // Decision queue: same one-slot volatile pattern as the signal queue. Payload
        // (reference assignment, atomic) BEFORE the flag.
        private volatile bool _decisionQueued;
        private BigPrintsDiscriminator.Evaluation _decisionEval;

        // Aggression-balance reversal filter (feature 2). Ledger of every drained signal (traded
        // or not) within a rolling AggressionWindowSeconds window, used ONLY at the reversal
        // decision to require the new side's recent volume to dominate the held side's — filters
        // a lone counter-signal out of a two-sided battle. OnBarUpdate-only (strategy thread);
        // never touched from OnMarketData.
        private struct AggressionRecord
        {
            public DateTime Time;
            public bool     IsBuy;
            public long     Volume;
        }
        private readonly List<AggressionRecord> _aggressionLedger = new List<AggressionRecord>();

        // Trade cooldown (feature: gates fresh entries from flat only, never reversals). Tape
        // time only — hard rule in this file, never Time[0]/DateTime.Now.
        private DateTime _lastTapeTime;  // updated on every OnMarketData event, all three types
        private DateTime _lastFlatTime;  // when the position last closed; MinValue = no trade yet

        // --- Trade management state (native mode only; see spec 2026-07-29) ---
        // Initialized lazily by ManageTradeStops() on the first tick with a non-flat position
        // (a direction flip re-initializes too, covering one-order reversals), reset on flat.
        // ponytail: hand-rolled Wilder ATR instead of the ATR() system-indicator wrapper —
        // nt8c's per-file check can't resolve system indicator wrappers (same gap as FVGFlow,
        // see FVGFlowStrategy.cs). Math is identical to NinjaTrader's ATR indicator.
        private Series<double> _atrSeries;
        private int    _pendingStopTicks; // stop ticks computed at submission, consumed at init
        private double _tradeEntryPrice;  // 0 = no active trade state
        private bool   _tradeIsLong;
        private double _tradeMaxFav;      // highest High (long) / lowest Low (short) since entry
        private double _tradeFloor;       // hard floor: initial stop price, raised to BE once armed
        private bool   _beArmed;
        private double _lastStopSent;     // CURRENT working stop price: floor at init, updated on each send, resynced by OnOrderUpdate on a failed change
        private DateTime _nextStopModifyUtc; // TIME backoff after a rejected CHANGE (a price-band veto froze a fixed breakeven park forever — audit 2026-07-29); wall clock, it measures system latency
        private bool   _stopOrderFailed;  // stop-loss PLACEMENT rejected -> naked position; OnBarUpdate flattens and retries until flat

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                          = "BigPrintsStrategy";
                Description                   = "Trades in the direction of large aggressive tape prints (sweeps). Real-time / Market Replay only — cannot be backtested in the Strategy Analyzer.";
                Calculate                     = Calculate.OnEachTick;
                EntriesPerDirection           = 1;
                EntryHandling                 = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy  = true;  // safety net — CheckSessionEnd() flattens explicitly too
                IsFillLimitOnTouch            = false;
                StartBehavior                 = StartBehavior.WaitUntilFlat;
                BarsRequiredToTrade           = 1;
                IncludeCommission             = true;  // daily USD limits must be net of commission

                MinVolume            = 150;
                ClusterMilliseconds  = 150;
                Contracts            = 1;
                SessionStart         = 930;   // 09:30
                SessionEnd           = 1555;  // 15:55
                DailyProfitTargetUSD = 500;
                DailyLossLimitUSD    = 300;
                AtmTemplateName      = "";    // empty = native mode
                UseAccountDailyPnL   = true;  // daily target/loss on the ACCOUNT's combined PnL (all instances/markets)
                AtrPeriod            = 14;
                AtrStopMult          = 2.0;   // stop = 2 x ATR, no tick cap (accepted risk — see LIVE-MONEY GATE)
                RewardMultiple       = 1.5;   // target = stop x this
                BreakevenTriggerTicks = 0;    // 0 = breakeven disabled
                BreakevenOffsetTicks  = 4;    // entry +/- 4 ticks once BE arms (covers commissions)
                TrailingEnabled      = true;
                TrailTightMult       = 1.0;   // k when opposing tape flow dominates
                TrailWideMult        = 3.0;   // k when favorable tape flow dominates
                GovTrailingDDRemaining = 0;    // 0 = governor disabled
                GovHorizonsPerDay      = 6;
                GovMaxConsecLosses     = 3;
                GovVolShockMult        = 2.0;
                AggressionWindowSeconds = 180;
                ReversalDominanceRatio  = 1.5;
                CooldownMinutes         = 5;
                EntryMode               = BigPrintsEntryMode.Immediate;
                EnableDiscriminatorLog  = true;
            }
            else if (State == State.Configure)
            {
                // No extra data series — this strategy trades off the Level-I tape (OnMarketData),
                // not off bar closes. The primary Bars series only supplies session/day-boundary
                // context (Time[0], Bars.IsFirstBarOfSession) for the risk/session governor.

                // Native-mode per-trade brackets are armed per-entry in ArmBrackets() (ATR-based,
                // so they can't be known here). Naturally a no-op in ATM mode: "BigPrintLong"/
                // "BigPrintShort" are never used as entry signal names there (AtmStrategyCreate
                // is used instead), so those brackets never have a matching entry to attach to.

                // The trail modifies a live stop against a moving market; a modification that
                // loses that race gets rejected, and the DEFAULT StopCancelClose handling then
                // kills the WHOLE strategy (Playback 2026-07-29: "Stop price can't be changed
                // below the market" -> "terminated itself"). Under accelerated Playback every
                // price the strategy reads can be ticks stale, so no client-side buffer makes
                // the race unlosable — instead, losing it must be benign: ignore + self-manage
                // per the documented pattern. OnOrderUpdate flattens on a rejected stop
                // PLACEMENT (naked position), resyncs the trail tracker on a failed CHANGE
                // (the order keeps working at its old, still-valid price), and entry/flatten
                // rejections were already handled/retried by the existing paths.
                RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;
            }
            else if (State == State.DataLoaded)
            {
                _bid = 0;
                _ask = 0;
                _clusterOpen      = false;
                _dayStartRealized = 0;
                _dailyLockout     = false;
                _lastResetBar     = -1;
                _orderPending         = false;
                _pendingSignal        = string.Empty;
                _orderPendingSinceUtc = DateTime.MinValue;
                _signalQueued     = false;
                _signalIsBuy      = false;
                _signalVolume     = 0;
                _signalTime       = default(DateTime);
                _aggressionLedger.Clear();
                _lastTapeTime     = DateTime.MinValue;
                _lastFlatTime     = DateTime.MinValue; // no trade yet -> no cooldown

                _atrSeries        = new Series<double>(this);
                _pendingStopTicks = 0;
                _stopOrderFailed  = false;
                ResetTradeState();

                _decisionQueued = false;
                _decisionEval   = null;
                _clusterMaxPrint = 0; _clusterPrintCount = 0; _clusterExtreme = 0;
                if (EntryMode == BigPrintsEntryMode.Discriminator || EnableDiscriminatorLog)
                {
                    _disc = new BigPrintsDiscriminator(TickSize)
                    {
                        LoggingEnabled = EnableDiscriminatorLog,
                        Log = msg => Print(msg),
                    };
                }

                // Shared-governor: wipe this account's registry entry so a Playback rewind
                // (which resets the account) starts clean; instances re-register on their next
                // tick via CurrentAccountDay(), so simultaneous enables reconverge on one
                // entry. Live consequence (documented): restarting ONE instance mid-session
                // re-baselines the shared day budget for the account, matching the existing
                // per-strategy restart semantics.
                _acctSessionDay = DateTime.MinValue;
                if (Account != null)
                    lock (_acctGovLock) _acctGov.Remove(Account.Name);

                _atmStrategyId       = string.Empty;
                _atmOrderId          = string.Empty;
                _atmPending          = false;
                _atmPositionOpen     = false;
                _atmClosing          = false;
                _atmReverseToBuy     = null;
                _atmRealizedPnLToday = 0;

                // Soft guardrail, not a block: NT staff call ATM-from-NinjaScript unsupported in
                // Playback (see PLAYBACK CRASH ROOT CAUSE header) — surface it in the output
                // window every time, since the parameter choice is easy to forget between runs.
                if (IsAtmMode && Account != null && Account.Name.StartsWith("Playback"))
                    Print("[BigPrints] WARNING: ATM mode on a Playback account — NT8 does not support ATM-from-NinjaScript in Playback. Prefer native mode (ATR brackets) here.");
            }
        }

        // Risk/session governor — runs every tick (Calculate.OnEachTick fires OnBarUpdate on each
        // tick too), independent of whether a new cluster fired, since unrealized PnL and the
        // session-end boundary both move without a new print.
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 0)
                return;

            // IsFirstBarOfSession stays true for every tick of that bar under Calculate.OnEachTick
            // — without the CurrentBar guard, DailyReset() would re-baseline CumProfit on every
            // tick of the session's first bar, silently absorbing any PnL closed within it.
            if (Bars.IsFirstBarOfSession && CurrentBar != _lastResetBar)
            {
                DailyReset();
                _lastResetBar = CurrentBar;
            }

            // ponytail: cluster lifecycle is driven entirely by tape-clock timeouts inside
            // OnMarketData (see header comment) — no bar-clock cluster hygiene belongs here.

            // Known one-tick staleness vs the forming bar is fine — the trail reads [0] later
            // this same call. Same recompute-every-tick pattern as FVGFlowStrategy.cs, plus a
            // bar-0 guard: TrueRange(0) reads Close[1], which doesn't exist on the first bar.
            if (CurrentBar >= 1)
                _atrSeries[0] = ComputeAtr();

            // Watchdog: a submission NT8 silently ignores produces no order events, so nothing
            // would ever clear _orderPending — and with it stuck, every gate in the strategy
            // (entries, reversals, the daily-target flatten) stays locked for the session.
            // WALL clock on purpose — the deliberate exception to this file's tape-time rule:
            // order-event latency is a system property, and under accelerated Playback 30 tape-
            // seconds elapse in under a second of real time, which would reset the gate while a
            // real order is still in flight (audit 2026-07-29). 60 real seconds without any
            // event for a market order is genuinely dead. Native mode only — ATM legs have
            // their own dead-order detection in PollAtmState(). Runs BEFORE the governor so a
            // freed gate lets a blocked lockout-flatten fire this same tick.
            if (_orderPending && !IsAtmMode && _orderPendingSinceUtc != DateTime.MinValue &&
                (DateTime.UtcNow - _orderPendingSinceUtc).TotalSeconds > 60)
            {
                Print("[BigPrints] WARNING: order pending 60s+ (wall clock) with no order events — submission presumed ignored; resetting the gate.");
                _orderPending = false;
            }

            // Mid-session enable can miss DailyReset entirely (no session-open bar in the
            // loaded data), which would silently leave shared mode inoperative (audit
            // 2026-07-29) — establish the day at the first realtime tick instead, visibly.
            if (UseAccountDailyPnL && _acctSessionDay == DateTime.MinValue &&
                State == State.Realtime && Account != null)
            {
                _acctSessionDay = Time[0].Date;
                CurrentAccountDay();
                Print("[BigPrints] Shared daily PnL day/baseline established at enable (no session-open bar in loaded data).");
            }

            PollAtmState();
            CheckRiskGovernor();
            CheckSessionEnd();
            ManageTradeStops();

            // A rejected stop-loss PLACEMENT leaves the position naked (RealtimeErrorHandling
            // is IgnoreAllErrors — nothing auto-closes anymore): flatten, retrying every tick
            // until confirmed flat, mirroring the daily-lockout retry in CheckRiskGovernor.
            if (_stopOrderFailed)
            {
                if (IsEffectivelyFlat())
                    _stopOrderFailed = false;
                else if (!_orderPending)
                    FlattenNow("BigPrintStopFail");
            }

            // Drain the signal queue LAST, after the governor/session checks above have had a
            // chance to update _dailyLockout/_orderPending on fresh state for this tick.
            if (_signalQueued)
            {
                _signalQueued = false;
                // Ledger BEFORE the gates in TryEnter — a signal that gets skipped (pending/
                // lockout/session) is still real tape flow for the reversal dominance filter.
                RecordAggression(_signalIsBuy, _signalVolume, _signalTime);
                if (EntryMode == BigPrintsEntryMode.Immediate)
                    TryEnter(_signalIsBuy, _signalVolume, _signalTime);
                // Discriminator mode: the cluster only feeds the ledger; entries come from
                // the decision queue below once T1/T2/T3 have spoken.
            }

            if (_decisionQueued)
            {
                _decisionQueued = false;
                BigPrintsDiscriminator.Evaluation dec = _decisionEval;
                _decisionEval = null;
                if (dec != null)
                {
                    string result = TryEnter(dec.EnterLong, dec.Volume, dec.DecisionTime);
                    if (EnableDiscriminatorLog)
                        DiscriminatorLog.AppendTrigger(dec, EntryMode.ToString(),
                            result == "entered" ? (dec.EnterLong ? "long" : "short") : result);
                }
            }
        }

        // OnBarUpdate-only (strategy thread) — see field comment on _aggressionLedger.
        private void RecordAggression(bool isBuy, long volume, DateTime time)
        {
            _aggressionLedger.Add(new AggressionRecord { Time = time, IsBuy = isBuy, Volume = volume });
            PruneAggressionLedger(time);
        }

        private void PruneAggressionLedger(DateTime now)
        {
            DateTime cutoff = now.AddSeconds(-AggressionWindowSeconds);
            _aggressionLedger.RemoveAll(r => r.Time < cutoff);
        }

        private long SumAggression(bool isBuy, DateTime now)
        {
            PruneAggressionLedger(now); // prune before summing too, per spec
            long sum = 0;
            for (int i = 0; i < _aggressionLedger.Count; i++)
                if (_aggressionLedger[i].IsBuy == isBuy)
                    sum += _aggressionLedger[i].Volume;
            return sum;
        }

        // Reversal-only filter — never called for an entry from flat. Requires the new side's
        // recent windowed volume to dominate the held side's by ReversalDominanceRatio, so a
        // lone counter-signal inside a two-sided battle doesn't flip the position.
        private bool PassesReversalFilter(bool heldIsBuy, bool newIsBuy, DateTime now)
        {
            long sumNew  = SumAggression(newIsBuy, now);
            long sumHeld = SumAggression(heldIsBuy, now);

            bool reverse = sumHeld == 0 || sumNew >= ReversalDominanceRatio * sumHeld;

            Print(string.Format("[BigPrints] Reversal check: new {0} sum={1} vs held {2} sum={3} ({4}s) -> {5}",
                newIsBuy ? "BUY" : "SELL", sumNew, heldIsBuy ? "BUY" : "SELL", sumHeld,
                AggressionWindowSeconds, reverse ? "REVERSE" : "HOLD"));

            return reverse;
        }

        // ATM orders don't fire OnExecutionUpdate, so ATM mode confirms fills/closes by polling
        // GetAtmStrategyMarketPosition() every tick — but only while something is actually in
        // flight (hot-path gate below), mirroring TBStrategy's HasAtmPosition() gating.
        private void PollAtmState()
        {
            if (!_atmPending && !_atmPositionOpen && !_atmClosing)
                return;
            if (State != State.Realtime || string.IsNullOrEmpty(_atmStrategyId))
                return;

            MarketPosition? atmPosOpt = SafeGetAtmMarketPosition(_atmStrategyId);
            if (atmPosOpt == null)
                return; // transient exception already printed — retry next tick
            MarketPosition atmPos = atmPosOpt.Value;

            // Deadlock guard: an entry order that dies AFTER AtmStrategyCreate() succeeded (margin
            // rejection, manual cancel, Day-TIF expiry) never produces a position, so the fill-
            // confirmation branch below would never fire and _orderPending would stay true forever
            // — silently locking the strategy out of the market for the rest of the session.
            // Gated on atmPos still Flat: a partial fill that then gets cancelled DOES have a real
            // position, and that case is correctly handled as a fill below, not a dead order here.
            if (_atmPending && atmPos == MarketPosition.Flat)
            {
                string[] status;
                try { status = GetAtmStrategyEntryOrderStatus(_atmOrderId); } // empty until the order registers
                catch (Exception ex)
                {
                    Print(string.Format("[BigPrints] GetAtmStrategyEntryOrderStatus threw: {0}.", ex.Message));
                    status = Array.Empty<string>();
                }
                if (status.Length == 3 &&
                    (string.Equals(status[2], "Rejected", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(status[2], "Cancelled", StringComparison.OrdinalIgnoreCase)))
                {
                    Print(string.Format("[BigPrints] ATM entry order {0} — resetting, ready for the next signal.", status[2]));
                    _atmPending      = false;
                    _atmPositionOpen = false;
                    _atmClosing      = false;
                    _atmReverseToBuy = null;
                    _atmStrategyId   = string.Empty;
                    _atmOrderId      = string.Empty;
                    _orderPending    = false;
                    return;
                }
            }

            if (_atmPending && atmPos != MarketPosition.Flat)
            {
                // Entry confirmed filled.
                _atmPending      = false;
                _atmPositionOpen = true;
                _orderPending    = false;
                Print(string.Format("[BigPrints] ATM entry fill confirmed. Position={0}", atmPos));
            }

            if ((_atmPositionOpen || _atmClosing) && atmPos == MarketPosition.Flat)
            {
                // ATM went flat — book its realized PnL before discarding the id (governor needs it).
                double realized = 0;
                try { realized = GetAtmStrategyRealizedProfitLoss(_atmStrategyId); } catch { }
                _atmRealizedPnLToday += realized;

                // Cooldown clock: no per-fill tape time available here (ATM doesn't report one),
                // so the freshest known tape time is the correct stand-in — never Time[0]/Now.
                _lastFlatTime = _lastTapeTime;

                _atmPositionOpen = false;
                _atmStrategyId   = string.Empty;
                _atmOrderId      = string.Empty;

                bool? reverseTo = _atmReverseToBuy;
                _atmClosing      = false;
                _atmReverseToBuy = null;

                if (reverseTo.HasValue)
                {
                    Print(string.Format("[BigPrints] ATM reversal: close confirmed, firing second leg ({0}).",
                        reverseTo.Value ? "BUY" : "SELL"));
                    CreateAtm(reverseTo.Value); // re-sets _atmPending/_orderPending for the new leg
                }
                else
                {
                    _orderPending = false;
                }
            }
        }

        private void DailyReset()
        {
            // nt8c-safe path (see Apertura4HMSS.cs for the same pattern): documented API for the
            // strategy's own cumulative realized PnL across all trades so far. Native mode only —
            // SystemPerformance does not track ATM trades, hence the separate ATM accumulator.
            _dayStartRealized    = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            _atmRealizedPnLToday = 0;
            _dailyLockout        = false;
            _consecLosses   = 0;
            _govHaltedToday = false;
            _disc?.Reset();  // session boundary: history/pending/outcome all restart (spec §7)

            // Account-wide baseline day for the shared daily governor: the registry entry for
            // this account+day is fetched-or-created per tick in CurrentAccountDay(), so all
            // instances compute against the IDENTICAL baseline (a per-instance snapshot could
            // diverge if a fill landed between two instances' session-open bars — audit
            // 2026-07-29). A new day replaces the entry, which also makes any day-boundary
            // self-reset of the account item harmless (fresh baseline every session).
            _acctSessionDay = Time[0].Date;
            CurrentAccountDay();
        }

        // Fetch-or-create the shared registry entry for this account + this instance's trading
        // day — called EVERY governor tick, never cached (see the field comment on _acctGov).
        // Returns null when shared mode can't operate yet: no day established, no account, or
        // another instance is already on a NEWER day (this instance's session clock is behind —
        // never overwrite a newer entry with an older-keyed one).
        private AcctDayGov CurrentAccountDay()
        {
            if (Account == null || _acctSessionDay == DateTime.MinValue)
                return null;
            lock (_acctGovLock)
            {
                AcctDayGov g;
                _acctGov.TryGetValue(Account.Name, out g);
                if (g != null && g.Day > _acctSessionDay)
                    return null;
                if (g == null || g.Day < _acctSessionDay)
                {
                    g = new AcctDayGov
                    {
                        Day      = _acctSessionDay,
                        Baseline = Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar),
                        Breached = false,
                    };
                    _acctGov[Account.Name] = g;
                }
                return g;
            }
        }

        // True when the strategy has no effective open exposure in the active mode. Position.
        // MarketPosition is always Flat in ATM mode (the ATM owns the real position), so every
        // "do we need to flatten" check must route through this instead of reading Position directly.
        private bool IsEffectivelyFlat()
        {
            return IsAtmMode
                ? (string.IsNullOrEmpty(_atmStrategyId) || !_atmPositionOpen)
                : Position.MarketPosition == MarketPosition.Flat;
        }

        private void CheckRiskGovernor()
        {
            int tc = SystemPerformance.AllTrades.Count;
            if (tc > _lastGovTradeCount)
            {
                for (int i = _lastGovTradeCount; i < tc; i++)
                {
                    Trade t = SystemPerformance.AllTrades[i];
                    _cumRealized  += t.ProfitCurrency;
                    _consecLosses  = t.ProfitCurrency < 0 ? _consecLosses + 1 : 0;
                }
                _lastGovTradeCount = tc;
                _equityHigh = Math.Max(_equityHigh, _cumRealized);
            }

            if (_dailyLockout)
            {
                // Already locked out — a single Exit/Close call isn't guaranteed to fill;
                // keep retrying the flatten every tick until effectively flat.
                if (!IsEffectivelyFlat() && !_orderPending)
                    FlattenNow("BigPrintRiskGovernor");
                return;
            }
            if (DailyProfitTargetUSD <= 0 && DailyLossLimitUSD <= 0)
                return; // both disabled — nothing to compute

            bool hitTarget, hitLoss;
            double dayPnL;
            AcctDayGov gov = UseAccountDailyPnL ? CurrentAccountDay() : null;
            bool sharedMode = gov != null;
            if (sharedMode)
            {
                // Account-wide: the sum across every market/instance on this account. The
                // registry's Breached flag is the broadcast — another instance may have seen a
                // peak between THIS instrument's ticks (per-instance sampling misses it, audit
                // 2026-07-29), so honor the flag before sampling.
                if (gov.Breached)
                {
                    _dailyLockout = true;
                    FlattenNow("BigPrintRiskGovernor");
                    Print("[BigPrintsStrategy] Account-wide daily limit breach broadcast received — locked out until next session.");
                    return;
                }
                double realized   = Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar) - gov.Baseline;
                double unrealized = Account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
                dayPnL = realized + unrealized;
                hitTarget = DailyProfitTargetUSD > 0 && dayPnL >= DailyProfitTargetUSD;
                hitLoss   = DailyLossLimitUSD > 0 && dayPnL <= -DailyLossLimitUSD;
                if (hitTarget || hitLoss)
                    gov.Breached = true; // broadcast to every other instance on this account
            }
            else
            {
                double realized, unrealized;
                if (IsAtmMode)
                {
                    realized   = _atmRealizedPnLToday;
                    unrealized = (_atmPositionOpen && !string.IsNullOrEmpty(_atmStrategyId))
                        ? SafeGetAtmUnrealized(_atmStrategyId)
                        : 0.0;
                }
                else
                {
                    realized   = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - _dayStartRealized;
                    unrealized = Position.MarketPosition != MarketPosition.Flat
                        ? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency)
                        : 0.0;
                }
                dayPnL    = realized + unrealized;
                hitTarget = DailyProfitTargetUSD > 0 && dayPnL >= DailyProfitTargetUSD;
                hitLoss   = DailyLossLimitUSD > 0 && dayPnL <= -DailyLossLimitUSD;
            }

            if (!hitTarget && !hitLoss)
                return;

            _dailyLockout = true;
            FlattenNow("BigPrintRiskGovernor");
            Print(string.Format("[BigPrintsStrategy] Daily {0} hit ({1:F2} USD{2}) — locked out until next session.",
                hitTarget ? "profit target" : "loss limit", dayPnL, sharedMode ? ", account-wide" : ""));
        }

        // ── Prop governor (min-of-multipliers) ───────────────────────────────
        // Four multipliers in [0,1], the SMALLEST wins. Native mode only — SystemPerformance
        // does not track ATM trades (see field comment on the governor state block).
        private int GovernorSize()
        {
            if (GovTrailingDDRemaining <= 0 || IsAtmMode) return Contracts; // disabled / ATM-blind

            double unreal = Position.MarketPosition != MarketPosition.Flat
                ? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency) : 0.0;
            double equity       = _cumRealized + unreal;
            double dailyBudget  = DailyLossLimitUSD > 0 ? DailyLossLimitUSD : 500.0;
            double perTradeRisk = 2.0 * dailyBudget / Math.Sqrt(GovHorizonsPerDay);
            double dailyRealized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - _dayStartRealized;

            double mDd    = Clamp01((GovTrailingDDRemaining - (_equityHigh - equity)) / (3.0 * perTradeRisk));
            double mDaily = Clamp01((dailyBudget + Math.Min(dailyRealized, 0.0)) / perTradeRisk);

            double mVol = 1.0;
            if (CurrentBar >= 50)
            {
                double r10 = 0, r50 = 0;
                for (int i = 0; i < 50; i++)
                {
                    double r = High[i] - Low[i];
                    r50 += r;
                    if (i < 10) r10 += r;
                }
                r10 /= 10.0; r50 /= 50.0;
                if (r50 > 0 && r10 > GovVolShockMult * r50)
                    mVol = r10 > 1.5 * GovVolShockMult * r50 ? 0.0 : 0.5;
            }

            double mStreak = GovMaxConsecLosses > 0 && _consecLosses >= GovMaxConsecLosses ? 0.0 : 1.0;

            if ((mDaily <= 0.0 || mStreak <= 0.0) && !_govHaltedToday)
            {
                _govHaltedToday = true;
                Print(string.Format("[BigPrints] governor HALT for session (mDaily={0:0.00} mStreak={1:0.00} consecLosses={2})", mDaily, mStreak, _consecLosses));
            }
            if (_govHaltedToday) return 0;

            double m = Math.Min(Math.Min(mDd, mDaily), Math.Min(mVol, mStreak));
            return (int)Math.Floor(Contracts * m + 1e-9);
        }

        private static double Clamp01(double x) { return x < 0 ? 0 : (x > 1 ? 1 : x); }

        private void CheckSessionEnd()
        {
            // !InSession() also flattens correctly for an overnight (wraparound) window — no
            // separate ">= SessionEnd" comparison needed.
            if (!InSession(Time[0]) && !IsEffectivelyFlat())
                FlattenNow("BigPrintSessionEnd");
        }

        private void FlattenNow(string signalName)
        {
            if (_orderPending)
                return; // a submission is already in flight — don't stack a second one

            if (IsAtmMode)
            {
                if (!string.IsNullOrEmpty(_atmStrategyId) && (_atmPositionOpen || _atmPending))
                {
                    _atmClosing      = true;
                    _atmReverseToBuy = null; // plain flatten — no reversal queued behind this close
                    MarkOrderPending("ATM");
                    Print(string.Format("[BigPrints] ATM close submitted: flatten ({0})", signalName));
                    AtmStrategyClose(_atmStrategyId);
                }
                return;
            }

            // Two-arg overload on purpose: ExitLong(string) is fromEntrySignal, NOT a signal
            // name — no entry is named "BigPrintRiskGovernor", so NT8 silently ignored the exit
            // and the daily lockout never actually flattened (bug found 2026-07-29). Empty
            // fromEntrySignal = attach the exit to ALL entries.
            if (Position.MarketPosition == MarketPosition.Long)
            {
                MarkOrderPending(signalName);
                ExitLong(signalName, "");
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                MarkOrderPending(signalName);
                ExitShort(signalName, "");
            }
        }

        private double SafeGetAtmUnrealized(string atmId)
        {
            try { return GetAtmStrategyUnrealizedProfitLoss(atmId); }
            catch { return 0.0; }
        }

        // Shared by PollAtmState and TryEnterAtm. A transient invalid-id exception (e.g. mid ATM
        // teardown) must never propagate out of a strategy event — null signals "try again next
        // tick" to the caller instead.
        private MarketPosition? SafeGetAtmMarketPosition(string atmId)
        {
            try { return GetAtmStrategyMarketPosition(atmId); }
            catch (Exception ex)
            {
                Print(string.Format("[BigPrints] GetAtmStrategyMarketPosition threw: {0}.", ex.Message));
                return null;
            }
        }

        private bool InSession(DateTime marketTime)
        {
            int t     = ToTime(marketTime);
            int start = SessionStart * 100;
            int end   = SessionEnd * 100;
            // Overnight window (e.g. start 1800, end 900): session wraps midnight.
            return start <= end ? (t >= start && t < end) : (t >= start || t < end);
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (CurrentBar < 0 || State != State.Realtime)
                return;

            // Freshest tape time, for every event type — used by PollAtmState to timestamp an
            // ATM flat-transition it detects without a per-fill time of its own (see cooldown).
            _lastTapeTime = e.Time;

            // Discriminator heartbeat on every event type: resolves a pending evaluation
            // once it crosses t_end + 5s. Must run BEFORE the cluster-timeout finalize
            // below — otherwise a cluster finalized by this same event would supersede an
            // evaluation that was already due.
            BigPrintsDiscriminator.Evaluation ev = _disc?.TryEvaluate(e.Time);
            if (ev != null)
            {
                if (EntryMode == BigPrintsEntryMode.Discriminator &&
                    ev.Decision != BigPrintsDiscriminator.Verdict.Abstain)
                {
                    _decisionEval   = ev;   // payload before flag (volatile publish)
                    _decisionQueued = true;
                }
                else if (EnableDiscriminatorLog)
                {
                    DiscriminatorLog.AppendTrigger(ev, EntryMode.ToString(),
                        EntryMode == BigPrintsEntryMode.Immediate ? "immediate_entry" : "no_trade");
                }
            }

            // Tape-clock timeout: quotes keep flowing even when prints pause, so ANY event type
            // (Bid/Ask/Last) more than ClusterMilliseconds after the cluster's last print is the
            // silence-breaker that finalizes it — lower latency than waiting for the next Last
            // print to break it. No double-finalize risk: FinalizeCluster sets _clusterOpen=false,
            // so if this tick IS itself a same-side Last print continuing the sweep, the block
            // below simply won't see an open cluster to fold into and starts a fresh one instead.
            if (_clusterOpen && (e.Time - _clusterLastTime).TotalMilliseconds > ClusterMilliseconds)
                FinalizeCluster(e.Time);

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

            _disc?.OnPrint(e.Time, e.Price, e.Volume, isBuy);

            if (_clusterOpen &&
                isBuy == _clusterIsBuy &&
                (e.Time - _clusterLastTime).TotalMilliseconds <= ClusterMilliseconds &&
                (e.Time - _clusterStartTime).TotalMilliseconds <= MaxClusterSpanMs)
            {
                // Same side, within the sweep window and the hard wall-clock cap — fold into the cluster.
                _clusterVolume  += e.Volume;
                _clusterLastTime = e.Time;
                _clusterPrintCount++;
                if (e.Volume > _clusterMaxPrint) _clusterMaxPrint = e.Volume;
                _clusterExtreme = _clusterIsBuy ? Math.Max(_clusterExtreme, e.Price)
                                                : Math.Min(_clusterExtreme, e.Price);
                return;
            }

            // Opposite side, gap exceeded, or max span exceeded — finalize the open cluster, start a new one.
            FinalizeCluster(e.Time);

            _clusterOpen      = true;
            _clusterIsBuy     = isBuy;
            _clusterVolume    = e.Volume;
            _clusterLastTime  = e.Time;
            _clusterStartTime = e.Time;
            _clusterMaxPrint   = e.Volume;
            _clusterPrintCount = 1;
            _clusterExtreme    = e.Price;
        }

        // ponytail: unlike BigPrints.cs, there is no Terminated-time flush here — a strategy has
        // nothing useful to draw at teardown, and firing a trade decision off a mid-shutdown
        // cluster would be actively unwanted. An in-flight cluster at Terminated is simply dropped.
        //
        // now = the time of the print that's finalizing the cluster (may be later than the
        // cluster's own _clusterLastTime — an opposite-side or gap-breaking print). Used both as
        // a staleness gate and as the InSession() check's clock, since it's the freshest time we have.
        private void FinalizeCluster(DateTime now)
        {
            if (!_clusterOpen)
                return;

            _clusterOpen = false;

            if (_clusterVolume < MinVolume)
                return;

            Print(string.Format("[BigPrints] Cluster {0} {1} contracts @ {2:HH:mm:ss.fff}",
                _clusterIsBuy ? "BUY" : "SELL", _clusterVolume, now));

            // Discriminator feed + trigger logging (both modes). A superseded pending
            // evaluation comes back partially evaluated for the log.
            if (_disc != null)
            {
                int spanMs = (int)(_clusterLastTime - _clusterStartTime).TotalMilliseconds;
                BigPrintsDiscriminator.Evaluation superseded = _disc.OnClusterFinalized(
                    _clusterStartTime, _clusterLastTime, _clusterIsBuy, _clusterVolume,
                    _clusterMaxPrint, _clusterExtreme, spanMs, _clusterPrintCount);
                if (superseded != null && EnableDiscriminatorLog)
                    DiscriminatorLog.AppendTrigger(superseded, EntryMode.ToString(), "superseded");
            }

            // Cold signal — the cluster's last print is too far behind "now" to still be
            // actionable. With the tape-clock timeout in OnMarketData now finalizing clusters
            // within ~ClusterMilliseconds of their real end, this only fires if the ENTIRE tape
            // (quotes included) went silent for 2s+ — a genuinely dead market, where skipping is right.
            if ((now - _clusterLastTime).TotalMilliseconds > 2000)
            {
                Print("[BigPrints] Cluster not traded: cold signal (tape silent 2s+ since the cluster's last print).");
                return;
            }

            // Queue for OnBarUpdate instead of calling TryEnter directly here — this runs on the
            // market-data thread (see THREADING note in the header); order submissions must not.
            // Payload BEFORE flag: _signalQueued is the volatile publish point.
            _signalIsBuy  = _clusterIsBuy;
            _signalVolume = _clusterVolume;
            _signalTime   = now;
            _signalQueued = true;
        }

        // Entry / reversal logic — dispatches to the active mode.
        //   1. Flat            -> enter in the aggressor's direction.
        //   2. Opposite cluster while positioned -> reverse.
        //   3. Same-direction cluster while positioned -> no-op, stay in.
        // Native mode arms ATR brackets per entry (ArmBrackets) and manages them per tick
        // (ManageTradeStops: breakeven + order-flow trailing). ATM mode gets its stop/target
        // from the template instead (see AtmTemplateName).
        private string TryEnter(bool isBuy, long volume, DateTime marketTime)
        {
            if (_orderPending)
            {
                Print("[BigPrints] Cluster not traded: an order/ATM operation is already pending.");
                return "pending";
            }
            if (_dailyLockout)
            {
                Print("[BigPrints] Cluster not traded: daily lockout active.");
                return "lockout";
            }
            if (!InSession(marketTime))
            {
                Print("[BigPrints] Cluster not traded: outside the session window.");
                return "session";
            }

            return IsAtmMode
                ? TryEnterAtm(isBuy, volume, marketTime)
                : TryEnterNative(isBuy, volume, marketTime);
        }

        // Cooldown gate — fresh entries from flat ONLY; reversals are exempt (governed by the
        // aggression-balance filter instead), and so is the ATM reversal second leg (fired
        // directly from PollAtmState via CreateAtm, bypassing this method entirely). Tape time
        // only — hard rule in this file, never Time[0]/DateTime.Now.
        private bool PassesCooldown(bool isBuy, long volume, DateTime now)
        {
            if (CooldownMinutes <= 0 || _lastFlatTime == DateTime.MinValue)
                return true;

            double elapsedMin = (now - _lastFlatTime).TotalMinutes;
            if (elapsedMin >= CooldownMinutes)
                return true;

            int remainingSec = (int)Math.Ceiling((CooldownMinutes - elapsedMin) * 60.0);
            Print(string.Format("[BigPrints] Signal {0} {1} skipped (cooldown, {2}s remaining)",
                isBuy ? "BUY" : "SELL", volume, remainingSec));
            return false;
        }

        // ── Trade management: ATR, brackets, breakeven, order-flow trailing ──
        // Spec: docs/superpowers/specs/2026-07-29-bigprints-trade-management-design.md (main-project).

        // Wilder ATR, identical formula to NinjaTrader's system ATR indicator (same hand-rolled
        // pattern as FVGFlowStrategy.cs — see the field comment on _atrSeries for why). Seeds as
        // a simple average of the first AtrPeriod true ranges, then smooths with k = 1/AtrPeriod.
        private double ComputeAtr()
        {
            double tr = TrueRange(0);

            if (CurrentBar < AtrPeriod)
            {
                double sum = tr;
                for (int k = 1; k < CurrentBar; k++)
                    sum += TrueRange(k);
                return sum / CurrentBar;
            }

            double prevAtr = _atrSeries[1];
            return prevAtr + (tr - prevAtr) / AtrPeriod;
        }

        private double TrueRange(int barsAgo)
        {
            double hl = High[barsAgo] - Low[barsAgo];
            double hc = Math.Abs(High[barsAgo] - Close[barsAgo + 1]);
            double lc = Math.Abs(Low[barsAgo] - Close[barsAgo + 1]);
            return Math.Max(hl, Math.Max(hc, lc));
        }

        private void ResetTradeState()
        {
            _tradeEntryPrice = 0;
            _tradeIsLong     = false;
            _tradeMaxFav     = 0;
            _tradeFloor      = 0;
            _beArmed           = false;
            _lastStopSent      = 0;
            _nextStopModifyUtc = DateTime.MinValue;
        }

        // Arms the initial ATR brackets for the entry/reversal about to be submitted: stop =
        // AtrStopMult x ATR (NO tick cap by design — Daily Loss Limit is the USD backstop),
        // target = stop x RewardMultiple. Ticks mode here; the trail later switches the live
        // stop to Price mode. Returns false (skip the signal) while ATR isn't warmed up yet.
        // ONLY the entered side's signal is armed (audit 2026-07-29): SetStopLoss on the HELD
        // side during a reversal re-prices that position's LIVE working stop back to full
        // fresh-ATR width — and if the reversal entry then gets rejected, the position is left
        // wider than its breakeven/trail floor with the tracker desynced.
        private bool ArmBrackets(bool isBuy)
        {
            double atr = CurrentBar >= 1 ? _atrSeries[0] : 0.0;
            if (CurrentBar < AtrPeriod || atr <= 0)
            {
                Print(string.Format("[BigPrints] Signal skipped: ATR not ready (bar {0} < period {1}).", CurrentBar, AtrPeriod));
                return false;
            }

            int stopT = Math.Max(1, (int)Math.Round(AtrStopMult * atr / TickSize));
            int tgtT  = Math.Max(1, (int)Math.Round(stopT * RewardMultiple));
            string sig = isBuy ? "BigPrintLong" : "BigPrintShort";
            SetStopLoss(sig,     CalculationMode.Ticks, stopT, false);
            SetProfitTarget(sig, CalculationMode.Ticks, tgtT);
            _pendingStopTicks = stopT;
            Print(string.Format("[BigPrints] Brackets armed ({0}): stop {1}t (={2:0.0} x ATR), target {3}t (={4:0.0}R)",
                sig, stopT, AtrStopMult, tgtT, RewardMultiple));
            return true;
        }

        // Trail k from the aggression ledger — the "is it really turning?" sensor. Reuses the
        // reversal filter's window and dominance ratio; no new detection machinery.
        private double TrailK(bool isLong)
        {
            DateTime now = _lastTapeTime != DateTime.MinValue ? _lastTapeTime : Time[0];
            long favor   = SumAggression(isLong, now);
            long against = SumAggression(!isLong, now);

            if (favor == 0 && against == 0)
                return AtrStopMult;                       // no recent big prints — neutral
            if (against >= ReversalDominanceRatio * favor)
                return TrailTightMult;                    // opposing flow dominates — hug price
            if (favor >= ReversalDominanceRatio * against)
                return TrailWideMult;                     // favorable flow dominates — breathe
            return AtrStopMult;
        }

        // Breakeven + chandelier trail, every tick while positioned (native mode only). The stop
        // may RETREAT when flow turns favorable again (deliberate "breathing"), but never below
        // the floor: initial stop price at first, raised to breakeven once BE arms — the worst
        // case of the trade never worsens. The fixed profit target coexists: first touch wins.
        private void ManageTradeStops()
        {
            if (IsAtmMode)
                return;

            MarketPosition pos = Position.MarketPosition;
            if (pos == MarketPosition.Flat)
            {
                if (_tradeEntryPrice != 0)
                    ResetTradeState();
                return;
            }

            bool isLong = pos == MarketPosition.Long;

            // Lazy init on the first tick of a new trade; a direction flip (one-order reversal)
            // re-initializes too. _pendingStopTicks was set by ArmBrackets at submission.
            if (_tradeEntryPrice == 0 || isLong != _tradeIsLong)
            {
                _tradeEntryPrice = Position.AveragePrice;
                _tradeIsLong     = isLong;
                _tradeMaxFav     = isLong ? High[0] : Low[0];
                double stopDist  = _pendingStopTicks * TickSize;
                _tradeFloor      = isLong ? _tradeEntryPrice - stopDist : _tradeEntryPrice + stopDist;
                _beArmed         = false;
                // The floor IS the initial bracket's price — seeding the tracker with it makes
                // the dedupe below swallow the trail's first update whenever it clamps to the
                // floor. (Seeding 0 here submitted a same-price no-op modify at the start of
                // every trade — the pointless change that lost the race on 2026-07-29.)
                _lastStopSent    = Instrument.MasterInstrument.RoundToTickSize(_tradeFloor);
            }

            _tradeMaxFav = isLong ? Math.Max(_tradeMaxFav, High[0]) : Math.Min(_tradeMaxFav, Low[0]);

            double bid = _bid, ask = _ask; // written by the tape thread; benign torn-free double reads
            if (bid <= 0 || ask <= 0)
                return;
            // ponytail: fixed 4-tick buffer — raise or parameterize only if a live fast market
            // still generates rejected modifies through it (they are non-fatal now, just noisy).
            const int StopMarketBufferTicks = 4;
            double buf = StopMarketBufferTicks * TickSize;

            // Hands-off zone: the market is within the buffer of the CURRENT working stop —
            // it is about to trigger, and any modification now is pointless and just races
            // the engine. Leave it alone entirely (breakeven floor bookkeeping below included:
            // a floor raised this close to the fill would be modified into the same race).
            if (isLong ? bid - buf <= _lastStopSent : ask + buf >= _lastStopSent)
                return;

            // Breakeven: one-shot floor raise once max favorable excursion hits the trigger.
            if (!_beArmed && BreakevenTriggerTicks > 0)
            {
                double favTicks = (isLong ? _tradeMaxFav - _tradeEntryPrice : _tradeEntryPrice - _tradeMaxFav) / TickSize;
                if (favTicks >= BreakevenTriggerTicks)
                {
                    double be = isLong
                        ? _tradeEntryPrice + BreakevenOffsetTicks * TickSize
                        : _tradeEntryPrice - BreakevenOffsetTicks * TickSize;
                    _tradeFloor = isLong ? Math.Max(_tradeFloor, be) : Math.Min(_tradeFloor, be);
                    _beArmed = true;
                    Print(string.Format("[BigPrints] Breakeven armed: floor -> {0}", _tradeFloor));
                }
            }

            double desired;
            if (TrailingEnabled)
            {
                double k   = TrailK(isLong);
                double atr = _atrSeries[0];
                if (atr <= 0)
                    return;
                desired = isLong ? _tradeMaxFav - k * atr : _tradeMaxFav + k * atr;
                desired = isLong ? Math.Max(desired, _tradeFloor) : Math.Min(desired, _tradeFloor);
            }
            else if (_beArmed)
            {
                desired = _tradeFloor; // BE without trailing: park the stop at the floor
            }
            else
            {
                return; // static ATR stop from entry stands untouched
            }

            // A sell stop must rest below the BID, a buy stop above the ASK — validate against
            // the live inside market, not Close[0] (the last trade can sit on the wrong side
            // of the spread), with the same buffer. If the desired level is too close, leave
            // the working stop alone this tick — next tick retries. A modify can still lose
            // the race against a fast market despite this; that is non-fatal now (see
            // RealtimeErrorHandling in Configure), OnOrderUpdate just resyncs the tracker.
            desired = Instrument.MasterInstrument.RoundToTickSize(desired);
            if (isLong ? desired > bid - buf : desired < ask + buf)
                return;

            if (Math.Abs(desired - _lastStopSent) < TickSize / 2)
                return; // unchanged — don't spam order modifications

            // Rejection damper: after a refused change, hold off further modifies briefly —
            // re-attempting every tick only produces an error-spam loop ("Stop change failed
            // ... stop still working" dozens of times, seen 2026-07-29). A TIME backoff, not a
            // price band: a band permanently froze a fixed breakeven park after one transient
            // rejection (audit 2026-07-29); time always self-heals.
            if (DateTime.UtcNow < _nextStopModifyUtc)
                return;

            _lastStopSent = desired;

            SetStopLoss(isLong ? "BigPrintLong" : "BigPrintShort", CalculationMode.Price, desired, false);
        }

        // NATIVE mode: EnterLong/EnterShort while in the opposite position reverses in one
        // managed-approach order (close + open), per NT8's documented Entry() reversal behavior
        // — no separate Exit() call needed. Reversal branches are gated by the aggression-balance
        // filter (feature 2); the flat-entry branch is gated by the cooldown instead (never both).
        private string TryEnterNative(bool isBuy, long volume, DateTime now)
        {
            MarketPosition pos = Position.MarketPosition;

            int gov = GovernorSize();
            if (gov < 1)
            {
                // Blocked while holding a position: a reversal signal becomes a flatten,
                // never a flip — the governor may only REDUCE exposure.
                if (pos != MarketPosition.Flat && !_orderPending)
                    FlattenNow("BigPrintGovernor");
                else if (pos == MarketPosition.Flat)
                    Print(string.Format("[BigPrints] governor SKIP signal at {0}", Time[0]));
                return "gov_skip";
            }

            if (pos == MarketPosition.Flat)
            {
                if (!PassesCooldown(isBuy, volume, now))
                    return "cooldown"; // consumed — not queued for later
                if (!ArmBrackets(isBuy))
                    return "atr_not_ready"; // ATR not warmed up yet

                MarkOrderPending(isBuy ? "BigPrintLong" : "BigPrintShort"); // BEFORE Enter* — the fill can process first (see _orderPending)
                if (isBuy) EnterLong(gov, "BigPrintLong");
                else       EnterShort(gov, "BigPrintShort");
                Print(string.Format("[BigPrints] Native entry submitted: {0} x{1}", isBuy ? "BUY" : "SELL", gov));
                return "entered";
            }
            if (pos == MarketPosition.Long && !isBuy)
            {
                if (!PassesReversalFilter(true, isBuy, now))
                    return "reversal_filter"; // consumed — position rides, no reversal
                if (!ArmBrackets(false))
                    return "atr_not_ready";

                MarkOrderPending("BigPrintShort");
                EnterShort(gov, "BigPrintShort");
                Print(string.Format("[BigPrints] Native reversal submitted: SELL x{0}", gov));
                return "entered";
            }
            if (pos == MarketPosition.Short && isBuy)
            {
                if (!PassesReversalFilter(false, isBuy, now))
                    return "reversal_filter";
                if (!ArmBrackets(true))
                    return "atr_not_ready";

                MarkOrderPending("BigPrintLong");
                EnterLong(gov, "BigPrintLong");
                Print(string.Format("[BigPrints] Native reversal submitted: BUY x{0}", gov));
                return "entered";
            }
            // Same-direction cluster while already positioned — stay in, do nothing.
            return "same_side";
        }

        // ATM mode: reversal is two async steps (unlike native's single-order reverse) — close
        // the live ATM, then create the opposite one once PollAtmState() confirms it's flat.
        // _atmReverseToBuy carries the queued direction across that gap. Reversal branches are
        // gated by the aggression-balance filter (feature 2); the flat-entry branch is gated by
        // the cooldown instead (never both — a reversal is exempt from cooldown by design).
        private string TryEnterAtm(bool isBuy, long volume, DateTime now)
        {
            MarketPosition atmPos;
            if (string.IsNullOrEmpty(_atmStrategyId))
            {
                atmPos = MarketPosition.Flat;
            }
            else
            {
                MarketPosition? atmPosOpt = SafeGetAtmMarketPosition(_atmStrategyId);
                if (atmPosOpt == null)
                    return "atm_transient"; // transient exception already printed — this signal is dropped, not queued (matches TryEnter's other no-op gates)
                atmPos = atmPosOpt.Value;
            }

            if (atmPos == MarketPosition.Flat)
            {
                if (!PassesCooldown(isBuy, volume, now))
                    return "cooldown"; // consumed — not queued for later

                CreateAtm(isBuy);
                return "entered";
            }
            if (atmPos == MarketPosition.Long && !isBuy)
            {
                if (!PassesReversalFilter(true, isBuy, now))
                    return "reversal_filter"; // consumed — position rides, no reversal

                _atmClosing      = true;
                _atmReverseToBuy = isBuy;
                MarkOrderPending("ATM");
                Print(string.Format("[BigPrints] ATM close submitted: reversal to {0}", isBuy ? "BUY" : "SELL"));
                AtmStrategyClose(_atmStrategyId);
                return "entered";
            }
            if (atmPos == MarketPosition.Short && isBuy)
            {
                if (!PassesReversalFilter(false, isBuy, now))
                    return "reversal_filter";

                _atmClosing      = true;
                _atmReverseToBuy = isBuy;
                MarkOrderPending("ATM");
                Print(string.Format("[BigPrints] ATM close submitted: reversal to {0}", isBuy ? "BUY" : "SELL"));
                AtmStrategyClose(_atmStrategyId);
                return "entered";
            }
            // Same-direction cluster while already positioned — stay in, do nothing.
            return "same_side";
        }

        private void CreateAtm(bool isBuy)
        {
            if (State != State.Realtime)
                return;

            _atmOrderId    = GetAtmStrategyUniqueId();
            _atmStrategyId = GetAtmStrategyUniqueId();
            _atmPending    = true;
            MarkOrderPending("ATM");

            string thisAtmId = _atmStrategyId; // capture — a reversal can overwrite the field before this fires

            Print(string.Format("[BigPrints] ATM create submitted: {0} | Template={1}", isBuy ? "BUY" : "SELL", AtmTemplateName));

            AtmStrategyCreate(
                isBuy ? OrderAction.Buy : OrderAction.SellShort,
                OrderType.Market, 0, 0, TimeInForce.Day,
                _atmOrderId, AtmTemplateName, thisAtmId,
                (errorCode, callbackId) =>
                {
                    if (callbackId != thisAtmId || errorCode == ErrorCode.NoError)
                        return;

                    Print(string.Format("[BigPrintsStrategy] ATM create error: {0}. Resetting.", errorCode));
                    if (_atmStrategyId == thisAtmId)
                    {
                        _atmPending    = false;
                        _atmStrategyId = string.Empty;
                        _atmOrderId    = string.Empty;
                        _orderPending  = false;
                    }
                });
        }

        // Clears _orderPending once the in-flight order reaches a terminal state, so the next
        // cluster signal (or the risk-governor retry) is free to submit again. Position.MarketPosition
        // is stale until this fires, and NT8's built-in duplicate-order guard does not cover
        // market orders — this is the gate that actually prevents double-submission.
        // NATIVE MODE ONLY — ATM-managed orders don't fire this callback (see PollAtmState instead).
        // VERIFIED harmless with native brackets (the ATR stop/target) now in play:
        // this clears on ANY order for this strategy, not just the entry, so a bracket (Stop
        // loss/Profit target) fill re-fires this clear too — but _orderPending is already false
        // by then (cleared back when the ENTRY itself filled), so it's a false->false no-op. A
        // reversal's old bracket is auto-cancelled by NT8 with zero fill (see SetStopLoss docs),
        // which does not raise OnExecutionUpdate at all, so there is no interleaving risk either.
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (IsAtmMode || execution?.Order == null)
                return;

            OrderState state = execution.Order.OrderState;
            if (state == OrderState.Filled || state == OrderState.Cancelled || state == OrderState.Rejected)
            {
                // NAME-GATED clear (audit 2026-07-29): only the tracked submission's own
                // terminal event may reopen the gate. An OLD bracket ("Stop loss"/"Profit
                // target") whose fill races a just-submitted reversal entry must NOT clear it —
                // that reopened the double-submission window the flag exists to close.
                if (execution.Order.Name == _pendingSignal)
                    _orderPending = false;

                // Cooldown clock: this fires on any terminal fill for this strategy — entry OR a
                // bracket exit (stop/target) — so a flat check here correctly captures a bracket
                // close too, using the real execution time (tape time), not Time[0]/DateTime.Now.
                if (Position.MarketPosition == MarketPosition.Flat)
                    _lastFlatTime = time;
            }
        }

        // Rejected/Cancelled orders never reach OnExecutionUpdate (it fires on executions only)
        // — without this, a no-fill terminal entry (margin rejection, manual cancel) leaves
        // _orderPending stuck true and silently locks the strategy out for the session. Gated
        // to OUR submitted orders by signal name: NT8's auto-cancel of a reversal's old bracket
        // also lands here as Cancelled, and clearing on that would reopen the gate while the
        // reversal entry itself is still in flight. NATIVE MODE ONLY (ATM = PollAtmState).
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string comment)
        {
            if (IsAtmMode || order == null)
                return;

            // Self-managed error handling (RealtimeErrorHandling.IgnoreAllErrors — see
            // Configure). "Stop loss"/"Profit target" are NT8's internal names for the
            // SetStopLoss/SetProfitTarget bracket orders.
            if (order.Name == "Stop loss")
            {
                if (orderState == OrderState.Rejected)
                {
                    // Placement rejected -> naked position. OnBarUpdate flattens and retries.
                    _stopOrderFailed = true;
                    Print("[BigPrints] Stop loss order REJECTED — flattening the naked position.");
                }
                else if (error != ErrorCode.NoError)
                {
                    // Failed CHANGE (e.g. the modify lost a race against a fast market): the
                    // order keeps working at its previous, still-valid price — benign. Resync
                    // the trail tracker to reality so later dedupes compare the right level.
                    // Side gate (audit 2026-07-29): a DELAYED rejection belonging to the
                    // previous trade can land after a reversal re-seeded the tracker — its
                    // OrderAction won't match the current position's protective side, and
                    // accepting it would freeze the trail behind the hands-off check.
                    // Back off further modifies for 2s wall regardless of which trade the
                    // rejection belonged to — worst case a new trade's first trail update is
                    // delayed 2s; the resync below stays side-gated.
                    _nextStopModifyUtc = DateTime.UtcNow.AddSeconds(2);
                    bool matchesSide = Position.MarketPosition == MarketPosition.Long
                        ? order.OrderAction == OrderAction.Sell
                        : Position.MarketPosition == MarketPosition.Short && order.OrderAction == OrderAction.BuyToCover;
                    if (matchesSide && stopPrice > 0)
                        _lastStopSent = stopPrice; // resync to the order's real working price
                    Print(string.Format("[BigPrints] Stop change failed ({0}) — stop still working @ {1}.", error, stopPrice));
                }
                return;
            }
            if (order.Name == "Profit target" && orderState == OrderState.Rejected)
            {
                Print("[BigPrints] Profit target order REJECTED — the stop still protects; continuing without a target.");
                return;
            }

            // Same name gate as OnExecutionUpdate: only the tracked submission's own terminal
            // state clears the gate. This inherently excludes NT8's auto-cancel of a reversal's
            // old bracket (Name "Stop loss", never the pending signal).
            if ((orderState == OrderState.Rejected || orderState == OrderState.Cancelled) &&
                order.Name == _pendingSignal)
                _orderPending = false;
        }

        #region Properties
        // GroupNames carry numeric prefixes because the NT8 property grid sorts groups alphabetically.

        // ── 01. Signal ──────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Min Volume", Description = "Minimum total contracts in a cluster to trade it.", Order = 1, GroupName = "01. Signal")]
        public int MinVolume { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Cluster Milliseconds", Description = "Max gap between same-side prints to still count as one sweep.", Order = 2, GroupName = "01. Signal")]
        public int ClusterMilliseconds { get; set; }

        [NinjaScriptProperty]
        [Range(10, 3600)]
        [Display(Name = "Aggression Window (sec)", Description = "Lookback window for the reversal dominance filter and the trailing-stop flow sensor — how far back to sum recent big-print volume by side.", Order = 3, GroupName = "01. Signal")]
        public int AggressionWindowSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Reversal Dominance Ratio", Description = "A reversal only fires if the new side's windowed volume is at least this many times the held side's. The trailing stop reuses this ratio to decide when one side's flow dominates.", Order = 4, GroupName = "01. Signal")]
        public double ReversalDominanceRatio { get; set; }

        // ── 02. Trade Management (native mode only — ATM mode uses the template's SL/TP) ──
        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "ATR Period", Description = "ATR lookback (bars of the chart series) for the dynamic stop and the trailing stop.", Order = 1, GroupName = "02. Trade Management")]
        public int AtrPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "ATR Stop Mult", Description = "Initial stop distance = this x ATR. No tick cap — on a violent day the stop is wide; the Daily Loss Limit is the USD backstop. Also the trail distance when tape flow is neutral.", Order = 2, GroupName = "02. Trade Management")]
        public double AtrStopMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Reward Multiple", Description = "Profit target = effective stop x this (e.g. 1.5 = 1.5R).", Order = 3, GroupName = "02. Trade Management")]
        public double RewardMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2000)]
        [Display(Name = "Breakeven Trigger (ticks)", Description = "Move the stop floor to breakeven once price moves this many ticks in favor. 0 = disabled.", Order = 4, GroupName = "02. Trade Management")]
        public int BreakevenTriggerTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Breakeven Offset (ticks)", Description = "Breakeven stop = entry +/- this many ticks (covers commissions so a scratch isn't a net loss).", Order = 5, GroupName = "02. Trade Management")]
        public int BreakevenOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trailing Enabled", Description = "Chandelier ATR trail modulated by tape flow: distance = k x ATR from the best price since entry, k breathing between Trail Tight/Wide Mult by aggression dominance. Never below the floor (initial stop, raised to BE).", Order = 6, GroupName = "02. Trade Management")]
        public bool TrailingEnabled { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Trail Tight Mult", Description = "Trail k when OPPOSING windowed tape volume dominates (>= Reversal Dominance Ratio x favorable) — the stop hugs price.", Order = 7, GroupName = "02. Trade Management")]
        public double TrailTightMult { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Trail Wide Mult", Description = "Trail k when FAVORABLE windowed tape volume dominates — the stop breathes and lets the trade run.", Order = 8, GroupName = "02. Trade Management")]
        public double TrailWideMult { get; set; }

        [NinjaScriptProperty]
        [Range(0, 240)]
        [Display(Name = "Trade Cooldown (min)", Description = "Minimum rest after a trade closes before a NEW entry from flat is allowed. 0 = disabled. Does NOT block reversals.", Order = 9, GroupName = "02. Trade Management")]
        public int CooldownMinutes { get; set; }

        // ── 03. Money Management ────────────────────────────────────────────
        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Contracts", Description = "Quantity per entry (and per reversal leg). Capped at 20 (fat-finger guard). NATIVE MODE ONLY — in ATM mode, size comes from the ATM template's own Quantity setting.", Order = 1, GroupName = "03. Money Management")]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Daily Profit Target (USD)", Description = "Flatten and lock out entries for the rest of the day once hit (includes open PnL). 0 = disabled.", Order = 2, GroupName = "03. Money Management")]
        public double DailyProfitTargetUSD { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Daily Loss Limit (USD)", Description = "Flatten and lock out entries for the rest of the day once hit (includes open PnL). 0 = disabled.", Order = 3, GroupName = "03. Money Management")]
        public double DailyLossLimitUSD { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Shared Daily PnL (account-wide)", Description = "ON: the daily target/loss watches the ACCOUNT's combined realized+unrealized PnL — the sum across every market/instance of this strategy on the account (any other trading on the account counts too, prop-firm semantics). Each instance flattens its own position and locks out on breach. OFF: each instance watches only its own PnL. Set the same target/loss on every instance.", Order = 4, GroupName = "03. Money Management")]
        public bool UseAccountDailyPnL { get; set; }

        // ── 04. Session ─────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Session Start (HHmm)", Description = "Entries allowed from this time, e.g. 930 for 09:30.", Order = 1, GroupName = "04. Session")]
        public int SessionStart { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Session End (HHmm)", Description = "No new entries at/after this time; flattens any open position, e.g. 1555 for 15:55.", Order = 2, GroupName = "04. Session")]
        public int SessionEnd { get; set; }

        // ── 05. Prop Governor ───────────────────────────────────────────────
        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(Name = "Trailing DD Remaining ($)", Description = "Trailing-drawdown headroom at strategy enable, from the prop dashboard. 0 = governor disabled. Native mode only — no effect when ATM Template Name is set.", Order = 1, GroupName = "05. Prop Governor")]
        public double GovTrailingDDRemaining { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Horizons Per Day", Description = "Expected trades/day for the per-trade risk split: risk = 2 x daily budget / sqrt(horizons).", Order = 2, GroupName = "05. Prop Governor")]
        public int GovHorizonsPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Max Consecutive Losses", Description = "Session halt after this many consecutive losing trades. 0 = disabled.", Order = 3, GroupName = "05. Prop Governor")]
        public int GovMaxConsecLosses { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 10.0)]
        [Display(Name = "Vol Shock Mult", Description = "Half size when 10-bar avg range > mult x 50-bar avg range; zero size beyond 1.5x that.", Order = 4, GroupName = "05. Prop Governor")]
        public double GovVolShockMult { get; set; }

        // ── 06. ATM ─────────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "ATM Template Name", Description = "Empty = native mode (managed EnterLong/EnterShort with ATR brackets). Set to an ATM template name to route entries through that ATM — SL/TP are then managed by the template and ALL of 02. Trade Management is ignored.", Order = 1, GroupName = "06. ATM")]
        public string AtmTemplateName { get; set; }

        // ── 07. Discriminator Entry ─────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Entry Mode", Description = "Immediate = v1: enter with the cluster the moment it fires. Discriminator = wait 5s after the cluster and enter only when >=2 of the pre-registered T1/T2/T3 discriminators agree (reversal -> fade the sweep, continuation -> follow it). Thresholds are frozen constants (audit 2026-07-30), not parameters.", Order = 1, GroupName = "07. Discriminator Entry")]
        public BigPrintsEntryMode EntryMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Discriminator Log", Description = "Log every cluster >= Min Volume with its T1/T2/T3 values, votes, action and eventual outcome label to BigPrintsAI/discriminator_log.jsonl — in BOTH entry modes. This is the validation corpus; leave it on.", Order = 2, GroupName = "07. Discriminator Entry")]
        public bool EnableDiscriminatorLog { get; set; }
        #endregion
    }
}
