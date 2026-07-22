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
//   - Empty (default): NATIVE mode — EnterLong/EnterShort managed orders, no per-trade stop.
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
// PLAYBACK ADVISORY (2026-07-21): NT8 8.1.8's OWN ATM-from-NinjaScript order path is a crasher
// in Playback, not something in this file — THREE separate crash dumps (22:57, 23:04, 23:20)
// all die inside NinjaScript.AtmStrategy.SubmitEntryOrders / the Strategy2Order SQLite write,
// the last one AFTER the OnMarketData->OnBarUpdate threading fix above was already in place.
// Vendor bug in NT8 8.1.8's ATM engine under Playback's compressed post-trade timing — no
// further workaround exists on our side. RECOMMENDATION: use NATIVE mode with StopLossTicks /
// ProfitTargetTicks (below) for Playback and testing; reserve ATM mode for live/sim accounts,
// where the timing that triggers the bug does not occur.
//
// LIVE-MONEY GATE (not fixed in v1 by design) — read before enabling on a funded account:
//   - NATIVE mode has no per-trade stop loss unless StopLossTicks/ProfitTargetTicks are set
//     (both 0 = disabled, the original default) — either way, the daily USD governor always
//     applies. ATM mode gets its stop/target from the template instead — but see the PLAYBACK
//     ADVISORY above before using ATM mode in Playback.
//   - Daily PnL baseline (_dayStartRealized / _atmRealizedPnLToday) resets on strategy restart,
//     not only on a new trading day — restarting mid-session re-baselines and can hide same-day PnL.
//   - No account-position adoption (StartBehavior.WaitUntilFlat) — a manually-opened position
//     on this account is invisible to this strategy.
//   - ConnectionLossHandling left at its NT8 default — no custom reconnect/flatten policy.
namespace NinjaTrader.NinjaScript.Strategies
{
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

        // Set on EVERY Enter/Exit (native) or AtmStrategyCreate/Close (ATM) submission, cleared
        // once that submission's outcome is confirmed — OnExecutionUpdate in native mode,
        // PollAtmState() in ATM mode (OnExecutionUpdate does NOT fire for ATM-managed orders).
        // Position.MarketPosition is stale until the fill confirms, and NT8's duplicate-order
        // safety net does not cover market orders — without this gate a rapid opposite cluster
        // (or the risk governor's per-tick retry) would double-submit.
        private bool _orderPending;

        // --- ATM state (Hybrid mode — pattern mirrored from TBStrategy.cs) ---
        private string _atmStrategyId  = string.Empty;
        private string _atmOrderId     = string.Empty;
        private bool   _atmPending;         // AtmStrategyCreate submitted, entry fill not yet confirmed
        private bool   _atmPositionOpen;    // ATM reports a live (non-flat) market position
        private bool   _atmClosing;         // AtmStrategyClose submitted, flat not yet confirmed
        private bool?  _atmReverseToBuy;    // set only when the close is to make room for a reversal
        private double _atmRealizedPnLToday; // SystemPerformance does not track ATM trades — summed by hand

        private bool IsAtmMode => !string.IsNullOrEmpty(AtmTemplateName);

