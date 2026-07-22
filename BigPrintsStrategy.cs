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

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                          = "BigPrintsStrategy";
                Description                   = "Trades in the direction of large aggressive tape prints (sweeps). Real-time / Market Replay only — cannot be backtested in the Strategy Analyzer.";
                Calculate                     = Calculate.OnEachTick;
                EntriesPerDirection           = 1;
                EntryHandling                 = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy  = false; // the session governor below flattens explicitly instead
                IsFillLimitOnTouch            = false;
                StartBehavior                 = StartBehavior.WaitUntilFlat;
                BarsRequiredToTrade           = 1;

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
            }
        }

        // Risk/session governor — runs every tick (Calculate.OnEachTick fires OnBarUpdate on each
        // tick too), independent of whether a new cluster fired, since unrealized PnL and the
        // session-end boundary both move without a new print.
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 0)
                return;

            if (Bars.IsFirstBarOfSession)
                DailyReset();

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
                return;
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
            if (ToTime(Time[0]) >= SessionEnd * 100 && Position.MarketPosition != MarketPosition.Flat)
                FlattenNow("BigPrintSessionEnd");
        }

        private void FlattenNow(string signalName)
        {
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong(signalName);
            else if (Position.MarketPosition == MarketPosition.Short)
                ExitShort(signalName);
        }

        private bool InSession(DateTime marketTime)
        {
            int t = ToTime(marketTime);
            return t >= SessionStart * 100 && t < SessionEnd * 100;
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
            FinalizeCluster();

            _clusterOpen      = true;
            _clusterIsBuy     = isBuy;
            _clusterVolume    = e.Volume;
            _clusterLastTime  = e.Time;
            _clusterStartTime = e.Time;
        }

        // ponytail: unlike BigPrints.cs, there is no Terminated-time flush here — a strategy has
        // nothing useful to draw at teardown, and firing a trade decision off a mid-shutdown
        // cluster would be actively unwanted. An in-flight cluster at Terminated is simply dropped.
        private void FinalizeCluster()
        {
            if (!_clusterOpen)
                return;

            _clusterOpen = false;

            if (_clusterVolume < MinVolume)
                return;

            TryEnter(_clusterIsBuy, _clusterLastTime);
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
            if (_dailyLockout || !InSession(marketTime))
                return;

            MarketPosition pos = Position.MarketPosition;

            if (pos == MarketPosition.Flat)
            {
                if (isBuy) EnterLong(Contracts, "BigPrintLong");
                else       EnterShort(Contracts, "BigPrintShort");
            }
            else if (pos == MarketPosition.Long && !isBuy)
            {
                EnterShort(Contracts, "BigPrintShort");
            }
            else if (pos == MarketPosition.Short && isBuy)
            {
                EnterLong(Contracts, "BigPrintLong");
            }
            // else: same-direction cluster while already positioned — stay in, do nothing.
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
        [Range(1, int.MaxValue)]
        [Display(Name = "Contracts", Description = "Quantity per entry (and per reversal leg).", Order = 3, GroupName = "Parameters")]
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
