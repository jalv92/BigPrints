#!/usr/bin/env python3
"""Validate BigPrints discriminator_log.jsonl and print the corpus scoreboard.

Usage: python3 tools/validate_discriminator_log.py <discriminator_log.jsonl>
Exit 0 = schema valid.
"""
import json
import sys
from collections import Counter


TRIGGER_KEYS = {"type", "ts", "mode", "side", "volume", "max_print", "span_ms", "n_prints",
                "sweep_extreme", "t1", "t2", "t3", "votes", "action"}
OUTCOME_KEYS = {"type", "trigger_ts", "label", "extension_ticks", "recovery_pct",
                "low_price", "resolved_ts"}


def main(path):
    errors = 0
    triggers, outcomes = {}, {}
    with open(path) as f:
        for i, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except ValueError:
                print("  FAIL line %d: not JSON" % i)
                errors += 1
                continue
            if r.get("type") == "trigger":
                missing = TRIGGER_KEYS - set(r)
                if missing:
                    print("  FAIL line %d: trigger missing %s" % (i, sorted(missing)))
                    errors += 1
                for t in ("t1", "t2", "t3"):
                    if r.get(t, {}).get("verdict") not in ("Abstain", "Reversal", "Continuation"):
                        print("  FAIL line %d: bad %s.verdict" % (i, t))
                        errors += 1
                triggers[r.get("ts")] = r
            elif r.get("type") == "outcome":
                missing = OUTCOME_KEYS - set(r)
                if missing:
                    print("  FAIL line %d: outcome missing %s" % (i, sorted(missing)))
                    errors += 1
                if r.get("label") not in ("REVERSAL", "CONTINUATION", "UNRESOLVED", "UNRESOLVED_SUPERSEDED"):
                    print("  FAIL line %d: bad outcome label %r" % (i, r.get("label")))
                    errors += 1
                outcomes[r.get("trigger_ts")] = r
            else:
                print("  FAIL line %d: unknown type %r" % (i, r.get("type")))
                errors += 1

    print("%d triggers, %d outcomes" % (len(triggers), len(outcomes)))
    print("actions: %s" % dict(Counter(t["action"] for t in triggers.values())))
    print("labels:  %s" % dict(Counter(o["label"] for o in outcomes.values())))

    matched = [(t, outcomes[ts]) for ts, t in triggers.items() if ts in outcomes]
    for name in ("t1", "t2", "t3"):
        tally = Counter()
        for t, o in matched:
            v = t[name]["verdict"]
            if v != "Abstain" and o["label"] in ("REVERSAL", "CONTINUATION"):
                tally["correct" if v.upper() == o["label"] else "wrong"] += 1
            elif v == "Abstain":
                tally["abstain"] += 1
        print("%s: %s" % (name, dict(tally)))

    orphans = set(outcomes) - set(triggers)
    if orphans:
        print("  WARN: %d outcome(s) without a matching trigger ts" % len(orphans))
    return 1 if errors else 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
