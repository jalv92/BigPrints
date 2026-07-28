"""Frozen BigPrints-50 cluster scan over NT8 .Last.ncd tick files.

Rule (preregistration.json, mirrors BigPrints.cs): consecutive prints of the SAME
aggressor side, inter-print gap <= GAP_MS, total span <= SPAN_MS, cluster volume
>= MIN_VOLUME. Continuation direction. The ONLY declared change vs the shipped
indicator is MIN_VOLUME 150 -> 50.

Scan logic derived from the 2026-07-28 audit probe (session scratchpad
clustercount.py); parser is MFF-Sim's ncd_parse.read_ticks (format credit:
bboyle1234/NTDFileReader).
"""
import json
import sys
from pathlib import Path

import numpy as np
import pandas as pd

import config

sys.path.insert(0, str(config.MFF_SIM))
from ncd_parse import read_ticks  # noqa: E402

TICKS_PER_MS = 10_000  # .NET ticks (100 ns)


def day_arrays(files):
    """Parse one day's hourly .Last.ncd files into flat numpy arrays.

    Returns (tt, price, side, vol); side is +1 at-ask / -1 at-bid / 0 unclassifiable.
    """
    tts, prices, sides, vols = [], [], [], []
    for f in sorted(files):
        try:
            recs = list(read_ticks(f))
        except Exception:
            continue  # 16/133 known-bad files in the db — skip, never fabricate
        for tt, price, boff, aoff, v in recs:
            tts.append(tt)
            prices.append(price)
            sides.append(1 if aoff == 0 else (-1 if boff == 0 else 0))
            vols.append(v)
    return (np.asarray(tts, dtype=np.int64), np.asarray(prices),
            np.asarray(sides, dtype=np.int8), np.asarray(vols, dtype=np.int64))


def clusters_from_arrays(tt, side, vol, min_volume, gap_ms, span_ms):
    """Rows: (t_start, t_end, side, vol, n_prints). Unclassifiable prints are skipped."""
    gap_t, span_t = gap_ms * TICKS_PER_MS, span_ms * TICKS_PER_MS
    out = []
    open_ = False
    c_side = c_vol = c_np = t_last = t_start = 0
    for i in range(len(tt)):
        s = side[i]
        if s == 0:
            continue
        t = tt[i]
        v = vol[i]
        if open_ and s == c_side and (t - t_last) <= gap_t and (t - t_start) <= span_t:
            c_vol += v; c_np += 1; t_last = t
            continue
        if open_ and c_vol >= min_volume:
            out.append((t_start, t_last, c_side, c_vol, c_np))
        open_, c_side, c_vol, c_np, t_last, t_start = True, s, v, 1, t, t
    if open_ and c_vol >= min_volume:
        out.append((t_start, t_last, c_side, c_vol, c_np))
    return np.asarray(out, dtype=np.int64).reshape(-1, 5)


def front_month_map(dirs=None, cache=True):
    """day 'YYYYMMDD' -> contract dir with the max summed tick volume that day.

    Cache is keyed on the total .Last.ncd file count across dirs (cheap glob, no
    parsing) so a growing tick download invalidates it automatically.
    """
    dirs = dirs or config.contract_dirs()
    n_files = sum(len(list(d.glob("*.Last.ncd"))) for d in dirs)
    cache_path = config.OUT / "front_month_map.json"
    if cache and cache_path.exists():
        cached = json.loads(cache_path.read_text())
        if cached.get("n_files") == n_files:
            return {d: Path(p) for d, p in cached["map"].items()}
    day_vol = {}
    for cdir in dirs:
        for f in sorted(cdir.glob("*.Last.ncd")):
            day = f.name[:8]
            try:
                v = sum(r[4] for r in read_ticks(f))
            except Exception:
                continue  # 16/133 known-bad files in the db — skip, never fabricate
            key = (day, str(cdir))
            day_vol[key] = day_vol.get(key, 0) + v
    best = {}
    for (day, cdir), v in day_vol.items():
        if day not in best or v > best[day][1]:
            best[day] = (cdir, v)
    result = {day: p for day, (p, _) in sorted(best.items())}
    config.OUT.mkdir(exist_ok=True)
    cache_path.write_text(json.dumps({"n_files": n_files, "map": result}, indent=1))
    return {d: Path(p) for d, p in result.items()}


def build_events():
    """Full scan -> out/events_minvol50.pkl (one row per event, front-month days only)."""
    fm = front_month_map()
    rows = []
    for day, cdir in fm.items():
        files = sorted(cdir.glob(f"{day}*.Last.ncd"))
        tt, price, side, vol = day_arrays(files)
        ev = clusters_from_arrays(tt, side, vol, config.MIN_VOLUME, config.GAP_MS, config.SPAN_MS)
        for t_start, t_end, s, v, n in ev:
            rows.append((t_start, t_end, int(s), int(v), int(n), day, cdir.name))
    df = pd.DataFrame(rows, columns=["t_start", "t_end", "side", "vol", "n_prints", "day", "contract"])
    config.OUT.mkdir(exist_ok=True)
    df.to_pickle(config.OUT / "events_minvol50.pkl")
    print(f"{len(df)} events over {df['day'].nunique()} front-month days -> out/events_minvol50.pkl")
    return df


if __name__ == "__main__":
    build_events()
