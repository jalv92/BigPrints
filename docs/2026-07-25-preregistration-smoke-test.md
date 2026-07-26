# BigPrints — pre-registered offline smoke test

**Written 2026-07-25, BEFORE the harness was run even once.**
Any edit to the parameters or thresholds below after a result has been seen
invalidates this document and converts the run from confirmation into
exploration. That is the whole point of writing it first.

---

## Status of this test

This is a **SMOKE TEST, NOT A GATE.** Every day of data it uses is exploration
data — the 2026 NQ tick window has already been mined by rounds 10–11 of the
MFF-Sim hunt plus the OF3 autopsy. A positive result here licenses a
**pre-registered forward confirmation**, nothing more. It cannot fund an
account and it cannot be reported as an edge.

Its job is to **kill cheaply**. Roughly 128 pre-registered trials have already
failed the holdout gate; the prior on this one is low, and the value of running
it is that it costs a day instead of six months of forward data.

**Career trial counter: this is trial #129.** Expected maximum |t| under pure
noise at that trial count is ≈ 2.5. The counter does not reset because the
harness is new.

---

## Hypothesis

Large aggressive sweeps on the NQ tape carry short-horizon directional
information: after a cluster of same-side aggressor prints totalling ≥ 150
contracts within a 150 ms window, price continues in the aggressor's direction
by more than round-trip friction, when held until an opposing sweep of
sufficient dominance reverses the position.

Falsifiable, one-sided. The null is that sweep direction carries no information
net of friction.

---

## Frozen parameters

Ported verbatim from `BigPrintsStrategy.cs` `State.SetDefaults`. **No parameter
may be changed after the first run.**

| Parameter | Value | Source |
|---|---|---|
| MinVolume | 150 contracts (cluster total) | `MinVolume = 150` |
| ClusterMilliseconds | 150 ms max gap between same-side prints | `ClusterMilliseconds = 150` |
| MaxClusterSpanMs | 1500 ms hard cap on total cluster span | `const int MaxClusterSpanMs = 1500` |
| SessionStart / SessionEnd | 09:30 / 15:55 ET | `SessionStart = 930`, `SessionEnd = 1555` |
| DailyProfitTargetUSD | 500 | `DailyProfitTargetUSD = 500` |
| DailyLossLimitUSD | 300 | `DailyLossLimitUSD = 300` |
| AggressionWindowSeconds | 180 | `AggressionWindowSeconds = 180` |
| ReversalDominanceRatio | 1.5 | `ReversalDominanceRatio = 1.5` |
| CooldownMinutes | 5 (flat entries only; reversals exempt) | `CooldownMinutes = 5` |
| Contracts | 1 | `Contracts = 1` |
| Stop / target | **none** — native mode has no per-trade stop | `StopLossTicks = 0`, `ProfitTargetTicks = 0` |

Mechanics that must be reproduced exactly:

- Aggressor: `price >= ask` → buy, `price <= bid` → sell, strictly inside the
  spread → **skipped, no aggressor**.
- Cluster finalizes on a tape-clock timeout: **any** event more than
  ClusterMilliseconds after the cluster's last print, not just the next Last.
- Governor P&L is **realized + unrealized** (mark-to-market), checked
  continuously. Hitting either limit flattens and locks out for the rest of
  the session.
- The aggression ledger records **every** finalized cluster ≥ MinVolume,
  including ones not traded (lockout, session, cooldown), because the live
  strategy does the same.
- Exits are only: opposing dominant sweep (reversal), session end, or governor
  lockout.

---

## Data

- **Instrument: NQ only.** BigPrints was built for ES, but there are 26 ES
  replay days and **zero** ES tick days, versus 81 NQ tick days. ES cannot be
  tested at any useful N, so the port is evaluated off-design and this is
  recorded as a known deviation, not hidden.
- Source: `Documents/NinjaTrader 8/db/tick/NQ {03,06,09}-26/*.Last.ncd`
- Span: 2026-02-24 → 2026-07-20, **81 trading dates**, ~31.2 M ticks.
- Point value $20/pt, tick $5, tick size 0.25.

## Friction

Commission fixed at **1 tick ($5) per round trip**. Entry slippage is **swept**,
not assumed, over {0, 1, 2, 3, 5} ticks, because the measured sensitivity of
this strategy family to slippage is large enough to decide the result. The
headline number is quoted at **2 ticks**, the midpoint of the plausible range
for a 1-lot market order chasing a 150-contract sweep on a book whose mean
depth-event size is 2.1 contracts.

---

## Metrics

Reported on **daily aggregated P&L**, not per-trade. `round2.py:69` and
`round3.py:348` both compute a per-trade t-statistic that assumes independent
observations; intraday trades are not independent. The measured intraclass
correlation on a proxy sweep rule was ρ = 0.014 (design effect 1.09×), but the
gate statistic is the daily one regardless, because it absorbs whatever ρ turns
out to be here.

- N trades, trades/day and its dispersion
- Mean net P&L per trade (ticks and USD), at each slippage level
- **t on daily aggregated P&L** — the gate statistic
- t per trade — reported for comparison only, never gated on
- Measured ρ (ICC of per-trade P&L grouped by day)
- Win rate, profit factor, mean/median hold time
- Long/short split — a one-sided sample is a red flag, the existing audit
  already saw 5 of 5 signals SHORT

---

## Kill thresholds — pre-committed

**ARCHIVE the strategy if ANY of these holds at 2 ticks of slippage:**

1. **N < 150 trades** over the 81 days → the corpus cannot power any
   conclusion; the strategy is untestable on the data that exists, which is
   itself a decisive result.
2. **Mean net P&L per trade ≤ 0** → no edge before any statistical question is
   asked.
3. **t on daily P&L < 1.0** → too weak to justify spending forward days, which
   are the only genuinely scarce resource.

**CONTINUE to a pre-registered forward confirmation only if ALL hold:**

- N ≥ 150, mean net > 0, and **t_daily ≥ 1.5** at 2 ticks slippage
- Still positive at **3 ticks** of slippage (robustness, not significance)
- Both halves of the date range positive
- Not driven by one side: the minority direction is ≥ 25% of trades

Anything between "kill" and "continue" — for example t_daily between 1.0 and
1.5 — is **inconclusive and stops here anyway**. Inconclusive is not a licence
to tune; it is a licence to stop. Re-running with a changed parameter is a new
trial and increments the counter.

---

## What this test explicitly cannot establish

- That BigPrints works **on ES**, which is what it was built for.
- Anything about fill quality: entry slippage is swept as an assumption, not
  measured against a real book.
- Anything that survives out of sample. All 81 days are exploration.
