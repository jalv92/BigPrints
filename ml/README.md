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

## Pointers

- Spec: `main-project docs/superpowers/specs/2026-07-28-bigprints50-meta-filter-design.md`
- Plan: `main-project docs/superpowers/plans/2026-07-28-bigprints50-governor-and-pregate.md`
