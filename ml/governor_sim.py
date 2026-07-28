"""Monte-Carlo scoring of the min-of-multipliers governor against PropSim's rule set.

Parametric bootstrap (no validated strategy exists yet, so trades are synthetic):
per day, N ~ Poisson(HORIZONS) trades; each risks R = 2 x daily_budget / sqrt(HORIZONS)
dollars; win = +1.5R (p = win_rate), loss = -R. Governor arms mirrored 1:1 from the
C# GovernorSize() in FVGFlowStrategy.cs / BigPrintsStrategy.cs, EXCEPT the vol-shock
arm (needs intraday bars a trade-level bootstrap does not have).
ponytail: this is a DELTA comparator (gov vs raw under identical draws), not a
PropSim replacement — both arms share the same rule approximations, so the delta
is meaningful even where the absolute rates are approximate.
"""
import argparse
import json
import math
from pathlib import Path

import numpy as np
import pandas as pd

import config

RULES = json.loads((config.MFF_SIM.parent / "PropSim" / "prop_rules.json").read_text())
HORIZONS = 6
MAX_CONSEC = 3
WIN_R = 1.5
SIM_DAYS = 30
WIN_RATES = [0.35, 0.40, 0.45, 0.50]


def clamp01(x):
    return 0.0 if x < 0 else (1.0 if x > 1 else x)


def governor_multiplier(equity, equity_high, dd_remaining, daily_pnl, daily_budget, consec):
    per_trade = 2.0 * daily_budget / math.sqrt(HORIZONS)
    m_dd = clamp01((dd_remaining - (equity_high - equity)) / (3.0 * per_trade))
    m_daily = clamp01((daily_budget + min(daily_pnl, 0.0)) / per_trade)
    m_streak = 0.0 if consec >= MAX_CONSEC else 1.0
    return min(m_dd, m_daily, m_streak)


def draw_outcomes(rng, win_rate):
    """Pre-draw the full 30-day outcome stream (list of per-day win/loss lists) so BOTH
    arms replay the exact same trades — the governor arm just declines some of them."""
    return [[rng.random() < win_rate for _ in range(rng.poisson(HORIZONS))]
            for _ in range(SIM_DAYS)]


def run_path(outcomes, rule, use_governor):
    """One 30-day account path over a pre-drawn outcome stream. Returns (breached, passed)."""
    daily_budget = rule["daily_loss_limit"] or (rule["max_dd"] / 5.0)
    per_trade = 2.0 * daily_budget / math.sqrt(HORIZONS)
    equity = 0.0            # PnL vs start_balance
    hwm = 0.0               # high-water in PnL terms, per hwm_basis
    traded_days = 0
    for day in outcomes:
        daily = 0.0
        halted = False
        consec = 0          # C# resets the streak per session (Task 1/3 Step 3)
        if day:
            traded_days += 1
        for win in day:
            if use_governor:
                m = governor_multiplier(equity, hwm, rule["max_dd"], daily, daily_budget, consec)
                if halted or m < 1.0:      # 1-contract scale: m < 1 floors to 0 (skip)
                    if m <= 0.0:
                        halted = True       # structural zeros stick for the day
                    continue
            pnl = WIN_R * per_trade if win else -per_trade
            equity += pnl
            daily += pnl
            consec = consec + 1 if pnl < 0 else 0
            if rule["breach_basis"] != "eod_close" and equity <= hwm - rule["max_dd"]:
                return True, False          # intraday trailing-DD breach
            if rule["daily_loss_limit"] and daily <= -rule["daily_loss_limit"]:
                break                       # daily limit: day over (soft model)
            if rule["hwm_basis"] != "eod_close":
                hwm = max(hwm, equity)
        if rule["hwm_basis"] == "eod_close":
            hwm = max(hwm, equity)
        if rule["breach_basis"] == "eod_close" and equity <= hwm - rule["max_dd"]:
            return True, False
    passed = equity >= rule["profit_target"] and traded_days >= (rule["min_days"] or 0)
    return False, passed


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--paths", type=int, default=2000)
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()

    rows = []
    for rule in RULES:
        if not rule.get("max_dd") or not rule.get("profit_target"):
            continue  # variant not expressible in this bootstrap (e.g. funded phase w/o target)
        for wr in WIN_RATES:
            rng = np.random.default_rng(args.seed)
            paths = [draw_outcomes(rng, wr) for _ in range(args.paths)]  # shared by both arms
            res = {}
            for gov in (False, True):
                b = p = 0
                for outcomes in paths:
                    breached, passed = run_path(outcomes, rule, gov)
                    b += breached
                    p += passed
                res["gov" if gov else "raw"] = (b / args.paths, p / args.paths)
            rows.append({
                "firm": rule["firm"], "variant": rule["variant"], "phase": rule["phase"],
                "size": rule["size"], "win_rate": wr,
                "breach_raw": res["raw"][0], "breach_gov": res["gov"][0],
                "pass_raw": res["raw"][1], "pass_gov": res["gov"][1],
            })

    df = pd.DataFrame(rows)
    config.OUT.mkdir(exist_ok=True)
    df.to_csv(config.OUT / "governor_sim.csv", index=False)
    print(df.groupby("win_rate")[["breach_raw", "breach_gov", "pass_raw", "pass_gov"]].mean().round(3))
    print(f"{len(df)} rows -> out/governor_sim.csv")


if __name__ == "__main__":
    main()
