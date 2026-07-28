"""Checks the frozen scan against the numbers measured in the 2026-07-28 audit
(memory ncd-orderflow-data-limits): NQ 09-26, the 10 audited calendar days,
clusters >= 150 per day == [0, 4, 1, 6, 9, 8, 14, 0, 3, 10]; mean >= 50 ~ 122/day.

Day list is pinned (not "first 10 sorted") because a free NT8 tick download can
extend the NQ 09-26 directory backwards at any time, shifting what "first 10" means.
"""
import sys, collections
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import config
from events import day_arrays, clusters_from_arrays

base = config.TICK_DB / "NQ 09-26"
days = collections.defaultdict(list)
for f in sorted(base.glob("*.Last.ncd")):
    days[f.name[:8]].append(f)

AUDIT_DAYS = ["20260611", "20260612", "20260614", "20260615", "20260616",
              "20260617", "20260618", "20260619", "20260621", "20260622"]
for d in AUDIT_DAYS:
    assert d in days, f"audited day {d} missing from NQ 09-26 tick dir (found {sorted(days)})"

audit_150 = [0, 4, 1, 6, 9, 8, 14, 0, 3, 10]
got_150, got_50 = [], []
for d in AUDIT_DAYS:
    tt, price, side, vol = day_arrays(days[d])
    got_150.append(len(clusters_from_arrays(tt, side, vol, 150, config.GAP_MS, config.SPAN_MS)))
    got_50.append(len(clusters_from_arrays(tt, side, vol, 50, config.GAP_MS, config.SPAN_MS)))

assert got_150 == audit_150, f"minvol=150 per-day mismatch vs audit: {got_150}"
mean50 = sum(got_50) / len(got_50)
assert 100 <= mean50 <= 145, f"minvol=50 mean/day {mean50:.1f} outside audited ballpark [100,145]"
# determinism
tt, price, side, vol = day_arrays(days[AUDIT_DAYS[3]])
a = clusters_from_arrays(tt, side, vol, 50, config.GAP_MS, config.SPAN_MS)
b = clusters_from_arrays(tt, side, vol, 50, config.GAP_MS, config.SPAN_MS)
assert (a == b).all(), "scan is not deterministic"
print("test_events OK", got_150, f"mean50={mean50:.1f}")
