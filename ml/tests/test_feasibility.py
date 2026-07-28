"""Smoke: pre-gate runs end-to-end on a 5-day sample and emits a well-formed report."""
import json, subprocess, sys
from pathlib import Path

ml = Path(__file__).resolve().parents[1]
r = subprocess.run([str(ml / ".venv/bin/python"), str(ml / "00_feasibility.py"), "--sample", "5"],
                   capture_output=True, text=True)
print(r.stdout, r.stderr[-2000:], sep="\n")
rep = json.loads((ml / "out/feasibility_report.json").read_text())
for h in ["60", "120", "300", "600"]:
    assert h in rep["edge_to_cost"], f"missing horizon {h}"
    assert rep["edge_to_cost"][h] > 0
assert rep["n_events"] > 0
assert rep["verdict"] in ("PASS", "FAIL")
assert r.returncode == (0 if rep["verdict"] == "PASS" else 1)
print("test_feasibility OK", rep["verdict"], rep["edge_to_cost"])
