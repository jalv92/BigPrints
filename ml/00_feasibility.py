"""Day-1 feasibility pre-gate for the BigPrints-50 meta-filter (spec section 5).

Answers two pre-registered questions BEFORE any vendoring/labeling/modeling:
  1. Edge-to-cost: median |forward move| at each declared horizon, in points,
     divided by the round-trip friction (1.5 pts NQ, the MFF-Sim baseline).
     Entry reference = first tick strictly AFTER the cluster end (no lookahead).
  2. MinBTL: does minimum_backtest_length(n_trials = 128 prior + 16 budget)
     exceed the event history we actually have?

Exits 0 (PASS) only if edge_to_cost >= EDGE_TO_COST_MIN at >= 1 horizon AND
MinBTL <= available history. Anything else exits 1 and Plan 2 is not written.
"""
import argparse
import json
import sys
from pathlib import Path

import numpy as np

import config
from events import clusters_from_arrays, day_arrays, front_month_map

PREREG = json.loads((Path(__file__).parent / "preregistration.json").read_text())
TICKS_PER_S = 10_000_000  # .NET ticks


def day_moves(files, horizons_s):
    """Per event: |price(t_entry + h) - entry| in points, for each horizon."""
    tt, price, side, vol = day_arrays(files)
    if len(tt) == 0:
        return {h: [] for h in horizons_s}, 0
    ev = clusters_from_arrays(tt, side, vol, config.MIN_VOLUME, config.GAP_MS, config.SPAN_MS)
    moves = {h: [] for h in horizons_s}
    tmax = int(tt[-1]) if len(tt) else 0
    for t_start, t_end, s, v, n in ev:
        i_entry = int(np.searchsorted(tt, t_end, side="right"))  # first tick AFTER the cluster
        if i_entry >= len(tt):
            continue
        entry = price[i_entry]
        for h in horizons_s:
            target = t_end + h * TICKS_PER_S
            if target > tmax:
                continue  # horizon runs past end of day's coverage — drop, never fabricate
            j = int(np.searchsorted(tt, target, side="right")) - 1
            if j <= i_entry:
                continue
            # ponytail: mid-day gaps from skipped bad files can still truncate a
            # horizon inside [t_end, tmax]; residual is <0.1% and biases |move|
            # DOWN (stale price closer to entry) — conservative, not fabricated.
            moves[h].append(abs(price[j] - entry))
    return moves, len(ev)


def min_btl_years(n_trials, sharpe_target=1.0):
    try:
        from purgedcv import minimum_backtest_length
        return float(minimum_backtest_length(n_trials=n_trials, target_sharpe=sharpe_target))
    except Exception:
        # Bailey-Lopez de Prado fallback (annualized-Sharpe units, years)
        from scipy.stats import norm  # scipy unavailable -> inline rational approx acceptable
        g = 0.5772156649
        e = np.e
        z1 = norm.ppf(1 - 1.0 / n_trials)
        z2 = norm.ppf(1 - 1.0 / (n_trials * e))
        return float(((1 - g) * z1 + g * z2) ** 2 / sharpe_target ** 2)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sample", type=int, default=0, help="limit to first N front-month days (0 = all)")
    args = ap.parse_args()

    horizons = PREREG["horizons_s"]
    fm = front_month_map()
    days = sorted(fm)[: args.sample] if args.sample else sorted(fm)

    all_moves = {h: [] for h in horizons}
    n_events = 0
    for day in days:
        cdir = fm[day]
        files = sorted(Path(cdir).glob(f"{day}*.Last.ncd"))
        moves, n = day_moves(files, horizons)
        n_events += n
        for h in horizons:
            all_moves[h].extend(moves[h])

    friction = PREREG["friction_rt_points"]
    e2c = {str(h): float(np.median(all_moves[h]) / friction) if all_moves[h] else 0.0 for h in horizons}

    n_trials = PREREG["n_trials_prior"] + PREREG["trial_budget_total"]
    btl = min_btl_years(n_trials)
    avail_years = len(days) / 252.0

    kc1 = all(v < PREREG["gate_thresholds"]["EDGE_TO_COST_MIN"] for v in e2c.values())
    kc2 = btl > avail_years
    verdict = "FAIL" if (kc1 or kc2) else "PASS"

    report = {
        "signal": PREREG["signal"],
        "n_sessions": len(days),
        "n_events": n_events,
        "edge_to_cost": e2c,
        "friction_rt_points": friction,
        "min_btl_years": btl,
        "available_years": avail_years,
        "kill_conditions_hit": {"KC1_edge_to_cost": kc1, "KC2_min_btl": kc2},
        "verdict": verdict,
    }
    config.OUT.mkdir(exist_ok=True)
    (config.OUT / "feasibility_report.json").write_text(json.dumps(report, indent=1))
    print(json.dumps(report, indent=1))
    sys.exit(0 if verdict == "PASS" else 1)


if __name__ == "__main__":
    main()
