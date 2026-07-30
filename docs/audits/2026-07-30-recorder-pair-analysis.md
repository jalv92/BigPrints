# Event Recorder — first pair analysis: sell304 (reversal) vs sell423 (continuation)

Date: 2026-07-30. Source recordings: `Documents/NinjaTrader 8/BigPrintsAI/recordings/2026-07-27/event_093346_sell304.json` and `event_094234_sell423.json` (MNQ 09-26 Playback). Method: multi-lens agent analysis + adversarial verification (2 workflows, 14 agents). Reproduction scripts alongside this file.

---

# PART 1 — Single-event verdict: SELL 304 @ 28501.50 (09:33:46)

All checks are in. One refuter claim is decisively refuted (the bars "inconsistency" — NT8 minute bars are close-stamped; the in-progress "09:34" bar snapshotted at trigger matches the tape's pre-trigger hi/lo/last exactly: 28530.25 / 28500.75 / 28501.00). Most others land. Final verdict follows.

# FINAL VERDICT — MNQ 09-26, 09:33:46 event

Adjudication re-runs: impact bins (`adjudicate.py` section A), turnover base rate (B), print-size phases (C), burst scan (D), bid path (E), bars reconciliation (F2), bottom-bin absorption (G), final-leg depth (H). All numbers below are from my own recomputes unless noted.

## Refuter scorecard

| Attack | Ruling |
|---|---|
| R1-1/R2-1: impact/1000 collapse is a bin artifact | **CONFIRMED.** My bins: −68.91 (pre) → −16.90 (sweep bin, 1124 sold) → **−63.97 the very next bin (594 sold)**, and the print low (42656 ms) sits inside that third bin. Base rate: 4/26 bins in −30..+22 s show \|impact\| < 22, including a quiet mid-downtrend bin (+19.5, no reversal followed). The signal as stated is dead. |
| R1-2/R2-3: turnover 5.83x unremarkable | **CONFIRMED.** 392 levels with flow: median 3.48x, mean 4.04x, 76% > 2x, **90 levels ≥ 5.83x**. Top turnover: 28522.25 at 14.55x (refill 834, broken), 28506.25 at 11.36x (broken), 28502.00 at 11.10x, 28501.50 at 10.43x — all run over. Worse: 28490.75, home of the "iceberg plateau," turns over only **2.77x — below file median**. Turnover is not diagnostic of defense. |
| R1-3: "no institutional seller" | **CONFIRMED.** big_prints.csv: **SELL 49 (plus SELL 24) at t=38872, −0.26 s, 28504.25** — the largest single print in the file is a sell, 224 ms before the trigger — and **SELL 40 at +22.2 s** inside the failed retest, larger than the rally's biggest buy (27). |
| R1-4: bid relocation overstated | **CONFIRMED.** First bid at 28490.25 arrives t=42168 (488 ms *before* the low), then falls away; 64 best-bid arrivals ≤ 28487.00; last bid < 28490.25 is at **t=43656** — sustained hold begins 1.0 s after the low, not 400 ms. Prints ≤ 28487 span 42656–42760 (22 prints, 29 contracts), not one 8 ms flicker. |
| R2-0: hindsight windowing | **CONFIRMED from source.** `deep.py` anchors windows on `bottom_t = first_t_at_min`; `deep2.py` grades bins with `close_price_at(window_end)`. Every "detectable" window was drawn around the known low. |
| R2-2: print-size profile is baseline, not signature | **CONFIRMED.** Sell prints by phase: pre-trend avg 1.18 / 99.0% ≤3-lot; flush 1.23 / 97.5%; post-low 1.07 / 99.8%; rally sell-side 1.11 / 99.6%. Statistically indistinguishable across continuation, flush, and reversal. Zero discriminating power. |
| R2-5: non-reversing twin burst | **CONFIRMED.** t=32400 (−6.7 s): 178 sold / 200 ms, 17-tick range, mid-trend — followed by continued decline. Nuance: at ≥300/200 ms the trigger bin (619 sold) is **unique in the file** — but that threshold was fit on this one outcome; it's a hypothesis for the corpus, not a rescue. |
| R2-6: bars/tape inconsistency | **REFUTED.** NT8 minute bars are stamped at bar *close*. Bar "09:34" = physical 09:33:00–09:34:00, snapshotted at trigger time: O 28521.50 (≈ prior close 28521.75), **H 28530.25, L 28500.75, C 28501.00 = exactly the tape's pre-trigger high, low, and last print**. Bar "09:33" (= 09:32–09:33) H 28597.25 sources the "downtrend from 28597" context. Bars and tape are the same feed; the downtrend framing stands. |

## (1) Revised mechanism narrative

**Context (intact):** established downtrend from 28597, prior 10-min delta −2835. Pre-sweep book stacked bid-side: bid10=336 vs ask10=103, imb 0.77.

**Trigger (revised):** SELL 305/186 in 32 ms through 28503.75→28500.75. The largest print in the entire file — SELL 49 at 28504.25 — hit 224 ms *before* the trigger; at least one large seller participated in starting this. The trigger's own composition (max 37, mostly 1-lots) no longer proves anything: that size profile is what every phase of this tape looks like.

**Flush (39.1→42.7 s, revised):** 1,332 further contracts sold, driving 16.25 points below the lowest previously traded price in the file — still the strongest surviving evidence for a stop-run: the stacked 28500–28503 bids were consumed (refilled +263/drained −278 at 28501.50), and the extension ran through progressively *thinner* trade: the final leg (41.1–42.7 s) printed only **508 contracts total, max 38 at any level, 1 contract at the absolute low 28486.75**. The "absorption from the first second" claim is retracted — the −16.9 impact bin is a windowing artifact; the next 2 s fell 9.75 points on 594 contracts (−63.97/1000, worse than the pre-sweep baseline). The book *thinned* after the sweep; it did not absorb early.

**The turn (revised):** at the bottom the two mechanisms are real but **not separable**: 161 sold vs 82 bought (net −79) in 42.5–43.0 s with price pinned 28490.25→28490.25 (verified — genuine passive standing), refill 132 at 28490.25, the 21-lot plateau at 28490.75 — *and* the cascade's fuel was demonstrably spent (thin final leg, sell rate dying +1–2 s later). A modest passive bid cluster met a starving cascade; this file cannot tell you which mattered more. The bid's path back was choppy: touched 28490.25 before the low, revisited ≤28487 for ~1 s, held above 28490.25 only from t=43656.

**Recovery (intact arithmetic):** first bounce +4→+10 s recrossed 28500; **failed** — full retest to 28490.25 by +23 s (net −148, containing a 40-lot sell); durable leg from +24 s: delta +652 (+30–60 s), +1902 (+60–120 s), 28486→28581. Short-covering of the down-leg's net short explains at most ~37% of recovery net buying; ≥63% is fresh initiative. The "retail small-lot crowd" gloss on the rally is withdrawn (size profile is uninformative), the arithmetic is not.

## (2) Confidence per claim

- **Stop-run/sweep extension as the down-leg mechanism — MEDIUM** (was PRIMARY/high). Survives on: consumed bid stack, 16.25-pt drive into virgin prices, thin final leg, full V-recovery. Lost: print-size evidence (uninformative), uniqueness (a comparable burst at −6.7 s did not reverse). Best available narrative for this tape; not a demonstrated class — n=1 by construction (`other_clusters: []`).
- **Absorption at 28490.25/.75 stopped the fall — MEDIUM-LOW.** The zero-displacement-under-net-sell fact (161/−79/flat) is verified and is the right *kind* of evidence; but turnover corroboration is gone (76% base rate; 28490.75 below median), the plateau's base rate is unmeasured, and absorption was not operative before ~42.5 s. Entangled with exhaustion; not separable here.
- **Seller exhaustion — MEDIUM,** co-primary with absorption, same entanglement.
- **Iceberg defense — LOW.** One plateau, level turnover 2.77x, no base rate.
- **Rally = majority fresh initiative buying — MEDIUM-HIGH.** Pure arithmetic on uncontested delta windows (−955 short vs +2,608 net buy). The *who* (retail vs institutional) — no confidence either way.
- **"No institutional actor except the buyer" — RETRACTED.** SELL 49 pre-trigger, SELL 40 mid-retest.
- **Data-quality caveats — HIGH,** unchanged: 4 ms grid, 252 ms book cadence, book blind through the sweep, counts ±1.
- **Downtrend context / bars — HIGH,** restored by the close-stamp reconciliation (three exact matches).

## (3) Real-time detectable vs hindsight

**Nothing in this file is demonstrated detectable before the low.** Specifically:

- **Impact/1000 collapse — DEAD as an entry signal.** The causal version fires at ~41.1 s and is immediately followed by the steepest 2 s of the event (−9.75 pts). In-file false-positive base rate 4/26 bins. At most a hindsight description of the sweep bin.
- **Flush print-size character — DEAD.** Identical in continuation, flush, and rally. Zero information.
- **Turnover/refill thresholds — DEAD** at any tested threshold (76% of levels > 2x; 23% ≥ 5.83x; top-turnover levels all broke).
- **Passive repricing vs net-sell flow — HINDSIGHT.** The divergence is real but confirmed ~1.0 s *after* the low, window drawn post-hoc, base rate unmeasured.
- **Surviving as *pre-registered hypotheses* (not signals):** (i) sell-burst ≥300/200 ms — unique to the trigger in this file, threshold fit on n=1; (ii) extension into virgin prices with per-level trade volume collapsing (the 508-contract final leg) — computable causally, untested; (iii) single-level net-sell-with-zero-displacement over ~500 ms — computable causally, confirms at the low at earliest. All three need denominators before anyone trades them.
- **Round-trip reality:** any hypothetical entry near 28487–28490 had to survive a full retest to 28490.25 lasting to +23 s. On MNQ that chop plus commissions is a material fraction of the eventual edge; survivability is an unanswered distributional question, not a footnote.

## (4) Open questions — what to record next

1. **Fix the recorder to keep negatives.** `other_clusters` must be populated: every BigPrints trigger per session, reversal or not, with identical tape+inside+book instrumentation. Without the denominator, "PRIMARY" labels are unfalsifiable.
2. **Auto-capture matched control bursts** (e.g., any 200 ms window with sell ≥150) even when the trigger doesn't fire — this file already contained one in-sample negative (t=32400) that nobody recorded as such.
3. **Pre-register causal signal definitions** — trailing windows only, fixed thresholds chosen *now*: burst intensity ≥300/200 ms; virgin-price extension with per-level volume < N; net-sell-flat-price ≥500 ms at a level; refill rate at successive best bids. Then measure hit/false-positive rates across ≥30–50 clusters spanning trend and range days.
4. **Define the outcome label ex-ante** (e.g., "recovers ≥50% of sweep extension within 60 s without trading X ticks below the signal low") so reversal/no-reversal isn't judged by eye.
5. **Log max adverse excursion after every candidate signal** — the failed-first-bounce problem needs a distribution to size stops against.
6. **Capture displayed depth at each new low tick** (was the bottom thin or defended?) and, if the recorder allows, book snapshots faster than 252 ms around triggers — the current cadence leaves the entire sweep blind.
7. **Track large prints (≥20) in the ±30 s around triggers.** The 49-lot sell 224 ms pre-trigger is the single most interesting unexplained datum in the file — is a large sell print immediately preceding the trigger a recurring feature of these sweeps?

Files: `/tmp/claude-1000/-home-javlo-Code-Projects-main-project/cbf37f3f-340e-4533-924e-eb9fa665481a/scratchpad/analysis/adjudicate.py` (all verification queries), `event.json`, `big_prints.csv`, `levels.csv`.
---

# PART 2 — Paired comparison verdict: R (sell304, reversed) vs C (sell423, continued)

All adjudications are complete. I now have every disputed number settled by a single identical code path (`adjudicate_final.py`) run on both raw files. Producing the final verdict.

---

# FINAL COMPARISON VERDICT (post-adversarial) — MNQ sell-sweep pair, Event R (reversed) vs Event C (continued)

Adjudication code: `/tmp/claude-1000/-home-javlo-Code-Projects-main-project/cbf37f3f-340e-4533-924e-eb9fa665481a/scratchpad/adjudicate_final.py` — one code path, both `event.json` files. Every number below is from that run (plus the strict-pre-trigger follow-up in its run log). Raw dirs: `scratchpad/analysis/` (R), `scratchpad/analysis2/` (C).

## 0. Adjudications — where the refuter and the synthesis disagreed, settled by re-running the raw files

1. **D1 window bug — refuter RIGHT, and it is worse than they reported.** The reported +55 (R) / −307 (C) are cum delta **since file start** (confirmed: identical values reproduced from `lead_verify.py`'s unbounded sum), not trailing-10s. But the refuter's replacement numbers (−266/−439) were themselves evaluated at t−2s, not t_start. Strictly causal trailing-10s over [t_start−10s, t_start): **R = −652, C = −430** — the direction *inverts*: R was MORE sell-heavy than C into its trigger. R's final 2s alone was −390 (the 49+24 cluster plus flush onset). Percentile within own pre-trigger 10s windows: R = 0.0% (most negative in its file's pre-trigger history), C = 4.4%. The VWAP60 leg inverts too: strict at t_start, R = **−15.90**, C = **−13.35** (reported −6.90/−10.52 were t−2s values; R slid hard in the final 2s). And C's own first ~42s were net **+123** — C's selling was also concentrated in its last 10s. **D1 is REFUTED entirely, not "fixable":** both legs flip sign with a 2s change of evaluation instant, and at the spec instant the two events are indistinguishable (both triggers fired at the sell-heaviest trailing window of their own pre-trigger tape). The "R detonated out of calm / C was already accelerating" narrative is an artifact of window length on both sides.
2. **D3/H2 "no separation" — refuter WRONG on the headline; their recompute had its own lookahead bug.** Their "literal spec" script (`attack4.py`) accumulates per-level volume over the **entire remaining file** — for R that folds the failed bounce and the +23s retest (300+ extra contracts) back into the first-16 levels, manufacturing the flat 0.90 ratio. Under the actually spec-faithful causal version (volumes counted only up to first-touch of the 16th level below sweep_end, cap t_end+5s): **R ratio = 0.20, corr −0.42, evaluable 0.34s after t_end; C ratio = 0.87, corr −0.16, evaluable 1.47s after t_end.** The separation survives fully causally, and R's collapse is robust (dropping R's single 226-lot level still gives 0.43 ≤ 0.5). What survives of the attack: (a) the originally reported 0.38x vs 1.25x used the hindsight `low_t` boundary (70.5s of future for C) — those numbers are retired; (b) C's *corr* sign is windowing-sensitive (−0.16 at 16-levels vs +0.29 at fixed t_end+5s) — so the corr statistic is dropped, the ratio kept; (c) against pre-registered thresholds C lands in **abstain** (0.87, not ≥1.0) — H2 is now one-sided: it flags R, it does not positively flag C.
3. **D2 causal percentiles — refuter RIGHT, magnitude narrows, direction survives.** Causal-only (sells strictly before t_start): R max-print 37 → 1 peer ≥ in 3,766 (top 0.027%; the peer is the 49-lot 224ms earlier — same cluster); C max-print 8 → 5 peers ≥ in 5,476 (top 0.091%). Whole-file numbers (0.017%/0.135%) are retired as leaky. Neither file has the spec's "trailing 5 min" (39.1s / 54.6s of history) — spec amended below.
4. **H1 fragility — refuter RIGHT, confirmed exactly.** Max rolling-200ms sell-sum outside trigger±500ms: C = **296** at t_end+1.6s (its own continuation selling — 4 contracts under the 300 line); R = 178 (at −6.6s, pre-trigger). Global peaks 685 (R) / 599 (C) — both trigger windows blow past the threshold identically. Dead as anything but a sweep-occurrence flag.
5. **Sequential confound — refuter RIGHT, confirmed to the decimal.** R's file ends 09:35:46.016 at 28581.0 (its rally top); C triggers 09:42:34.628 — **408.6s later, unrecorded**; C's low 28427.50 is ~153 pts below R's file-end price. R and C are two samples of one continuous decline arc, C drawn after R's entire reversal had been erased.
6. **Big-print row — refuter RIGHT.** R's 24 and 49 print at the identical millisecond (t=38872) — one clustering event. Base rates: 5 (R) / 4 (C) sells ≥20 per whole file. Retired from the ranked table; the 49-lot survives only *inside* D2 (it is the causal lumpiness evidence).

