"""Shared constants for the BigPrints-50 ML pipeline (Plan 1: pre-gate only).

Spec: main-project docs/superpowers/specs/2026-07-28-bigprints50-meta-filter-design.md
"""
from pathlib import Path

TICK_DB = Path("/mnt/c/Users/javlo/Documents/NinjaTrader 8/db/tick")
MFF_SIM = Path(__file__).resolve().parents[2] / "MFF-Sim"
OUT     = Path(__file__).resolve().parent / "out"

# Frozen BigPrints-50 rule (preregistration.json) — the ONLY declared change vs shipped is MIN_VOLUME.
MIN_VOLUME = 50
GAP_MS     = 150
SPAN_MS    = 1500

# NQ instrument + friction. NEVER derive costs from the .Last.ncd spread field
# (invalid decode — memory ncd-orderflow-data-limits). 1.5 pts RT is the
# established MFF-Sim evaluate() baseline.
POINT_VALUE        = 20.0
TICK_SIZE          = 0.25
FRICTION_RT_POINTS = 1.5


def contract_dirs(symbol: str = "NQ") -> list[Path]:
    """All NT8 tick contract dirs for the symbol, e.g. 'NQ 09-26'."""
    return sorted(d for d in TICK_DB.glob(f"{symbol} [0-9][0-9]-[0-9][0-9]") if d.is_dir())
