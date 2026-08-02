# Batch-8 research — B2 scoring, virgin shadow, covariates, quiet-day mining

**Date:** 2026-08-01 · **Corpus:** 8 sessions (2026-07-20…31; 07-29 unrecorded, labels only), 737 v3 triggers, 7 days of 1s bars
**Method:** 9-agent workflow (3 scoring/mining lenses → 2 adversarial verifiers each). Verifiers reproduced every load-bearing number independently; corrections were textual. Scripts under the session scratchpad `batch8/analysis/`.

## Decision 1 — B2 stays at 6.0 for LIVE; the corpus opens up 7×

B2 confirmed as the funnel's sole bottleneck across all 8 sessions (leave-one-gate-out: dropping B2 admits +24 candidates; no other gate adds more than +3). The cut table (K+B1 candidates, with-sweep bracket sim, independence-collapsed): **no cut below 6 ever admits a second winner** — cut 4 is 0.00R (n=3, its lone extra arc is a 07-28 loser, out-of-sample confirmation of the day-28 "zero trades was correct" verdict), cuts ≤3 go negative (07-21 loser).

- **D1: `B2MinBreakPts = 6.0` UNCHANGED in live.** No code change.
- **D2 (the real move): corpus admission = every `bars≥450 ∧ K1 ∧ K2 ∧ K3 ∧ B1` trigger regardless of break_pts, flagged `live_eligible = brk≥6` — an OFFLINE scoring convention (all covariates already logged).** Accrual: 0.25 → **1.75 arcs/session** (30 arcs in ~17 sessions instead of ~120).
- D3: UNI stays out of the balance gate (measured no-op). D4: B1 stays (rejected 2/2 losers). D5: offline sims log MAE/MFE-at-600s per candidate (all three losers stopped by ≤3.25 pt and then ran +36…+75 — bracket geometry is a future question, not a change now).
- **Pre-registered re-visit trigger:** B2 6→4 only if the logged brk∈[4,6) band reaches ≥15 collapsed arcs with avgR ≥ +0.20; a positive brk∈[−20,−5) band (currently +0.405/53 arcs uncontrolled) would mean a *band-gate inversion* — a new registration, not an edit.
- **Range day answered:** 07-22 never opens via B2 (its 5 candidates top out at brk −8.25). The range-day lever is **K3**, a separate future hypothesis (K1'/K3' relativization stays refuted as scoped).
- Caveat on the record: at n=2 arcs, the ±1s fill assumption flips signs — direction over magnitude.

## Decision 2 — the an60 fade-veto graduates; the reversal side stays dead pending reformulation

- **R2 (CONFIRMED, p=0.004, negative on 6/7 sessions): no fade-side entry ever when `an60 ≥ 0.10`.** The day-28 finding replicates on the full corpus — the first covariate to earn frozen-gate status. It vetoes 16/16 VirginExtension triggers by itself.
- R3: second fade veto, `uni_ok == False` (fading lumpy clusters: p=0.022). R2∧R3 defines the only measured non-negative fade cell (~+0.1R, ns).
- R1: the virgin fade kill is confirmed by sim (10 arcs: −1.00R at 15/30, +2.00R at 20/40 — statistically flat, max |z|≈0.7); already demoted. **The compression arm (range<60) is UNTESTED, not refuted — all three such triggers sit on unrecorded 07-29.**
- R5: an60 is a veto, NOT an entry (bracket-dependent on the follow side). R4: s_state and K-context stay log-only (s_state currently measures its own generator — needs the MAE-from-entry term, R7).
- **R6 pre-registered:** any future reversal formulation must clear ≥40/100 collapsed arcs (≥40%) at 20/40 across ≥10 sessions under R2+R3 — else the reversal side is dead as a price/flow formulation and only a new input class (book/timing) reopens it.
- **R8:** record every session — 07-29's missing 1s bars cost exactly the compression subgroup.

## Decision 3 — one new candidate from the quiet days: UE ("Unsponsored Extreme"), LogOnly

The three quiet days finally exercise K1's band (27-40 of ~50 triggers each inside 60-120 vs 17/108 on 07-27) — but 07-22 is a grind-up (2nd-highest path efficiency of the corpus), so a genuinely two-sided range day is STILL missing. Four rotation families were measured and killed; one survived every check including non-destructiveness on trend days:

**UE:** on a closed 1s bar making a new 900s extreme with the bar's own delta DISAGREEING with the break (new high on negative delta / new low on positive delta), volume ≥10, and the an60 veto (an60×push < 0.10) → fade, next-bar-open entry, 20/40 primary, 600s cap, 120s same-direction cooldown. ARM-A adds `range15 ≤ 120`; ARM-B without. ARM-B: ~7 collapsed fires/session, lift +over-random pooled p=0.0175, positive on all 3 quiet days, non-destructive on all 4 trend days. **Honest discount: ~60 parameter cells searched; the pre-registered 10-session gate (PASS: lift ≥ +0.25, ≥6/10 sessions positive; KILL: lift < +0.10 or trend sumR < −5R) is mandatory, not optional.** Implementation needs only 1s bars + aggressor delta — no book, no new bar types. Time-of-day note logged (largest quiet-day long leg started 10:55–11:06 on all three days, n=3, observation only).

## Standing items

- Implement UE as the second LogOnly setup (pending Javier's go).
- Corpus scorer adopts D2's admission + MAE-from-entry (R7) conventions.
- Keep hunting a true two-sided range day; keep the recorder on EVERY session (R8).