Unattacked and re-verified: D5 (R −248 / +2.25 pts; C −1056 / −23.50 pts), D4 intervals (R: −272, +43, −195, +289 for (0,250ms], (1,2s], (2,5s], (5,10s]; C: +14, −396, −181, −349), sweep anatomy (R 305c/186 prints/32ms/12 lvls/9.5 c/ms; C 423c/330 prints/20ms/21 lvls/21.1 c/ms — boundary-inclusive counts, 304/185 in the event summary is the same sweep), outcome labels (R 56-tick ext, recovery 298% PASS; C 221-tick ext, recovery 40% FAIL; neither breached 8 ticks).

## 1. Revised discriminator table — survivors only

| # | Discriminator (identical causal code both events) | R | C | Earliest causal horizon | Confidence |
|---|---|---|---|---|---|
| 1 | **Sweep anatomy / lumpiness (D2)**: causal percentile of max trigger print vs prior same-side prints; throughput; levels; print-size character | 37 = top **0.027%** (1 peer: the 49-lot in the same pre-trigger cluster); 9.5 c/ms, 12 lvls, 186 prints | 8 = top **0.091%** (5 peers); 21.1 c/ms, 21 lvls, 330 prints, 99%+ ≤3 lots | **t_end + 0ms** | **Medium.** Only discriminator fully causal as originally computed AND surviving the leak fix. Magnitude narrowed 8x→3.4x under causal-only ranking. n=2. |
| 2 | **Extension-volume collapse ratio (H2/D3, pinned spec)**: sell vol per level, first 16 levels below sweep_end, counted only to first-touch of 16th level (cap t_end+5s); ratio = mean(last 8)/mean(first 8) | **0.20** (REVERSAL-prone; robust to dropping its 226 outlier → 0.43) | **0.87** (**abstain** — not ≥1.0) | t_end + 0.34s (R) / 1.47s (C); ≤ t_end+5s by construction | **Medium-low.** Survives strictly causal recompute (refuter's contrary claim was their own bug), but one-sided at n=2: identifies R, abstains on C. Corr statistic dropped (sign-unstable); ratio only. |
| 3 | **Two-stage post-sweep delta flip (D4)**: (1,2s] and (2,5s] net delta | +43 then −195 → passes both stages | −396 → continuation | t_end + 5s | **Low.** Untouched by the attack but thresholds (esp. the −250 clause) were shaped to admit R's failed first bounce. Unique-feature risk. |
| 4 | **10s composite (D5)**: net delta (t_end, +10s]; price vs sweep_end | −248; **+2.25 pts** | −1056; **−23.50 pts** | t_end + 10s | **High as description, nil as insight** — partially definitionally entangled with the 60s outcome label. Benchmark ceiling: D2/D3'/D4 must beat this horizon to matter. |

**Retired (killed by adjudication):** D1 pre-trigger regime (both legs sign-flip with evaluation instant; zero separation at spec instant), H1 burst (fires identically on both; threshold inside C's post-sweep noise), H3 absorption (premise dead: max net-sell-while-holding 16–30 vs floor 50), big-print-cluster row (base-rate artifact; folded into D2), first-250ms delta (inverted vs intuition, R −272 / C +14 — unusable without corpus), book imbalance, bid-stack size, best-bid churn (all previously retired, unchanged).

## 2. Mechanism contrast (revised)

The pre-trigger "calm vs accelerating" story is dead: **both** sweeps detonated at the sell-heaviest trailing-10s window of their own recorded pre-trigger history (R −652, its file's 0th percentile; C −430, 4.4th), both ~13–16 pts under their trailing VWAP, and both with the selling concentrated in the final seconds. What actually separates the pair, causally, is the *character of the flow at and immediately after the trigger*. R's sweep was lumpy: a 49+24 same-millisecond cluster 224ms before, a 37-lot inside a 186-print/32ms sweep — prints of a size the prior tape produced once in ~3,800 — and its extension starved almost instantly: by 0.34s after the sweep, the most recent half of the first 16 levels below sweep_end was trading at 0.20x the first half, delta flipped positive within (1,2s], and after one failed bounce (−195 in (2,5s]) the tape went +289 in (5,10s] and price round-tripped above the sweep price. Finite, front-loaded, done — the signature of forced/stop-run liquidation exhausting. C's sweep was uniform algo shredding — max print 8 across 330 prints, 21 levels in 20ms at 2.2x R's throughput, size-character matched 5 times in its own prior tape — and nothing exhausted: per-level extension volume held near-flat (0.87), delta never flipped (−396, −181, −349), price ground −23.5 pts by 10s. A process, not an event. **However** — see §5 — the pair is sequential (one decline arc, C sampled 7 minutes and one fully-erased bounce downstream of R), so "two mechanism classes" and "one liquidation arc sampled early then late" are observationally equivalent at n=2. The flow-character discriminators (D2, D3') are the ones least contaminated by that confound, because they read the sweep itself, not the regime around it.

## 3. H1 / H2 / H3 after n=2

- **H1 (sell-burst ≥300/200ms): REFUTED.** Fires once per file, both times inside the trigger itself (zero lead), identical signal, opposite outcomes; and the 300 threshold sits 4 contracts above C's ordinary continuation selling (296 at t_end+1.6s). Keep only as the sweep-occurrence flag.
- **H2 (virgin-price extension with per-level volume collapse): SUPPORTED ONE-SIDED, spec now pinned.** Under the strictly causal 16-level/5s-cap operationalization it cleanly flags R (0.20) and abstains on C (0.87). The dramatic original numbers (0.38x vs 1.25x) used a hindsight leg boundary and are retired; the corr statistic is retired (sign-unstable). It is no longer "consistent with both outcomes" — it explains R and says nothing about C. Promote to corpus as D3' below, with that reduced claim.
- **H3 (net-sell ≥50 at an unmoved best bid, ≥500ms): REFUTED — premise dead.** Zero qualifying episodes at X=50 in either file under two independent run definitions; empirical max net absorption while a level holds is 16–30 contracts. The book resolves before the spec's floor can accumulate. Any re-thresholded variant is a new hypothesis requiring fresh pre-registration.

## 4. Final pre-registered test plan for the corpus

**Trigger:** existing recorder definition. **sweep_end** = extreme aggressor-side print price in [t_start, t_end]. **Outcome label (unchanged):** REVERSAL iff price recovers ≥50% of (sweep_end − extension low) within 60s of the low without printing ≥8 ticks below the low; else CONTINUATION. (R PASS 298%, C FAIL 40%.)

Pre-registered discriminators — exactly three, thresholds frozen now:

- **T1 (from D2), evaluated at t_end:** causal percentile of the largest print in [t_start, t_end] vs all same-side prints in trailing min(5 min, session-to-date), requiring ≥30s of history else abstain. REVERSAL-prone if top-fraction ≤0.05% AND sweep throughput <15 c/ms; CONTINUATION-prone if top-fraction ≥0.08% AND throughput ≥15 c/ms; else abstain. (R: 0.027%/9.5 → reversal; C: 0.091%/21.1 → continuation.)
- **T2 (from H2/D3, pinned convention), evaluated at first-touch of the 16th distinct level below sweep_end, cap t_end+5s:** per-level sell volume counted only up to evaluation time, levels in first-touch order; ratio = mean(most recent half)/mean(first half). REVERSAL-prone ≤0.5; CONTINUATION-prone ≥1.0; else abstain. Fewer than 8 levels traded by t_end+5s → abstain. (R: 0.20 → reversal; C: 0.87 → abstain.)
- **T3 (from D4), evaluated at t_end+5s:** REVERSAL-prone iff delta(1,2s] ≥ 0 AND delta(2,5s] ≥ −250; CONTINUATION-prone iff delta(1,2s] ≤ −200. (R: +43/−195 → reversal; C: −396 → continuation.)

**Benchmark (not a discriminator):** D5 at t_end+10s as stated previously; any of T1–T3 is only interesting if it matches or beats D5's accuracy at a strictly earlier horizon.

**Independence requirement (new, mandatory):** an event is admissible only if it is from a different session than the previous admitted event, OR ≥30 wall-clock minutes after the previous event's outcome window closed with the intervening tape recorded. Chained same-leg events (this pair's defect) are excluded or down-weighted to one sample per arc.

**Sample size and decision rule:** minimum **30 admissible events with ≥10 in the minority outcome class** (extend collection until both floors are met). Per discriminator: one-sided binomial test of classification accuracy vs 0.5 on non-abstained events, Bonferroni α = 0.05/3 → at n=30 classified this requires **≥22/30 correct** (p≈0.008). Abstain rate >50% on any discriminator = fail for that discriminator regardless of accuracy. No threshold may be revised mid-corpus; a revised threshold restarts that discriminator's count.

## 5. What n=2 cannot tell us — explicitly

1. **Whether two mechanism classes exist at all.** R and C are consecutive samples from one continuous ~150-pt intraday decline, separated by 408.6 unrecorded seconds in which R's entire reversal was erased. "Exhausted stop-run vs ongoing supply process" and "same liquidation arc sampled before and after its failed bounce" are indistinguishable on this data. Only independent events can separate them.
2. **Base rates.** We have one reversal and one continuation by construction of the pair. Nothing here estimates how often sweeps reverse, so nothing here estimates precision/expected value of any rule.
3. **Whether any threshold generalizes.** Every number in T1–T3 (0.05%, 15 c/ms, 0.5, 1.0, −200, −250, 16 levels) was chosen looking at these two events. They are calibration artifacts until the corpus scores them; the −250 clause in T3 exists specifically to admit R and is the most suspect single constant in the plan.
4. **Anything about session/context variance:** both events are 09:33–09:43 ET on one Playback date (2026-07-27), one instrument, one volatility regime. Time-of-day, news, and regime effects are perfectly confounded with everything.
5. **Whether abstains are informative.** T2 abstained on the one continuation we have; whether abstention correlates with outcome is unknowable at n=2.
6. **Live viability.** All horizons are computed on recorded data with perfect clocks; nothing here measures feed latency, and D2's percentile requires ≥30s of session history the recorder must now retain.

**Files:** adjudication `scratchpad/adjudicate_final.py` (full path above; run output in this session's log); prior verification `scratchpad/lead_verify.py`; refuter scripts `scratchpad/attack.py`–`attack4.py` (note: `attack4.py` contains the whole-file accumulation bug documented in §0.2); raw events `scratchpad/analysis/event.json`, `scratchpad/analysis2/event.json`; prior single-event verdict `scratchpad/analysis/final_verdict_e1.md`.