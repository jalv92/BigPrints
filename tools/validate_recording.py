#!/usr/bin/env python3
"""Validate a BigPrints event-recorder JSON and print a summary.

Usage: python3 tools/validate_recording.py <recording.json> [more.json ...]
Exit code 0 = all files valid.
"""
import json
import sys


def fail(msg):
    print("  FAIL: " + msg)
    return 1


def validate(path):
    print(path)
    errors = 0
    with open(path) as f:
        d = json.load(f)

    for key in ("meta", "tape", "inside", "book"):
        if key not in d:
            errors += fail("missing top-level key '%s'" % key)
    if errors:
        return errors

    meta = d["meta"]
    for key in ("instrument", "t0", "trigger", "other_clusters", "bars", "session", "partial"):
        if key not in meta:
            errors += fail("missing meta.%s" % key)
    trig = meta.get("trigger") or {}
    for key in ("side", "volume", "max_print_price", "n_prints", "t_start_ms", "t_end_ms"):
        if key not in trig:
            errors += fail("missing meta.trigger.%s" % key)

    for name, width in (("tape", 6), ("inside", 4)):
        rows = d[name]
        for i, row in enumerate(rows):
            if len(row) != width:
                errors += fail("%s[%d] has %d fields, expected %d" % (name, i, len(row), width))
                break
        ts = [r[0] for r in rows]
        if ts != sorted(ts):
            errors += fail("%s timestamps not monotonic" % name)

    book = d["book"]
    ts = [s["t"] for s in book]
    if ts != sorted(ts):
        errors += fail("book timestamps not monotonic")
    gaps = [b - a for a, b in zip(ts, ts[1:])]
    for i, s in enumerate(book):
        if "bids" not in s or "asks" not in s:
            errors += fail("book[%d] missing bids/asks" % i)
            break

    sides = [r[3] for r in d["tape"]]
    n_inside = sides.count(0)
    span_s = (max(x for x in ([r[0] for r in d["tape"]] + ts)) -
              min(x for x in ([r[0] for r in d["tape"]] + ts))) / 1000.0 if d["tape"] else 0

    print("  ok: trigger [%s] %s %s @ %s (%s prints), mode=%s, partial=%s, capped=%s" % (
        trig.get("type", "sweep"), trig.get("side"), trig.get("volume"),
        trig.get("max_print_price"), trig.get("n_prints"),
        meta.get("recorder_mode", "manual"), meta.get("partial"), meta.get("capped", False)))
    others = meta.get("other_clusters") or []
    if others:
        print("  ok: %d window triggers: %s" % (len(others),
              ", ".join("%s %s%s" % (c.get("type", "sweep"), c.get("side"), c.get("volume")) for c in others)))
    print("  ok: %d tape (%d inside-spread), %d inside updates, %d book snaps, span %.0fs"
          % (len(d["tape"]), n_inside, len(d["inside"]), len(book), span_s))
    if gaps:
        print("  ok: book cadence median %.0f ms" % sorted(gaps)[len(gaps) // 2])

    # Post-trigger price path — quick triage of what happened after the event.
    ref, t_end = trig.get("max_print_price"), trig.get("t_end_ms")
    if ref is not None and t_end is not None:
        post = [(r[0], r[1]) for r in d["tape"] if r[0] >= t_end]
        for h in (30, 60, 120, 180):
            seg = [p for t, p in post if t <= t_end + h * 1000]
            if seg:
                print("  path +%3ds: hi %+g / lo %+g (vs trigger %g)"
                      % (h, max(seg) - ref, min(seg) - ref, ref))
    return errors


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    total = sum(validate(p) for p in sys.argv[1:])
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
