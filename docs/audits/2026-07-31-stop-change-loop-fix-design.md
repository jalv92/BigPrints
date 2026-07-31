## FINAL DESIGN — BigPrints stop-change serialization

**Verified artifacts** (patched copy compiles clean, byte-identical to what is described below):
- Patched file: `/tmp/claude-1000/-home-javlo-Code-Projects-main-project/cbf37f3f-340e-4533-924e-eb9fa665481a/scratchpad/bp/BigPrintsStrategy.cs`
- Unified diff: `/tmp/claude-1000/-home-javlo-Code-Projects-main-project/cbf37f3f-340e-4533-924e-eb9fa665481a/scratchpad/bp/final.diff` (5 hunks, 206 lines)
- Compile: `nt8c build --custom-dir <scratch>/bp/Custom --no-emit` → `Compiled 4 files… OK (no-emit) — 0 warnings`, identical to the unpatched baseline run.

---

## 1. Root cause (revised)

**The loop, proven from the code.** `ManageTradeStops()` has no concept of a change being in flight. It gates the resubmission on a price tracker (`BigPrintsStrategy.cs:1264`) and a 2s wall-clock damper (`:1272`), then calls `SetStopLoss(...)` (`:1277`). The failure handler then *erases the only memory the loop has*: `_lastStopSent = stopPrice` (`:1507`) snaps the tracker back to the order's real, unchanged working price, so 2s later the trail recomputes the same `desired`, clears the dedupe at `:1264` again, and reproduces the identical refusal. That is exactly the observed signature: 18 lines, `stopPrice` from the event (the order's real price, `nt8-strategy/reference/onorderupdate.md:54-56`), bit-identical at 28476.5 — no change ever landed, and the strategy survived (`RealtimeErrorHandling.IgnoreAllErrors`, `:318`), so this is not the 2026-07-29 self-termination.

**Why the change is refused — leading hypothesis, not proof.** `OrderState.ChangePending → ChangeSubmitted` (`onorderupdate.md:127-134`) is a real non-terminal window during which a second change is refused with `ErrorCode.UnableToChangeOrder` (`onorderupdate.md:85`). `Calculate.OnEachTick` (`:261`) under accelerated Playback fires `OnBarUpdate` far faster than a change round trip, so the trail can collide with itself. **The refuter's correction is accepted:** this is Market Replay, so the "round trip" is against NT8's *own simulated order pipeline*, not a broker — the mechanism could equally be (a) a stop held in `Accepted` rather than `Working`, which NT8 documents happens for simulated/held stops (`order.md:244`), or (b) an engine-side per-order mutation lock/cooldown that the in-flight gate cannot cure. I therefore **do not size any constant on a "broker latency" story**, and the fix instruments the answer: the first-failure line now carries `orderState` **and** the submit→event latency in ms. Sub-millisecond latency with state `Working`/`Accepted` falsifies the contention hypothesis in one run.

**Secondary root cause (this one is certain and is what actually hurts):** the failure path is unbounded — no counter, no growth, one `Print` per attempt (`:1508`). Whatever NT8's reason is, a refusal must cost one attempt per capped interval and one log line, not one per damper interval forever.

---

## 2. Adjudication of the adversarial review

