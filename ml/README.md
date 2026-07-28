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

Scores the min-of-multipliers governor (`governor_multiplier()`, a Python mirror
of the C# `GovernorSize()` from Tasks 1/3, minus the vol-shock arm — no intraday
bars in a trade-level bootstrap) against the 60 PropSim variants that have both
a `max_dd` and a `profit_target` (88 of 148 variants are funded/live phases
without a target and are skipped as not expressible in this bootstrap). Both
arms replay identical pre-drawn 30-day trade streams per path, so the delta is
a fair comparison, not two independent random draws.

```bash
.venv/bin/python governor_sim.py --paths 2000 --seed 1
```

Full run (`--paths 2000 --seed 1`, 60 variants x 4 win rates x 2 arms x 2000 paths):

| win_rate | breach_raw | breach_gov | Δbreach | pass_raw | pass_gov | Δpass |
|----------|-----------:|-----------:|--------:|---------:|---------:|------:|
| 0.35     | 1.000      | 0.000      | -1.000  | 0.000    | 0.008    | +0.008 |
| 0.40     | 0.994      | 0.000      | -0.994  | 0.006    | 0.034    | +0.028 |
| 0.45     | 0.954      | 0.000      | -0.954  | 0.046    | 0.105    | +0.059 |
| 0.50     | 0.830      | 0.000      | -0.830  | 0.170    | 0.214    | +0.044 |

The governor drives mean breach rate to ~0 across every win-rate tested while
*also* improving pass rate — it isn't just "safe by refusing to trade," it
turns off exposure at the daily-loss/DD boundary early enough that surviving
accounts get more days to reach the profit target.

## Pointers

- Spec: `main-project docs/superpowers/specs/2026-07-28-bigprints50-meta-filter-design.md`
- Plan: `main-project docs/superpowers/plans/2026-07-28-bigprints50-governor-and-pregate.md`
