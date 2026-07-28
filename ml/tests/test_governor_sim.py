"""With a fixed seed, the governor must not increase mean breach rate across the grid."""
import subprocess, sys
from pathlib import Path
import pandas as pd

ml = Path(__file__).resolve().parents[1]
r = subprocess.run([str(ml / ".venv/bin/python"), str(ml / "governor_sim.py"), "--paths", "300", "--seed", "7"],
                   capture_output=True, text=True)
print(r.stdout[-2000:], r.stderr[-1000:], sep="\n")
assert r.returncode == 0
df = pd.read_csv(ml / "out/governor_sim.csv")
assert len(df) >= 148, f"expected >=148 variant rows, got {len(df)}"
assert df["breach_gov"].mean() <= df["breach_raw"].mean() + 1e-9, "governor increased mean breach rate"
print("test_governor_sim OK",
      f"breach {df['breach_raw'].mean():.3f}->{df['breach_gov'].mean():.3f}",
      f"pass {df['pass_raw'].mean():.3f}->{df['pass_gov'].mean():.3f}")
