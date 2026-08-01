#!/usr/bin/env python3
"""Validate BigPrints discriminator_log.jsonl and print the corpus scoreboard.

Usage: python3 tools/validate_discriminator_log.py <discriminator_log.jsonl>
Exit 0 = schema valid.

Handles both v2 records (T1/T2/T3 vote engine) and v3 records ("v": 3 —
context-first NQ registration: ctx/uni fields, decision path, MAE-aware
outcomes). Scoring is per instrument: point/contract thresholds do not
transfer across NQ/MNQ (audit 2026-08-01).
"""
import json
import sys
from collections import Counter, defaultdict


TRIGGER_KEYS_V2 = {"type", "ts", "mode", "side", "volume", "max_print", "span_ms", "n_prints",
                   "sweep_extreme", "t1", "t2", "t3", "action"}
TRIGGER_KEYS_V3 = TRIGGER_KEYS_V2 | {"ctx", "uni", "decision"}
OUTCOME_KEYS = {"type", "trigger_ts", "label", "extension_ticks", "recovery_pct",
                "low_price", "resolved_ts"}
VERDICTS = ("Abstain", "Reversal", "Continuation")


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
                keys = TRIGGER_KEYS_V3 if r.get("v") == 3 else TRIGGER_KEYS_V2
                missing = keys - set(r)
                if missing:
                    # Malformed lines are reported and EXCLUDED from scoring — one truncated
                    # write must not crash the scoreboard for the whole corpus.
                    print("  FAIL line %d: trigger missing %s (excluded from scoring)" % (i, sorted(missing)))
                    errors += 1
                    continue
                for t in ("t1", "t2", "t3"):
                    if r.get(t, {}).get("verdict") not in VERDICTS:
                        print("  FAIL line %d: bad %s.verdict" % (i, t))
                        errors += 1
                if r.get("v") == 3:
                    if r.get("ctx", {}).get("verdict") not in ("None", "BalanceSweep", "VirginExtension"):
                        print("  FAIL line %d: bad ctx.verdict" % i)
                        errors += 1
                    if r.get("decision") not in VERDICTS:
                        print("  FAIL line %d: bad decision" % i)
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

    by_instr = defaultdict(list)
    for ts, t in triggers.items():
        if ts in outcomes:
            by_instr[t.get("instrument", "?")].append((t, outcomes[ts]))

    for instr, matched in sorted(by_instr.items()):
        print("\n== %s (%d scored) ==" % (instr, len(matched)))

        # Legacy component scoreboard (v2 + v3 logged-only values).
        for name in ("t1", "t2", "t3"):
            tally = Counter()
            for t, o in matched:
                v = t[name]["verdict"]
                if v != "Abstain" and o["label"] in ("REVERSAL", "CONTINUATION"):
                    tally["correct" if v.upper() == o["label"] else "wrong"] += 1
                elif v == "Abstain":
                    tally["abstain"] += 1
            print("%s: %s" % (name, dict(tally)))

        # v3 paths. Scored only where the outcome label resolved.
        v3 = [(t, o) for t, o in matched if t.get("v") == 3
              and o["label"] in ("REVERSAL", "CONTINUATION")]
        if not v3:
            continue

        def score(rows, want):
            c = Counter()
            for t, o in rows:
                c["correct" if o["label"] == want else "wrong"] += 1
            return dict(c)

        bal_traded = [(t, o) for t, o in v3
                      if t["ctx"]["verdict"] == "BalanceSweep" and t["uni"]["ok"]]
        bal_konly  = [(t, o) for t, o in v3 if t["ctx"].get("k_only")]
        bal_all    = [(t, o) for t, o in v3 if t["ctx"]["verdict"] == "BalanceSweep"]
        rev_traded = [(t, o) for t, o in v3 if t["decision"] == "Reversal"]
        print("v3 CTX+UNI (traded continuation): %s" % score(bal_traded, "CONTINUATION"))
        print("v3 K-only shadow (ctx passed, uni failed): %s" % score(bal_konly, "CONTINUATION"))
        print("v3 CTX-any-balance (both above): %s" % score(bal_all, "CONTINUATION"))
        print("v3 reversal path (virgin + T2): %s" % score(rev_traded, "REVERSAL"))

        # Label-defect guard: a "correct" call with a huge adverse excursion is not a win.
        maes = [(o.get("max_up_ticks"), o.get("max_dn_ticks")) for _, o in v3
                if o.get("max_up_ticks") is not None]
        if maes:
            ups = sorted(u for u, d in maes)
            dns = sorted(d for u, d in maes)
            print("excursions from sweep extreme (ticks): up median %.0f / max %.0f, down median %.0f / max %.0f"
                  % (ups[len(ups) // 2], ups[-1], dns[len(dns) // 2], dns[-1]))

    orphans = set(outcomes) - set(triggers)
    if orphans:
        print("  WARN: %d outcome(s) without a matching trigger ts" % len(orphans))
    return 1 if errors else 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
