All adjudications complete. Final plan follows.

---

# FINAL ACTION PLAN — BigPrints Discriminator Smoke Test Failure (2026-07-27 MNQ)

**Post-adversarial-review revision.** The refuter's central hit is **conceded and adjudicated against primary sources**: audit §4 verbatim says *"REVERSAL iff price recovers ≥50% of (sweep_end − extension low) within 60s of the low **without printing ≥8 ticks below the low**"* — the "8 ticks" is a re-extension **invalidation clause**, not an extension floor, and the recap line "neither breached 8 ticks" beside extensions of 56t/221t proves that reading. The draft's `MinExtensionTicks=8`/`NO_EXTENSION` gate is **withdrawn**. Re-verified by replay (`scratchpad/final_rule_verify.py`): the checkpoint rule **with no floor** is numerically identical to the floor=8 version on both events — R: REVERSAL ext 56t, recovery 301.8% at t_end+63.5s (audit PASS 298%); C: right-censored at file end (+120.0s) holding 39.8% (audit FAIL 40%), checkpoint would fall +130.5s; C's max in-window recovery 58.4% off the global low 28427.50 (+70.5s, ext 221t) — so first-crossing mislabels C at every floor ≤221t, while checkpoint matches both ground truths. The floor bought nothing and cost pre-registration integrity; `extension_ticks` is already written to every outcome JSONL record (BigPrintsDiscriminator.cs:449), so any inclusion floor remains available **offline, reversibly**, once base rates exist.

Two previously unverified claims are now **verified from disk** (`/mnt/c/Users/javlo/Documents/NinjaTrader 8/BigPrintsAI/discriminator_log.jsonl`): both trigger records carry `"mode":"Immediate"`, both outcome records carry the corrupt `extension_ticks:1.0, recovery_pct:100.0`, and C's exact `top_frac = 0.000799130545965989 = 14/17519`. The adjudication also surfaced **one new bug**: both triggers logged `action:"immediate_entry"` while the transcript shows the session gate rejected both — the corpus's action field lies in Immediate mode (§2.4).

## 1. Root causes, ranked

**(a) User configuration — why no trade happened (dominant):**

1. **`EntryMode` was `Immediate` at runtime, not `Discriminator`.** Now proven twice over: (i) `"mode":"Immediate"` on both trigger records in `discriminator_log.jsonl` (read this session); (ii) transcript ordering — the "not traded" print precedes the eval line, achievable only via the Immediate path (`BigPrintsStrategy.cs:453-454`); Discriminator dispatch runs only from the decision queue after the +5s eval (`:459-471`, queued `:890-894`), and R's non-Abstain `Reversal -> LONG` would have produced a second gate print. The `[BigPrints/disc]` lines are a red herring: `EnableDiscriminatorLog` defaults `true` and constructs `_disc` in both modes (`:340-347`).
2. **`SessionStart`/`SessionEnd` runtime values are not 930/1555** (hypothesis, honestly hedged). With defaults, `InSession(93346)` → true. The only value-set consistent with both 09:33:46 and 09:42:34 rejecting is a wrapped window — most likely swapped values (1555/930) hitting the overnight branch (`:862`): `93346 >= 155500 || 93346 < 93000` → false — or a stale serialized instance (NT8 does not re-run `SetDefaults` on saved instances). The `.cs` defaults are innocent. Unconfirmable until the Parameters dialog is read (§4).

**(b) Code defects:**

