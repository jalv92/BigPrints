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
// mirror the change here too.
//
// REAL-TIME / MARKET REPLAY ONLY. OnMarketData does not fire on historical data, so this
// strategy CANNOT be backtested or optimized in the Strategy Analyzer — there is no historical
// tape to replay the aggressor logic against. It only trades while running live or in Market
// Replay (both report State.Realtime, which the code below relies on).
//
// LIVE-MONEY GATE (not fixed in v1 by design) — read before enabling on a funded account:
//   - No per-trade stop loss; only the daily USD governor (target/loss) bounds risk.
//   - Daily PnL baseline (_dayStartRealized) resets on strategy restart, not only on a new
//     trading day — restarting mid-session re-baselines CumProfit and can hide same-day PnL.
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

        // Set on EVERY Enter/Exit submission, cleared in OnExecutionUpdate once that order
        // reaches a terminal state. Position.MarketPosition is stale until the fill confirms,
        // and NT8's duplicate-order safety net does not cover market orders — without this gate
        // a rapid opposite cluster (or the risk governor's per-tick retry) would double-submit.
        private bool _orderPending;

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

            // Stale-cluster hygiene: if the tape went quiet mid-sweep, discard the open cluster
            // once it's clearly done (wall-clock gap since its last print exceeds the hard span
            // cap) rather than let it linger and merge with an unrelated print far later.
            if (_clusterOpen && (Time[0] - _clusterLastTime).TotalMilliseconds > MaxClusterSpanMs)
                _clusterOpen = false;

            CheckRiskGovernor();
            CheckSessionEnd();
        }

        private void DailyReset()
        {
            // nt8c-safe path (see Apertura4HMSS.cs for the same pattern): documented API for the
            // strategy's own cumulative realized PnL across all trades so far.
            _dayStartRealized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            _dailyLockout     = false;
        }

        private void CheckRiskGovernor()
        {
            if (_dailyLockout)
            {
                // Already locked out — a single ExitLong/ExitShort call isn't guaranteed to fill;
                // keep retrying the flatten every tick until the position is actually flat.
                if (Position.MarketPosition != MarketPosition.Flat && !_orderPending)
                    FlattenNow("BigPrintRiskGovernor");
                return;
            }
            if (DailyProfitTargetUSD <= 0 && DailyLossLimitUSD <= 0)
                return; // both disabled — nothing to compute

            double realized   = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit - _dayStartRealized;
            double unrealized = Position.MarketPosition != MarketPosition.Flat
                ? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency)
                : 0.0;
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
            if (!InSession(Time[0]) && Position.MarketPosition != MarketPosition.Flat)
                FlattenNow("BigPrintSessionEnd");
        }

        private void FlattenNow(string signalName)
        {
            if (_orderPending)
                return; // an Enter/Exit is already in flight — don't stack a second submission

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

            // Cold signal — the cluster's last print is too far behind "now" to still be
            // actionable (tape went quiet, or this finalize was only triggered by a much later
            // unrelated tick). Drop it rather than trade a stale sweep.
            if ((now - _clusterLastTime).TotalMilliseconds > 2000)
                return;

            TryEnter(_clusterIsBuy, now);
        }

        // Entry / reversal logic.
        //   1. Flat            -> enter in the aggressor's direction.
        //   2. Opposite cluster while positioned -> EnterLong/EnterShort while in the opposite
        //      position reverses in one managed-approach order (close + open), per NT8's
        //      documented Entry() reversal behavior — no separate Exit() call needed.
        //   3. Same-direction cluster while positioned -> no-op, stay in.
        // v1 has no per-trade stop/target (user spec: daily limits only, via the risk governor
        // above). To add one later: call SetStopLoss/SetProfitTarget right after each EnterLong/
        // EnterShort call below, tied to the same signal name ("BigPrintLong"/"BigPrintShort").
        private void TryEnter(bool isBuy, DateTime marketTime)
        {
            if (_orderPending || _dailyLockout || !InSession(marketTime))
                return;

            MarketPosition pos = Position.MarketPosition;

            if (pos == MarketPosition.Flat)
            {
                if (isBuy) EnterLong(Contracts, "BigPrintLong");
                else       EnterShort(Contracts, "BigPrintShort");
                _orderPending = true;
            }
            else if (pos == MarketPosition.Long && !isBuy)
            {
                EnterShort(Contracts, "BigPrintShort");
                _orderPending = true;
            }
            else if (pos == MarketPosition.Short && isBuy)
            {
                EnterLong(Contracts, "BigPrintLong");
                _orderPending = true;
            }
            // else: same-direction cluster while already positioned — stay in, do nothing.
        }

        // Clears _orderPending once the in-flight order reaches a terminal state, so the next
        // cluster signal (or the risk-governor retry) is free to submit again. Position.MarketPosition
        // is stale until this fires, and NT8's built-in duplicate-order guard does not cover
        // market orders — this is the gate that actually prevents double-submission.
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution?.Order == null)
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
        [Display(Name = "Contracts", Description = "Quantity per entry (and per reversal leg). Capped at 20 (fat-finger guard).", Order = 3, GroupName = "Parameters")]
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
        #endregion
    }
}
