# ml/

Feasibility pre-gate for the BigPrints-50 meta-filter (Plan 1: pre-gate only,
no live model). Before spending trial budget on a real meta-filter, this
checks whether the frozen `bigprints_minvol50_continuation` rule has any
edge over cost, using purged/embargoed cross-validation to guard against
overfitting on a small event count.

## Run the pre-gate

```bash
.venv/bin/python 00_feasibility.py
```

(venv + deps set up per `requirements.txt`; `purgedcv==0.1.2` provides
`minimum_backtest_length`, `deflated_sharpe_ratio`, and
`probability_of_backtest_overfitting`.)

## Files

- `config.py` — shared constants (tick DB path, frozen rule params, friction, `MFF_SIM`/`OUT` paths).
- `preregistration.json` — **frozen** on commit. Kill conditions and gate thresholds (edge-to-cost, PBO, DSR, path Sharpe/maxDD). No threshold in this file is ever adjusted after commit.
- `out/` — pre-gate output (gitignored).

## Governor Monte-Carlo (`governor_sim.py`)

Scores the min-of-multipliers governor (`governor_multiplier()`'s three arms
`m_dd`/`m_daily`/`m_streak` are a 1:1 Python mirror of the C# `GovernorSize()`
from Tasks 1/3, minus the vol-shock arm — no intraday bars in a trade-level
bootstrap) against the 60 PropSim variants that have both a `max_dd` and a
`profit_target` (88 of 148 variants are funded/live phases without a target
and are skipped as not expressible in this bootstrap). Both arms replay
identical pre-drawn 30-day trade streams per path, so the delta is a fair
comparison, not two independent random draws. `daily_budget`'s `max_dd/5`
fallback (when a variant has no `daily_loss_limit`) is a deliberate modeling
choice, not a C# mirror — C# defaults to a flat `500.0`, but that would
mis-size large accounts across this grid.

```bash
.venv/bin/python governor_sim.py --paths 2000 --seed 1
```

**Governor calibration finding (small accounts):** for **21 of the 60**
variants, `dd_arm_ceiling = max_dd / (3 x per_trade)` is already `< 1` at zero
drawdown — the `m_dd` arm floors every 1-contract trade to 0 before a single
trade is placed, so the governor **structurally blocks ALL trading** for
those 21 variants (mostly the smallest-`max_dd` rows: TopStep/MFF/Apex $1-2K
accounts). This is not a sim bug — it's the real C# `GovernorSize()` doing the
same thing at `Contracts=1`. It flags `GovHorizonsPerDay` (6) and the 3x
headroom factor as needing recalibration for small accounts before the
governor ships live on those variants (see the CSV's `dd_arm_ceiling` column
per row). The 39 remaining variants show the intended throttling behavior.

Full run (`--paths 2000 --seed 1`, 60 variants x 4 win rates x 2 arms x 2000 paths):

**All 60 variants:**

| win_rate | breach_raw | breach_gov | Δbreach | pass_raw | pass_gov | Δpass |
|----------|-----------:|-----------:|--------:|---------:|---------:|------:|
| 0.35     | 0.998      | 0.000      | -0.998  | 0.001    | 0.008    | +0.007 |
| 0.40     | 0.984      | 0.000      | -0.984  | 0.012    | 0.035    | +0.023 |
| 0.45     | 0.928      | 0.000      | -0.928  | 0.068    | 0.107    | +0.039 |
| 0.50     | 0.793      | 0.000      | -0.793  | 0.206    | 0.221    | +0.015 |

**39 variants where the governor still trades** (`dd_arm_ceiling >= 1`) — the
throttling story:

| win_rate | breach_raw | breach_gov | Δbreach | pass_raw | pass_gov | Δpass |
|----------|-----------:|-----------:|--------:|---------:|---------:|------:|
| 0.35     | 0.998      | 0.000      | -0.998  | 0.001    | 0.012    | +0.011 |
| 0.40     | 0.983      | 0.000      | -0.983  | 0.011    | 0.053    | +0.042 |
| 0.45     | 0.912      | 0.000      | -0.912  | 0.082    | 0.165    | +0.083 |
| 0.50     | 0.719      | 0.000      | -0.719  | 0.280    | 0.340    | +0.060 |

**21 blocked variants** (`dd_arm_ceiling < 1`) — `pass_gov` is `0.000` at every
win rate by construction (the account never places a trade), so the raw
Δpass there is misleadingly negative-looking if averaged in unlabeled; the
governor is unconditionally breach-safe on these but at the cost of never
attempting the challenge.

Read together: the governor drives breach rate to ~0 everywhere, and where it
actually gets to trade (39/60 variants) it *also* raises pass rate — the
"safe by refusing to trade" objection only applies to the 21 accounts the
governor blocks outright, and that's a governor-calibration gap for small
accounts, not evidence the throttling mechanism doesn't work.

## Pointers

- Spec: `main-project docs/superpowers/specs/2026-07-28-bigprints50-meta-filter-design.md`
- Plan: `main-project docs/superpowers/plans/2026-07-28-bigprints50-governor-and-pregate.md`