1. **Outcome tracker (`BigPrintsDiscriminator.cs` `UpdateOutcome`, lines 327-368): resolves REVERSAL on first crossing of 50% against a still-moving extreme, with no checkpoint.** Two observable failures: (i) 1-tick extensions make recovery binary 0%/100%, so one tick of give-back resolves REVERSAL instantly (both events: R at +0.016s, C at +0.468s — reproduced exactly); (ii) even with any extension floor, C transiently crossed to 58.4% then faded to 39.8%, so first-crossing mislabels C at every floor 2-221t (floor-only fix replayed: C false REVERSAL 8t/50% at +1.06s). The audit's own C number (40%, not 58.4%) proves the pre-registered definition meant a **held** recovery, not a touched one. Both on-disk outcome records are corrupt (`REVERSAL 1t/100%`).
2. **JSONL `action` field lies in Immediate mode** (`BigPrintsStrategy.cs:898-899`): hardcodes `"immediate_entry"` regardless of what `TryEnter` returned — the spec's amended action vocabulary (`session | lockout | gov_skip | ...`) is recorded only on the Discriminator path (`:468-469`). Both smoke-test triggers logged `immediate_entry` while the session gate returned `"session"`. Corpus data-integrity bug, found this session.
3. **Diagnosability defects**: the session rejection print (`:1038`) discloses none of `t/start/end`; nothing prints resolved `EntryMode`/session params at startup. Either would have made this incident a 10-second read.
4. **T1 threshold transfer** — *not* a bug (`ComputeT1` reproduces the audit's D2 to 4 decimals) but a decision item: the frozen 0.0008 was derived on 39-55s of history vs the live engine's up-to-300s window, and C's live `top_frac` 0.0007991 misses it by 1.1%. See §3.

## 2. Code changes (exact)

### 2.1 `BigPrintsDiscriminator.cs` — checkpoint outcome rule, **no new constants, no new labels**

Replace `UpdateOutcome` (lines 327-368) and its header comment entirely:

```csharp
// Outcome label (audit §4, causal operationalization — AMENDED 2026-07-30 post smoke
// test; prior labels void, corpus count restarts at zero): running extreme; every new
// extreme restarts the 60 s window. Recovery is evaluated ONCE, at the first print
// after the extreme has held OutcomeWindowSec — never on first crossing. A transient
// spike through 50 % that fades is a failed bounce, not a reversal (event C: 58.4 %
// max in-window, 39.8 % held -> CONTINUATION, matching audit §4's own numbers).
// The audit's "without printing >= 8 ticks below the low" invalidation clause is
// subsumed, strictly: ANY new tick below the low restarts our window, so a candidate
// the audit would void can never be called here in the first place. (On R and C this
// strictness changes nothing — verified by replay.)
private void UpdateOutcome(DateTime t, double price)
{
    Outcome o = _outcome;

    bool newExtreme = o.IsBuySweep ? price > o.Extreme : price < o.Extreme;
    if (newExtreme)
    {
        o.Extreme = price; o.ExtremeTime = t;
    }

    double ext = Math.Abs(o.SweepExtreme - o.Extreme);

    if ((t - o.ExtremeTime).TotalSeconds > OutcomeWindowSec)
    {
        if (ext < _tickSize / 2) { ResolveOutcome(t, "UNRESOLVED", 0); return; } // 0/0: no extension low exists
        double recovery = o.IsBuySweep ? (o.Extreme - price) / ext : (price - o.Extreme) / ext;
        ResolveOutcome(t, recovery >= OutcomeRecoveryFrac ? "REVERSAL" : "CONTINUATION", recovery);
        return;
    }
    if ((t - o.TriggerTime).TotalSeconds > OutcomeCapSec)
    {
        // Reachable only while the window is still open (the branch above returns
        // otherwise) => an extreme was set after TriggerTime + (Cap - Window),
        // so ext >= 1 tick here by construction; still extending => CONTINUATION.
        double bestAtCap = o.IsBuySweep ? (o.Extreme - price) / ext : (price - o.Extreme) / ext;
        ResolveOutcome(t, "CONTINUATION", bestAtCap);
    }
}
```

Justifications:
- **No floor.** Refuter confirmed correct; replay confirms floor=0 vs floor=8 identical on both available events (both clear at 56t/221t), and the floor's only untested effect is the asymmetric one (thinning fast/clean reversals out of the scoreboard). Any future floor is applied offline via the already-logged `extension_ticks`.
- **`ext == 0` → `UNRESOLVED`** — reuses the pre-registered enum (its spec meaning is exactly "residual case where the label function couldn't be computed"; here recovery = 0/0). **The label taxonomy is unchanged from pre-registration** — no enum amendment needed. Cap branch needs no such guard: cap reachable ⇒ window open ⇒ an extreme refreshed after trigger+120s ⇒ ext ≥ 1 tick (invariant, provable from branch ordering).
- The cap's `UNRESOLVED` arm was dead code (window branch returns first whenever `stillExtending` would be false) — removed. `UNRESOLVED` also still arrives via `CloseOutcome` (superseded), untouched.
- REVERSAL now resolves at extreme+60s instead of instantly — irrelevant to trading: outcome state is disjoint from `_pending`/`Decision` and never feeds `TryEnter` (refuter-verified).

**Ceremony (symmetry with §3, refuter's hit accepted):** this is a re-operationalization of a pre-registered definition. Amend audit §4 and the spec (below), declare the two pre-amendment outcome records void, and restart the corpus count at zero. Verified free: the JSONL holds exactly 2 triggers (both `mode:Immediate`, and the second is inadmissible under §4's independence rule anyway — same session, 9 min apart, same decline arc) and 2 corrupt outcomes.

### 2.2 Spec + audit amendments

- **Spec** (`docs/superpowers/specs/2026-07-30-bigprints-discriminator-entry-design.md`, outcome-record paragraph, lines ~104-113): label enum **unchanged**; rewrite the operationalization paragraph to the checkpoint semantics, stating explicitly: *recovery is evaluated once at extreme+60s, never on first crossing* — the audit's prose reads as first-crossing but its own C number (40%, with a 58.4% in-window touch) contradicts that reading; without this sentence a future implementer reintroduces the bug. Add: ext==0 at checkpoint → UNRESOLVED (0/0); cap always CONTINUATION (still-extending invariant); running-extreme restart strictly subsumes the ≥8-tick invalidation clause.
- **Audit §4**: mirror the amendment note with date and "labels recorded before 2026-07-30 amendment are void; count restarted at zero".
- **Validator** (`tools/validate_discriminator_log.py`): no scoreboard change — line 61 already restricts the tally to `REVERSAL`/`CONTINUATION`. Add only a strict whitelist so typos fail validation, in the `elif r.get("type") == "outcome":` branch after the missing-keys check:
```python
if r.get("label") not in ("REVERSAL", "CONTINUATION", "UNRESOLVED", "UNRESOLVED_SUPERSEDED"):
    print("  FAIL line %d: bad outcome label %r" % (i, r.get("label")))
    errors += 1
```

### 2.3 `BigPrintsStrategy.cs` — diagnosability (2 one-liners)

After the `_disc` construction block (line 347, `State.DataLoaded`):
```csharp
Print(string.Format("[BigPrints] mode={0} session={1:0000}-{2:0000} discLog={3}",
    EntryMode, SessionStart, SessionEnd, EnableDiscriminatorLog));
```
Replace line 1038 (refuter's format nit fixed — `00\:00\:00` renders 93346 as 09:33:46):
```csharp
Print(string.Format("[BigPrints] Cluster not traded: outside session window (t={0:00\\:00\\:00}, window {1:0000}-{2:0000}).",
    ToTime(marketTime), SessionStart, SessionEnd));
```

### 2.4 `BigPrintsStrategy.cs` — truthful `action` in Immediate mode (new)

Field near the signal-queue fields (~line 188): `private volatile string _lastImmediateAction;` (one-slot, same publish pattern as `_signalQueued`; string reference assignment is atomic, volatile for cross-thread visibility — strategy thread writes, market-data thread reads at eval time, per-trigger cadence).

Replace lines 453-454:
```csharp
if (EntryMode == BigPrintsEntryMode.Immediate)
{
    string result = TryEnter(_signalIsBuy, _signalVolume, _signalTime);
    _lastImmediateAction = result == "entered" ? (_signalIsBuy ? "long" : "short") : result;
}
```
Replace the ternary at line 899:
```csharp
EntryMode == BigPrintsEntryMode.Immediate ? (_lastImmediateAction ?? "immediate_entry") : "no_trade");
```
This makes the corpus record `action:"session"` (etc.) in Immediate mode, matching the spec's amended vocabulary and the Discriminator path (`:468-469`). Pairing note: the eval's action reflects the most recent immediate attempt; supersede races already write their own `"superseded"` record — acceptable for a diagnostic field.

### 2.5 Recorder

Extend post-trigger capture to **≥ t_end+180s + margin** (resolution is bounded by the cap at trigger+180s plus one print). Both existing files end at +119.9/+120.0s: the cap branch has never been genuinely exercised, and C's corrected label is unresolvable offline (checkpoint +130.5s > file end). Without this, corpus labels can never be independently re-verified from recordings.

## 3. T1 threshold — recommendation with the honest caveat

Facts (all now from the primary source): C's live `top_frac = 14/17519 = 0.000799130545965989`, throughput 21.15; frozen `T1TopFracContinuation = 0.0008` misses it by 1.1% → Abstain. R: `top_frac = 0.0000778`, throughput 9.5 — the `ComputeT1` AND-gate (`:267-272`) blocks R's Continuation arm on throughput (9.5 < 15) regardless of any top_frac threshold. Live T2/T3 transfer cleanly (T3 R +43/−195 exact; T3 C −396 exact; T2 C 0.82 vs audit 0.87, both Abstain). T1's window is the only transfer failure: audit thresholds were derived on 39-55s of history vs the live min(300s, session) window, and top_frac decays with window length (Report 3's sweep: C 0.00237@10s → 0.00091@54s).

**Recommendation: set `T1TopFracContinuation` (line 33) 0.0008 → 0.0007, plus Print format `{3:0.0000}` → `{3:0.000000}` (line 314).**

**The honest caveat, stated plainly (refuter's framing accepted):** the window-transfer argument motivates the *direction* of the change, but the *value* 0.0007 is chosen so that the one known continuation classifies correctly — an n=1 boundary fit on C alone (R cannot be affected, so this is not even n=2). It is calibration, not validation. What makes it legitimate anyway: audit §4 explicitly provides the mechanism ("a revised threshold restarts that discriminator's count"), and the restart is verified free — T1's admissible count is zero (2 records on disk, both `mode:Immediate`, second inadmissible, labels void per §2.1). The corpus will score 0.0007 prospectively from zero, which is the only honest test any value could get at n=2 — the audit's own §5.3 concedes every frozen threshold is a calibration artifact until the corpus scores it. Robustness margin at the exact on-disk value: one 8-lot peer print moves C's top_frac by 1/17519 ≈ 5.7e-5 (~7%); 13/17519 = 0.000742 still flags Continuation, 12/17519 = 0.000685 → Abstain — the margin tolerates −1 peer print, not −2. This fragility is inherent to low-max-print events (C's max_print=8 is an ordinary MNQ size), not to the threshold. Alternative A (keep 0.0008 frozen, accept C-class abstains) remains defensible if pre-registration optics outweigh the abstain-rate risk — but audit §4 fails any tier with >50% abstains, so A risks failing T1 for a window artifact. Leave `T1TopFracReversal = 0.0005` and the throughput split 15 untouched. Document in audit §4 + spec as re-pre-registration with count restart — same ceremony as §2.1.

## 4. User checklist (before re-running the smoke test)

1. Open the strategy instance's Parameters dialog on the Playback chart. Set **Entry Mode = Discriminator** — it is currently `Immediate`, proven by `"mode":"Immediate"` on both trigger records in `discriminator_log.jsonl`.
2. Read **Session Start / Session End** in the same dialog. They must be **930 / 1555**. If they read 1555 / 930 (swapped) or anything else, fix them — do not trust that code defaults apply to a saved instance. Report what you find (this confirms or kills root-cause a.2).
3. If the instance predates recent parameter additions, delete it and add the strategy fresh so `SetDefaults` runs.
4. After recompiling with the fixes, confirm the startup line prints `mode=Discriminator session=0930-1555` before trusting anything else in the run.
5. Archive or delete the current `discriminator_log.jsonl` (4 lines, all void) so the restarted corpus starts clean.
6. Re-run the same Playback session — the previous run never exercised Discriminator dispatch, so it validated nothing about entry behavior. Expect: R enters LONG after the +5s eval (if session gate passes); C abstains under frozen T1, flags Continuation under 0.0007.

## 5. What remains unknown until more events are recorded

1. **C's label under the corrected rule** is right-censored on the recording (checkpoint +130.5s vs file end +120.0s, fading 58.4%→39.8%) — live it resolves CONTINUATION, but offline re-verification is impossible until the recorder captures ≥ t_end+180s (§2.5).
2. **Base rate of small/zero-extension events** — whether ext==0 UNRESOLVEDs are rare noise or a real class, and whether an offline extension floor is ever warranted. Both known events (56t, 221t) say nothing; the floor question is deliberately deferred to analysis time on logged `extension_ticks`.
3. **Whether the checkpoint rule's extra strictness vs the audit's hand rule ever flips a label** — running-extreme restarts on *any* new tick below the low where the audit tolerated 1-7 tick dribble. Inert on R and C (verified); untested elsewhere.
4. **The cap branch (trigger+180s CONTINUATION)** has never been genuinely exercised — both files end before +180s.
5. **T1 = 0.0007 generalization** — n=1 fit; scored prospectively from a zero count. Also whether T2's abstains are informative and whether any threshold survives (audit §5 items 3/5 stand).
6. **Root-cause a.2 (session params)** — hypothesis until the dialog is read (checklist step 2).
7. **Base rates and mechanism classes** — audit §5.1/5.2 unchanged: one reversal, one continuation, one decline arc; nothing here estimates precision or expected value of any rule.

Files: `/home/javlo/Code Projects/main-project/projects/Trading/BigPrints/BigPrintsDiscriminator.cs`, `/home/javlo/Code Projects/main-project/projects/Trading/BigPrints/BigPrintsStrategy.cs`, `/home/javlo/Code Projects/main-project/projects/Trading/BigPrints/tools/validate_discriminator_log.py`, `/home/javlo/Code Projects/main-project/docs/superpowers/specs/2026-07-30-bigprints-discriminator-entry-design.md`, `/home/javlo/Code Projects/main-project/projects/Trading/BigPrints/docs/audits/2026-07-30-recorder-pair-analysis.md`, `/mnt/c/Users/javlo/Documents/NinjaTrader 8/BigPrintsAI/discriminator_log.jsonl` (evidence, to be archived); adjudication script `/tmp/claude-1000/-home-javlo-Code-Projects-main-project/cbf37f3f-340e-4533-924e-eb9fa665481a/scratchpad/final_rule_verify.py`.