| # | Finding | Verdict |
|---|---|---|
| 1 | New fields never reset on a one-order reversal (`ResetTradeState()` only at `:343` and `:1180`; the flip re-inits at `:1188-1201`) | **Confirmed, fixed** — but not by duplicating fields into the flip block. `ResetTradeState()` is now *called* as the first statement of the flip block, so this class of bug cannot recur: one reset function, both paths. It also fixes the pre-existing leak of `_nextStopModifyUtc` across a reversal. |
| 2 | Watchdog unreachable behind the flat/hands-off/ATR/too-close/dedupe returns (`:1177,:1207,:1218,:1241,:1262,:1264`) | **Confirmed, fixed** — hoisted above every gate, and it **does not `return`**, so `_tradeMaxFav`/breakeven bookkeeping keeps running while a change is in flight (the draft's `return` would have dropped high-water updates for up to 5s of wall time). Only the *submit* is gated. |
| 3 | Give-up desyncs the breakeven floor from the live order | **Resolved by deletion.** `MaxStopChangeFails` is cut. Its only real job was log-spam control, which "print the first failure only" already does. It bought a permanent per-trade suspended state, an unbounded BE desync, and extra reset plumbing — three problems to solve a solved one. Max desync is now bounded by the backoff cap (8s), which is unavoidable for any damper. |
| 4 | "Broker round-trip" framing wrong for Playback; constants possibly 10-1000x oversized | **Accepted.** Backoff cap dropped 30s → 8s (2, 4, 8, 8…), and the failure line logs the submit→event latency so the next run sizes it from data instead of narrative. |
| 5 | `OrderState.Rejected` on a *change* would force a flatten of a protected position | **Behaviour deliberately unchanged, diagnostic added.** Splitting on `_stopChangePending` is unsafe: the trail can submit its first Price-mode change on the same tick the entry fills, i.e. while NT8 is still placing the initial bracket stop — a genuine placement rejection could then arrive with the flag true and the naked position would go undetected. Missing a naked position is strictly worse than a redundant flatten. The REJECTED line now prints `changeInFlight={true/false}`; if that ever prints `True`, the branch needs splitting and we'll have the evidence. |

---

## 3. The C# to apply — verbatim

File: `/home/javlo/Code Projects/main-project/projects/Trading/BigPrints/BigPrintsStrategy.cs`. Six edits. Line numbers are the current `main` file.

### Edit 1 — new fields, insert immediately **after** line 253 (`private bool _stopOrderFailed; …`)

```csharp

        // A stop CHANGE is in flight. NT8 refuses a second change while the previous one is still
        // travelling (OrderState.ChangePending -> ChangeSubmitted) and reports
        // ErrorCode.UnableToChangeOrder; the time-only damper above cannot see that, so under
        // accelerated Playback (Calculate.OnEachTick) the trail raced itself and EVERY change of a
        // whole trade was refused - Playback 2026-07-30: 18x "Stop change failed
        // (UnableToChangeOrder) - stop still working @ 28476.5", the same price every time, i.e.
        // the stop was frozen for the entire trade and BE/trail did nothing. ALWAYS set the flag
        // BEFORE the SetStopLoss it guards, never after: under Playback the order event can fire
        // re-entrantly INSIDE the order method, the same ordering rule as _orderPending above.
        private volatile bool _stopChangePending;
        private DateTime      _stopChangeSentUtc; // WALL clock - same documented exception as _orderPendingSinceUtc: it measures order-event latency (a system property), not market time
        private volatile int  _stopChangeFails;   // consecutive refused/lost changes; back to 0 on any confirmed change. Drives the backoff and keeps a streak to ONE log line
        private const double  StopChangeWatchdogSec   = 5.0; // no order event at all in this long -> presume the change lost; the trail must never wait forever on an event that will not come
        // ponytail: 2,4,8,8,... seconds. Tuning knob, not a law - the first-failure line now logs
        // the submit->event latency, so size this from real numbers if a refusal ever persists.
        private const double  StopChangeBackoffCapSec = 8.0;
```

### Edit 2 — `ResetTradeState()` (`:1117-1120`): replace those four lines with

```csharp
            _beArmed           = false;
            _lastStopSent      = 0;
            _nextStopModifyUtc = DateTime.MinValue;
            // No stop change can outlive the position it protected. Called from DataLoaded, from
            // the flat branch of ManageTradeStops AND from its direction-flip branch: a one-order
            // reversal never passes through flat (see TryEnterNative), so the flip is the ONLY
            // reset a reversing trade ever gets.
            _stopChangePending = false;
            _stopChangeSentUtc = DateTime.MinValue;
            _stopChangeFails   = 0;
        }

        // Records a refused or lost stop change: grows the wall-clock backoff (2, 4, 8s cap) and
        // logs ONLY the first failure of a streak - 18 identical lines is not information. Wall
        // clock per this file's rule: it measures order round-trip latency, a system property.
        // Called from OnOrderUpdate (order thread) and from the watchdog in ManageTradeStops
        // (strategy thread); the increment is not atomic, and losing that race only delays the
        // next backoff step by one notch - the following failure re-counts.
        private void NoteStopChangeFailure(string reason, double workingPrice, double latencyMs)
        {
            int fails          = _stopChangeFails + 1;
            _stopChangeFails   = fails;
            _nextStopModifyUtc = DateTime.UtcNow.AddSeconds(Math.Min(StopChangeBackoffCapSec, Math.Pow(2, Math.Min(fails, 3))));
            if (fails == 1)
                Print(string.Format("[BigPrints] Stop change failed ({0}) - stop still working @ {1}; latency {2:0}ms, backing off (repeats stay silent until one succeeds).",
                    reason, workingPrice, latencyMs));
        }
```

### Edit 3 — `ManageTradeStops()`: insert **immediately before** line 1184 (`bool isLong = pos == MarketPosition.Long;`)

```csharp
            // In-flight watchdog, deliberately ABOVE every other gate in this method: if the
            // confirming order event never arrives, the escape must NOT depend on price moving far
            // enough to clear the dedupe further down (a gate that only opens when the market
            // cooperates is exactly how the 2026-07-29 price-band damper froze the trail). No
            // return here on purpose - the max-favorable/breakeven bookkeeping below must keep
            // running while a change is in flight; only the SUBMIT is gated, at the end.
            if (_stopChangePending && (DateTime.UtcNow - _stopChangeSentUtc).TotalSeconds >= StopChangeWatchdogSec)
            {
                _stopChangePending = false;
                NoteStopChangeFailure(string.Format("no order event in {0}s, change presumed lost", StopChangeWatchdogSec),
                    _lastStopSent, StopChangeWatchdogSec * 1000.0);
            }

```

### Edit 4 — flip branch: insert as the **first statement** inside `if (_tradeEntryPrice == 0 || isLong != _tradeIsLong) {` (`:1188`), i.e. before `_tradeEntryPrice = Position.AveragePrice;`

```csharp
                // Reset FIRST so every per-trade field dies with its trade - including the stop-
                // change gate, backoff and streak. A one-order reversal never hits the flat branch
                // above, so without this the new trade inherits the dead trade's damper.
                ResetTradeState();
```

### Edit 5 — replace lines 1267-1277 (the damper comment through the `SetStopLoss` call) with

```csharp
            // ONE change in flight at a time, plus a time backoff after a refusal. NT8 refuses a
            // change while the previous one is still ChangePending/ChangeSubmitted (->
            // ErrorCode.UnableToChangeOrder), and this strategy is Calculate.OnEachTick: under
            // accelerated Playback OnBarUpdate fires far faster than a change round trip, so the
            // time damper alone let the trail race itself into a refusal on every attempt while
            // the resync in OnOrderUpdate handed it the same target back - 18 refusals, stop
            // frozen for the whole trade (Playback 2026-07-30). The damper stays a TIME backoff,
            // never a price band: a band permanently froze a fixed breakeven park after one
            // transient rejection (audit 2026-07-29); time always self-heals. It is capped, so a
            // persistent refusal costs at most one attempt per StopChangeBackoffCapSec and
            // exactly one log line - no give-up state that could outlive the problem.
            if (_stopChangePending || DateTime.UtcNow < _nextStopModifyUtc)
                return;

            // Tracker and flag BEFORE the submit - the order event can process first, re-entrantly
            // inside SetStopLoss (see the ordering note on _orderPending). NOTHING below the call.
            _lastStopSent      = desired;
            _stopChangeSentUtc = DateTime.UtcNow;
            _stopChangePending = true;

            SetStopLoss(isLong ? "BigPrintLong" : "BigPrintShort", CalculationMode.Price, desired, false);
```

### Edit 6 — `OnOrderUpdate()`: replace the whole `if (order.Name == "Stop loss") { … }` block (lines 1482-1511) with

```csharp
            if (order.Name == "Stop loss")
            {
                if (orderState == OrderState.Rejected)
                {
                    // Placement rejected -> naked position. OnBarUpdate flattens and retries.
                    // A refused CHANGE is not expected here (Playback reports it as error !=
                    // NoError on a NON-terminal state, handled below), but that is an assumption,
                    // not a documented guarantee - so log whether a change was in flight. If
                    // changeInFlight=True ever appears, this branch is flattening a still-
                    // protected position and needs splitting. Erring toward the flatten is
                    // deliberate: an unprotected position is worse than a redundant flatten.
                    bool wasChanging   = _stopChangePending;
                    _stopChangePending = false;
                    _stopOrderFailed   = true;
                    Print(string.Format("[BigPrints] Stop loss order REJECTED (changeInFlight={0}) - flattening the naked position.", wasChanging));
                    return;
                }

                // Still travelling - not an outcome yet. Keep the gate closed so ManageTradeStops
                // cannot fire a second change into it.
                if (orderState == OrderState.ChangePending || orderState == OrderState.ChangeSubmitted)
                    return;

                // Any other state resolves whatever change was in flight. Open the gate FIRST and
                // unconditionally: a stale or duplicate event may only ever UNSTICK it, never
                // leave it stuck (the watchdog in ManageTradeStops is the last resort).
                double latencyMs   = _stopChangeSentUtc == DateTime.MinValue
                    ? -1.0 : (DateTime.UtcNow - _stopChangeSentUtc).TotalMilliseconds;
                _stopChangePending = false;

                // Side gate (audit 2026-07-29): an event belonging to the PREVIOUS trade can land
                // after a reversal re-seeded the tracker - its OrderAction won't match the current
                // position's protective side, and accepting its price would freeze the trail
                // behind the hands-off check.
                bool matchesSide = Position.MarketPosition == MarketPosition.Long
                    ? order.OrderAction == OrderAction.Sell
                    : Position.MarketPosition == MarketPosition.Short && order.OrderAction == OrderAction.BuyToCover;

                if (error != ErrorCode.NoError)
                {
                    // Failed CHANGE (refused, or it lost the race against a fast market): the
                    // order keeps working at its previous, still-valid price - benign. Resync the
                    // tracker to reality so later dedupes compare the right level, then back off
                    // instead of retrying blind. The reason carries orderState + the submit->event
                    // latency: that is what identifies WHICH refusal this is, next run.
                    if (matchesSide && stopPrice > 0)
                        _lastStopSent = stopPrice; // resync to the order's real working price
                    NoteStopChangeFailure(string.Format("{0}/{1}{2}", error, orderState, matchesSide ? "" : "/OTHER-SIDE"),
                        stopPrice, latencyMs);
                    return;
                }

                // Confirmed change (or the initial placement): reality wins and the failure streak
                // dies, so one transient refusal can never cost more than one delayed update. This
                // is also the first time this strategy CONFIRMS a stop change instead of assuming
                // it landed. The pending backoff is deliberately NOT cancelled here: it expires on
                // its own within StopChangeBackoffCapSec, and cancelling it would let a stray
                // NoError event on the same order re-open the per-tick retry loop this fix exists
                // to kill.
                if (matchesSide && stopPrice > 0)
                {
                    if (_stopChangeFails > 0)
                        Print(string.Format("[BigPrints] Stop change recovered after {0} failure(s) - working @ {1}.", _stopChangeFails, stopPrice));
                    _stopChangeFails = 0;
                    _lastStopSent    = stopPrice;
                }
                return;
            }
```

---

## 4. Safety argument — every stuck-state scenario

**Lost / never-delivered order event.** `_stopChangePending` is released by the watchdog at the *top* of `ManageTradeStops` (Edit 3), above the flat, hands-off (`:1217`), ATR-ready (`:1240`), too-close (`:1261`) and dedupe (`:1264`) gates. It fires on any tick with an open position, regardless of where price is — the promise is unconditional, which is precisely what the refuter showed the draft's placement was not. It does not `return`, so MFE/breakeven bookkeeping is unaffected by a change in flight. Ceiling: 5s of no trail submissions, then one silent retry.

**Reversal (one-order flip, no flat).** `ResetTradeState()` is now called from the flip branch itself (Edit 4), so the gate, the backoff and the streak die with the trade that owned them. `TryEnterNative`'s documented one-order reversal (`:1280-1283`) is fully covered; no field can be added to `ResetTradeState()` in future and silently miss the flip path. A *late* failure event from the dead trade landing after the flip is caught by the side gate (`OrderAction` mismatch → `/OTHER-SIDE` in the log, `_lastStopSent` untouched); it costs the new trade one 2s backoff, which expires on its own.

**Partial fill.** Stop-order `PartFilled` carries `error == NoError` and a non-`Change*` state → it lands in the confirm branch, opens the gate, and resyncs `_lastStopSent` to the still-working price of the remaining quantity. `SetStopLoss` continues to manage the reduced position per the managed approach (`setstoploss.md:14`). No stuck state. An entry partial fill is unchanged from today (NT8 generates the stop on incoming entry executions).

**Restart mid-trade.** `State.DataLoaded` calls `ResetTradeState()` (`:343`) → all three new fields start `false`/`MinValue`/`0`; `StartBehavior.WaitUntilFlat` (`:266`) prevents adoption of a live position. Unchanged pre-existing caveat: a manually-held position is invisible (`:82-83`).

**Playback rewind.** Requires a disable/reconnect cycle → `DataLoaded` again → same clean start, including the shared account governor wipe already at `:360-364`.

**Threading.** `_stopChangePending` (`volatile bool`) and `_stopChangeFails` (`volatile int`) are written from both the order thread and the strategy thread — atomic reads/writes, same pattern the file already trusts for `_orderPending` (`:156`). The only non-atomic op is `_stopChangeFails + 1` in `NoteStopChangeFailure`; a lost increment can only *under*count, which delays the backoff by one notch — self-corrects on the next failure. `_stopChangeSentUtc` is written on the strategy thread and read on the order thread **for logging only**; a torn 8-byte read (impossible on the 64-bit CLR NT8 runs) would garble one number in one log line and nothing else. No `Order` object is stored across threads. Flag and tracker are written *before* `SetStopLoss` with nothing after it, because Playback fires order state events inside the order method (`onorderupdate.md:26`, `:14`) — the same lesson as `:151-155`.

**Naked-position protection.** `OrderState.Rejected → _stopOrderFailed = true` is behaviourally untouched; `OnBarUpdate:448-454` still flattens and retries until flat. Only a `_stopChangePending = false` and a diagnostic field were added.

**Breakeven floor honesty.** While a backoff is active (≤8s) `_tradeFloor` can be ahead of the live order — the `:1169` comment's "the worst case of the trade never worsens" is true of the *floor variable*, not of the broker-side stop, during that window. That window is now hard-capped at 8s and can never become permanent (no give-up state exists).

---

## 5. Log hygiene

Per incident, at most two lines instead of N:

1. **First failure only** — `[BigPrints] Stop change failed (UnableToChangeOrder/Working) - stop still working @ 28476.5; latency 3ms, backing off (repeats stay silent until one succeeds).` The reason carries `error/orderState` plus `/OTHER-SIDE` when the event belongs to a different position; the latency is the submit→event delta.
2. **Recovery, once** — `[BigPrints] Stop change recovered after N failure(s) - working @ X.` Its *absence* after line 1 is itself the signal that the trail never recovered in that trade.

Failures 2..N are silent (backoff still growing to the 8s cap). A lost callback prints through the same path with reason `no order event in 5s, change presumed lost`. The naked-position line gains `changeInFlight={bool}`; the profit-target line is untouched.

---

## 6. What remains unverifiable until the next Playback run

1. **The actual refusal reason.** Contention (`ChangePending` collision) is the leading hypothesis, not proof. NT8 documents no trigger list for `UnableToChangeOrder` (the string appears once in the bundled corpus, as a bare enum member at `onorderupdate.md:85`). The new first-failure line settles it: state `ChangePending`/`ChangeSubmitted` → contention, the in-flight gate is the cure. State `Working`/`Accepted` with sub-millisecond latency → contention was *not* the cause and the gate is inert; next step is `isSimulatedStop: true` on the trail's `SetStopLoss` (locally simulated stop, no change round trip — `setstoploss.md:7,54`) or moving the protective leg to unmanaged `Exit*` handling. `Accepted` specifically corroborates the held/simulated-stop reading at `order.md:244`.
2. **Whether the backoff constants are right.** 2/4/8s is a guess sized to be *smaller* than the draft's 30s precisely because a sim-engine lock may clear in milliseconds. The logged latency is the number that tunes it; do not touch `StopChangeBackoffCapSec` before reading it.
3. **Whether a refused change can ever surface as `OrderState.Rejected`.** Unknown; assumed no, flatten kept as the safe default, `changeInFlight=` will report it. This is the item that must be resolved with **live** (not Playback) evidence before the LIVE-MONEY GATE at `:74-84` is cleared.
4. **Whether any of this reproduces outside Playback.** Every data point is accelerated Market Replay. A clean Playback run does **not** prove the live trail works.
5. **Watch for:** (a) `Stop change recovered` appearing → the change path works and the old failure was self-inflicted; (b) more than one or two first-failure lines per session → systemic, escalate rather than widen the backoff; (c) `no order event in 5s` → a genuinely dropped submission, a different bug; (d) `changeInFlight=True` on a REJECTED line → split the branch immediately; (e) any trade exiting at exactly its initial ATR stop with a failure line and no recovery line → that is the P&L cost of the damper and the number that justifies retuning.

**Skipped:** the `MaxStopChangeFails` give-up (log spam is already solved by printing once; suspension bought a BE desync with no cap) — add it only if the next run shows a refusal streak that survives the 8s-capped retry for a whole trade *and* the NT8 Log tab noise turns out to matter.