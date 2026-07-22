#region Using declarations
using System;
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
// LIVE-MONEY GATE (not fixed in v1 by design) — read before enabling on a funded account:
//   - No per-trade stop loss in NATIVE mode; only the daily USD governor (target/loss) bounds
//     risk there. This is SOLVED in ATM mode — use an ATM template with a real stop.
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
            }
            else if (State == State.Configure)
            {
                // No extra data series — this strategy trades off the Level-I tape (OnMarketData),
                // not off bar closes. The primary Bars series only supplies session/day-boundary
                // context (Time[0], Bars.IsFirstBarOfSession) for the risk/session governor.
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

            MarketPosition atmPos = GetAtmStrategyMarketPosition(_atmStrategyId);

            // Deadlock guard: an entry order that dies AFTER AtmStrategyCreate() succeeded (margin
            // rejection, manual cancel, Day-TIF expiry) never produces a position, so the fill-
            // confirmation branch below would never fire and _orderPending would stay true forever
            // — silently locking the strategy out of the market for the rest of the session.
            // Gated on atmPos still Flat: a partial fill that then gets cancelled DOES have a real
            // position, and that case is correctly handled as a fill below, not a dead order here.
            if (_atmPending && atmPos == MarketPosition.Flat)
            {
                string[] status = GetAtmStrategyEntryOrderStatus(_atmOrderId); // empty until the order registers
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

            TryEnter(_clusterIsBuy, now);
        }

        // Entry / reversal logic — dispatches to the active mode.
        //   1. Flat            -> enter in the aggressor's direction.
        //   2. Opposite cluster while positioned -> reverse.
        //   3. Same-direction cluster while positioned -> no-op, stay in.
        // Native v1 has no per-trade stop/target (user spec: daily limits only, via the risk
        // governor above). To add one later: call SetStopLoss/SetProfitTarget right after each
        // EnterLong/EnterShort call in TryEnterNative, tied to the same signal name. ATM mode
        // gets its stop/target from the template instead (see AtmTemplateName).
        private void TryEnter(bool isBuy, DateTime marketTime)
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
                TryEnterAtm(isBuy);
            else
                TryEnterNative(isBuy);
        }

        // NATIVE mode: EnterLong/EnterShort while in the opposite position reverses in one
        // managed-approach order (close + open), per NT8's documented Entry() reversal behavior
        // — no separate Exit() call needed.
        private void TryEnterNative(bool isBuy)
        {
            MarketPosition pos = Position.MarketPosition;

            if (pos == MarketPosition.Flat)
            {
                if (isBuy) EnterLong(Contracts, "BigPrintLong");
                else       EnterShort(Contracts, "BigPrintShort");
                _orderPending = true;
                Print(string.Format("[BigPrints] Native entry submitted: {0} x{1}", isBuy ? "BUY" : "SELL", Contracts));
            }
            else if (pos == MarketPosition.Long && !isBuy)
            {
                EnterShort(Contracts, "BigPrintShort");
                _orderPending = true;
                Print(string.Format("[BigPrints] Native reversal submitted: SELL x{0}", Contracts));
            }
            else if (pos == MarketPosition.Short && isBuy)
            {
                EnterLong(Contracts, "BigPrintLong");
                _orderPending = true;
                Print(string.Format("[BigPrints] Native reversal submitted: BUY x{0}", Contracts));
            }
            // else: same-direction cluster while already positioned — stay in, do nothing.
        }

        // ATM mode: reversal is two async steps (unlike native's single-order reverse) — close
        // the live ATM, then create the opposite one once PollAtmState() confirms it's flat.
        // _atmReverseToBuy carries the queued direction across that gap.
        private void TryEnterAtm(bool isBuy)
        {
            MarketPosition atmPos = string.IsNullOrEmpty(_atmStrategyId)
                ? MarketPosition.Flat
                : GetAtmStrategyMarketPosition(_atmStrategyId);

            if (atmPos == MarketPosition.Flat)
            {
                CreateAtm(isBuy);
            }
            else if ((atmPos == MarketPosition.Long && !isBuy) || (atmPos == MarketPosition.Short && isBuy))
            {
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
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (IsAtmMode || execution?.Order == null)
                return;

            OrderState state = execution.Order.OrderState;
            if (state == OrderState.Filled || state == OrderState.Cancelled || state == OrderState.Rejected)
                _orderPending = false;
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
        #endregion
    }
}