        // One-slot signal queue — see THREADING note in the header. OnMarketData (market-data
        // thread) writes; OnBarUpdate (strategy thread) reads and clears. Overwrite semantics:
        // a newer cluster replaces an undrained older one, matching "always follow the latest
        // big entry" — there is never more than 1 tick between write and drain anyway.
        private bool     _signalQueued;
        private bool     _signalIsBuy;
        private long     _signalVolume;
        private DateTime _signalTime;

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
                StopLossTicks        = 0;     // 0 = disabled
                ProfitTargetTicks    = 0;     // 0 = disabled
                AggressionWindowSeconds = 180;
                ReversalDominanceRatio  = 1.5;
                CooldownMinutes         = 5;
            }
            else if (State == State.Configure)
            {
                // No extra data series — this strategy trades off the Level-I tape (OnMarketData),
                // not off bar closes. The primary Bars series only supplies session/day-boundary
                // context (Time[0], Bars.IsFirstBarOfSession) for the risk/session governor.

                // Native-mode per-trade brackets (see PLAYBACK ADVISORY in the header). Must be
                // set here, before any entry is submitted, and tied to each entry's own signal
                // name so the managed engine auto-attaches/replaces the bracket per entry/
                // reversal. Naturally a no-op in ATM mode too: "BigPrintLong"/"BigPrintShort" are
                // never used as entry signal names there (AtmStrategyCreate is used instead), so
                // these brackets simply never have a matching entry to attach to.
                if (StopLossTicks > 0)
                {
                    SetStopLoss("BigPrintLong",  CalculationMode.Ticks, StopLossTicks, false);
                    SetStopLoss("BigPrintShort", CalculationMode.Ticks, StopLossTicks, false);
                }
                if (ProfitTargetTicks > 0)
                {
                    SetProfitTarget("BigPrintLong",  CalculationMode.Ticks, ProfitTargetTicks);
                    SetProfitTarget("BigPrintShort", CalculationMode.Ticks, ProfitTargetTicks);
                }
            }
            else if (State == State.DataLoaded)
            {
                _bid = 0;
                _ask = 0;
                _clusterOpen      = false;
                _dayStartRealized = 0;
                _dailyLockout     = false;
                _lastResetBar     = -1;
                _orderPending     = false;
                _signalQueued     = false;
                _signalIsBuy      = false;
                _signalVolume     = 0;
                _signalTime       = default(DateTime);
                _aggressionLedger.Clear();
                _lastTapeTime     = DateTime.MinValue;
                _lastFlatTime     = DateTime.MinValue; // no trade yet -> no cooldown

                _atmStrategyId       = string.Empty;
                _atmOrderId          = string.Empty;
                _atmPending          = false;
                _atmPositionOpen     = false;
                _atmClosing          = false;
                _atmReverseToBuy     = null;
                _atmRealizedPnLToday = 0;
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

            PollAtmState();
            CheckRiskGovernor();
            CheckSessionEnd();

            // Drain the signal queue LAST, after the governor/session checks above have had a
            // chance to update _dailyLockout/_orderPending on fresh state for this tick.
            if (_signalQueued)
            {
                _signalQueued = false;
                // Ledger BEFORE the gates in TryEnter — a signal that gets skipped (pending/
                // lockout/session) is still real tape flow for the reversal dominance filter.
                RecordAggression(_signalIsBuy, _signalVolume, _signalTime);
                TryEnter(_signalIsBuy, _signalVolume, _signalTime);
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
            double dayPnL = realized + unrealized;

            bool hitTarget = DailyProfitTargetUSD > 0 && dayPnL >= DailyProfitTargetUSD;
            bool hitLoss   = DailyLossLimitUSD > 0 && dayPnL <= -DailyLossLimitUSD;

            if (!hitTarget && !hitLoss)
                return;

            _dailyLockout = true;
            FlattenNow("BigPrintRiskGovernor");
            Print(string.Format("[BigPrintsStrategy] Daily {0} hit ({1:F2} USD) — locked out until next session.",
                hitTarget ? "profit target" : "loss limit", dayPnL));
        }

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
                    _orderPending    = true;
                    Print(string.Format("[BigPrints] ATM close submitted: flatten ({0})", signalName));
                    AtmStrategyClose(_atmStrategyId);
                }
                return;
            }

            if (Position.MarketPosition == MarketPosition.Long)
            {
                ExitLong(signalName);
                _orderPending = true;
            }
            else if (Position.MarketPosition == MarketPosition.Short)
            {
                ExitShort(signalName);
                _orderPending = true;
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

            if (_clusterOpen &&
                isBuy == _clusterIsBuy &&
                (e.Time - _clusterLastTime).TotalMilliseconds <= ClusterMilliseconds &&
                (e.Time - _clusterStartTime).TotalMilliseconds <= MaxClusterSpanMs)
            {
                // Same side, within the sweep window and the hard wall-clock cap — fold into the cluster.
                _clusterVolume  += e.Volume;
                _clusterLastTime = e.Time;
                return;
            }

            // Opposite side, gap exceeded, or max span exceeded — finalize the open cluster, start a new one.
            FinalizeCluster(e.Time);

            _clusterOpen      = true;
            _clusterIsBuy     = isBuy;
            _clusterVolume    = e.Volume;
            _clusterLastTime  = e.Time;
            _clusterStartTime = e.Time;
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
            _signalQueued = true;
            _signalIsBuy  = _clusterIsBuy;
            _signalVolume = _clusterVolume;
            _signalTime   = now;
        }

        // Entry / reversal logic — dispatches to the active mode.
        //   1. Flat            -> enter in the aggressor's direction.
        //   2. Opposite cluster while positioned -> reverse.
        //   3. Same-direction cluster while positioned -> no-op, stay in.
        // Native v1 has no per-trade stop/target (user spec: daily limits only, via the risk
        // governor above). To add one later: call SetStopLoss/SetProfitTarget right after each
        // EnterLong/EnterShort call in TryEnterNative, tied to the same signal name. ATM mode
        // gets its stop/target from the template instead (see AtmTemplateName).
        private void TryEnter(bool isBuy, long volume, DateTime marketTime)
        {
            if (_orderPending)
            {
                Print("[BigPrints] Cluster not traded: an order/ATM operation is already pending.");
                return;
            }
            if (_dailyLockout)
            {
                Print("[BigPrints] Cluster not traded: daily lockout active.");
                return;
            }
            if (!InSession(marketTime))
            {
                Print("[BigPrints] Cluster not traded: outside the session window.");
                return;
            }

            if (IsAtmMode)
                TryEnterAtm(isBuy, volume, marketTime);
            else
                TryEnterNative(isBuy, volume, marketTime);
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

        // NATIVE mode: EnterLong/EnterShort while in the opposite position reverses in one
        // managed-approach order (close + open), per NT8's documented Entry() reversal behavior
        // — no separate Exit() call needed. Reversal branches are gated by the aggression-balance
        // filter (feature 2); the flat-entry branch is gated by the cooldown instead (never both).
        private void TryEnterNative(bool isBuy, long volume, DateTime now)
        {
            MarketPosition pos = Position.MarketPosition;

            if (pos == MarketPosition.Flat)
            {
                if (!PassesCooldown(isBuy, volume, now))
                    return; // consumed — not queued for later

                if (isBuy) EnterLong(Contracts, "BigPrintLong");
                else       EnterShort(Contracts, "BigPrintShort");
                _orderPending = true;
                Print(string.Format("[BigPrints] Native entry submitted: {0} x{1}", isBuy ? "BUY" : "SELL", Contracts));
            }
            else if (pos == MarketPosition.Long && !isBuy)
            {
                if (!PassesReversalFilter(true, isBuy, now))
                    return; // consumed — position rides, no reversal

                EnterShort(Contracts, "BigPrintShort");
                _orderPending = true;
                Print(string.Format("[BigPrints] Native reversal submitted: SELL x{0}", Contracts));
            }
            else if (pos == MarketPosition.Short && isBuy)
            {
                if (!PassesReversalFilter(false, isBuy, now))
                    return;

                EnterLong(Contracts, "BigPrintLong");
                _orderPending = true;
                Print(string.Format("[BigPrints] Native reversal submitted: BUY x{0}", Contracts));
            }
            // else: same-direction cluster while already positioned — stay in, do nothing.
        }

        // ATM mode: reversal is two async steps (unlike native's single-order reverse) — close
        // the live ATM, then create the opposite one once PollAtmState() confirms it's flat.
        // _atmReverseToBuy carries the queued direction across that gap. Reversal branches are
        // gated by the aggression-balance filter (feature 2); the flat-entry branch is gated by
        // the cooldown instead (never both — a reversal is exempt from cooldown by design).
        private void TryEnterAtm(bool isBuy, long volume, DateTime now)
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
                    return; // transient exception already printed — this signal is dropped, not queued (matches TryEnter's other no-op gates)
                atmPos = atmPosOpt.Value;
            }

            if (atmPos == MarketPosition.Flat)
            {
                if (!PassesCooldown(isBuy, volume, now))
                    return; // consumed — not queued for later

                CreateAtm(isBuy);
            }
            else if (atmPos == MarketPosition.Long && !isBuy)
            {
                if (!PassesReversalFilter(true, isBuy, now))
                    return; // consumed — position rides, no reversal

                _atmClosing      = true;
                _atmReverseToBuy = isBuy;
                _orderPending    = true;
                Print(string.Format("[BigPrints] ATM close submitted: reversal to {0}", isBuy ? "BUY" : "SELL"));
                AtmStrategyClose(_atmStrategyId);
            }
            else if (atmPos == MarketPosition.Short && isBuy)
            {
                if (!PassesReversalFilter(false, isBuy, now))
                    return;

                _atmClosing      = true;
                _atmReverseToBuy = isBuy;
                _orderPending    = true;
                Print(string.Format("[BigPrints] ATM close submitted: reversal to {0}", isBuy ? "BUY" : "SELL"));
                AtmStrategyClose(_atmStrategyId);
            }
            // else: same-direction cluster while already positioned — stay in, do nothing.
        }

        private void CreateAtm(bool isBuy)
        {
            if (State != State.Realtime)
                return;

            _atmOrderId    = GetAtmStrategyUniqueId();
            _atmStrategyId = GetAtmStrategyUniqueId();
            _atmPending    = true;
            _orderPending  = true;

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
        // VERIFIED harmless with native brackets (StopLossTicks/ProfitTargetTicks) now in play:
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
                _orderPending = false;

                // Cooldown clock: this fires on any terminal fill for this strategy — entry OR a
                // bracket exit (stop/target) — so a flat check here correctly captures a bracket
                // close too, using the real execution time (tape time), not Time[0]/DateTime.Now.
                if (Position.MarketPosition == MarketPosition.Flat)
                    _lastFlatTime = time;
            }
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Min Volume", Description = "Minimum total contracts in a cluster to trade it.", Order = 1, GroupName = "Parameters")]
        public int MinVolume { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Cluster Milliseconds", Description = "Max gap between same-side prints to still count as one sweep.", Order = 2, GroupName = "Parameters")]
        public int ClusterMilliseconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Contracts", Description = "Quantity per entry (and per reversal leg). Capped at 20 (fat-finger guard). NATIVE MODE ONLY — in ATM mode, size comes from the ATM template's own Quantity setting.", Order = 3, GroupName = "Parameters")]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Session Start (HHmm)", Description = "Entries allowed from this time, e.g. 930 for 09:30.", Order = 4, GroupName = "Parameters")]
        public int SessionStart { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Session End (HHmm)", Description = "No new entries at/after this time; flattens any open position, e.g. 1555 for 15:55.", Order = 5, GroupName = "Parameters")]
        public int SessionEnd { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Daily Profit Target (USD)", Description = "Flatten and lock out entries for the rest of the day once hit. 0 = disabled.", Order = 6, GroupName = "Parameters")]
        public double DailyProfitTargetUSD { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Daily Loss Limit (USD)", Description = "Flatten and lock out entries for the rest of the day once hit. 0 = disabled.", Order = 7, GroupName = "Parameters")]
        public double DailyLossLimitUSD { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATM Template Name", Description = "Empty = native mode (managed EnterLong/EnterShort, no per-trade stop). Set to an ATM template name to route entries through that ATM — SL/TP are then managed by the template.", Order = 8, GroupName = "Parameters")]
        public string AtmTemplateName { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2000)]
        [Display(Name = "Stop Loss (ticks)", Description = "NATIVE mode only, ignored in ATM mode. 0 = disabled.", Order = 9, GroupName = "Parameters")]
        public int StopLossTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2000)]
        [Display(Name = "Profit Target (ticks)", Description = "NATIVE mode only, ignored in ATM mode. 0 = disabled.", Order = 10, GroupName = "Parameters")]
        public int ProfitTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(10, 3600)]
        [Display(Name = "Aggression Window (sec)", Description = "Lookback window for the reversal dominance filter — how far back to sum recent big-print volume by side.", Order = 11, GroupName = "Parameters")]
        public int AggressionWindowSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Reversal Dominance Ratio", Description = "A reversal only fires if the new side's windowed volume is at least this many times the held side's — filters a lone counter-signal out of a two-sided battle.", Order = 12, GroupName = "Parameters")]
        public double ReversalDominanceRatio { get; set; }

        [NinjaScriptProperty]
        [Range(0, 240)]
        [Display(Name = "Trade Cooldown (min)", Description = "Minimum rest after a trade closes before a NEW entry from flat is allowed. 0 = disabled. Does NOT block reversals.", Order = 13, GroupName = "Parameters")]
        public int CooldownMinutes { get; set; }
        #endregion
    }
}
