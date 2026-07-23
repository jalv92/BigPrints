# BigPrints AI Advisor — Design Spec

**Date:** 2026-07-23
**Status:** Approved design, pending implementation plan
**Owner:** Javier (manual trading decision support)

## Goal

Add an on-demand AI analysis layer to the BigPrints indicator. When the trader clicks an
**Analyze** button on the chart, the indicator captures the full market context of that
instant (L2 ladder, recent big-print clusters, recent bars, session stats, chart
screenshot) and runs a multi-agent analysis on `claude-sonnet-5` that returns a single
actionable verdict: **BUY / SELL / HOLD**, with confidence, entry, stop-loss, take-profit,
and a short rationale — drawn on the chart.

This is **decision support for manual trading**. The AI never places orders.

## Non-goals (v1)

- No automatic triggering (no per-print or zone-detection triggers). The manual button
  must prove the analyses are worth acting on first; auto-trigger becomes a parameter later.
- No integration with `BigPrintsStrategy.cs`. The JSON analysis log (below) is the natural
  bridge if that ever happens.
- No DOM-window screenshot (not capturable from an indicator; the ladder goes as text,
  which is more precise for the model anyway).
- No prompt-caching optimization. Click volume is low and manual; the stable prompt
  prefixes are mostly below Sonnet 5's 2048-token cacheable minimum. Revisit only if an
  auto-trigger mode ever makes volume high.

## Architecture

```
[Analyze button click — UI thread]
        |
   Capture context snapshot:
   - L2 ladder (10 levels/side, text)
   - last ~20 big-print clusters
   - last ~30 bars OHLCV
   - session stats (cum delta, Hi/Lo, bid/ask)
   - chart screenshot (PNG base64)
        |
   Task.Run — off the UI and market-data threads
        |
   +----------------+----------------+
   |                |                |
[Lens: Order    [Lens:          [Lens:
 Flow]           Structure]      Risk]        3 parallel calls, same context,
   |                |                |         different system prompts
   +----------------+----------------+
                    |
             [Orchestrator]                    1 call, reads the 3 reports,
                    |                          structured output (guaranteed JSON)
   {decision, confidence, entry, stop,
    target, rationale}
                    |
   Dispatcher.InvokeAsync → draw panel + SL/TP lines + sound
                    |
   Append full record to JSON log
```

### Why this topology

A single big print means nothing in isolation — absorption, icebergs, and exhaustion are
only readable in the *sequence* plus the standing liquidity. So every analyst sees the
same complete context; the multi-agent value comes from **perspective diversity**
(independent opinions resolved by an orchestrator), not from sharding prints across
agents. Chosen over (a) a single-call analyst (cheaper but no second opinion) and (b)
one-agent-per-print (N redundant calls per burst; each agent needs the full context
anyway).

## 1. Trigger & chart UX

- **Analyze button** in the chart toolbar (WPF `Button` added via `ChartControl.Dispatcher`,
  standard NT8 pattern). Disabled while an analysis is in flight (prevents double spend);
  shows elapsed seconds while running ("Analyzing… 12s").
- **Result panel**: text block in a chart corner —
  `BUY (verde) / SELL (rojo) / HOLD (gris)`, confidence 0-100, 2-3 rationale lines,
  and the three levels. Redrawn per analysis.
- **Level lines**: horizontal lines for Entry / SL / TP (distinct styles), removed and
  redrawn on each new analysis. Not drawn for HOLD.
- **Completion sound** (optional): reuses the existing winmm P/Invoke path — never
  NT8's `PlaySound()` (verified use-after-free crash, see BigPrints.cs header).
- **JSON log**: every analysis appends one JSON line to
  `Documents/NinjaTrader 8/BigPrintsAI/analyses.jsonl` — timestamp, full context sent,
  lens reports, final verdict, token usage. This is the audit trail to score the
  advisor's hit rate before trusting it further, and the future bridge to the strategy.

## 2. Context capture (at click time, on the UI thread)

| Piece | Source | Notes |
|---|---|---|
| L2 ladder | **New**: `OnMarketDepth` book | Two `List<LadderRow>` (bid/ask) indexed by `e.Position` (NOT price-keyed) — the pattern from NinjaTrader's own `SampleLevel2Book.cs`. All mutations and reads locked on `Instrument.SyncMarketDepth` (the platform's own sanctioned lock). Serialized as `ASK price x size` / `BID price x size` lines, 10 levels/side (param). |
| Recent clusters | **New**: bounded buffer | `FinalizeCluster` already detects them; add a bounded queue (last ~20, param) of `{time, side, price, volume}` records. The model reads absorption/response by comparing cluster prices/times against current price — no extra tracking needed. |
| Recent bars | Chart's own series | Last ~30 (param) OHLCV via **absolute indexers** (`Bars.GetClose(idx)` etc.) — the `barsAgo` indexer is only safe inside market-data events, and capture runs on the UI thread. Guard `CurrentBar >= 0`, try/catch like the existing `bestEffort` path. |
| Session stats | **New**: accumulated in `OnMarketData` | Cumulative delta from the already-classified prints (buy volume − sell volume), session high/low, current bid/ask, current time. |
| Chart screenshot | **New**: `Chart.GetScreenshot(ShareScreenshotType.Chart)` | NT8-internal API (same mechanism as the Share feature). Button click already runs on the UI thread → direct call, no dispatcher hop. `Freeze()` the bitmap, PNG-encode + base64 inside `Task.Run`. Caveats: works only when the chart tab is active (always true on a manual click); undocumented API — if it returns null or throws, **degrade gracefully: send the analysis without image and note it in the panel**. Documented fallback if a future NT8 build removes it: official Share Service (temp-file based) — not implemented in v1. |
| Base prompt | Indicator parameter | Multiline string prepended to every system prompt: account size, max risk per trade, instrument notes. Sensible English default. All prompts to the model are English (workspace rule). |

If the data feed provides no L2 (book empty), the payload says `L2 data unavailable` and
the analysis proceeds — the model is told what it does and doesn't have.

## 3. AI pipeline

- **Model:** `claude-sonnet-5` for all four calls (parameter `ModelId`, default
  `claude-sonnet-5`). Adaptive thinking is the model default — the `thinking` field is
  omitted. No `temperature`/`top_p`/`top_k` (rejected by the model).
- **Lenses** (parallel, same user content, different system prompts, plain-text reports):
  - *Order Flow*: aggressor behavior, absorption, icebergs (repeated prints at a level
    with the ladder refilling), exhaustion, whether price responds to the big prints.
  - *Structure*: trend, session context, where price sits relative to swing points and
    the session range; what the visual chart shows.
  - *Risk*: is there a trade with acceptable R:R here at all; where is the invalidation;
    explicit permission to veto ("no trade").
- **Orchestrator**: receives the three reports plus a compact context summary. Uses
  **structured outputs** (`output_config.format` with a JSON schema) so the reply is
  guaranteed-parseable:

  ```json
  {
    "decision":   "buy | sell | hold",
    "confidence": 0-100,
    "entry":      number | null,
    "stop":       number | null,
    "target":     number | null,
    "rationale":  "string (2-4 sentences)"
  }
  ```

  The rationale is written in the language set by the `ResponseLanguage` parameter
  (default English); everything else about the prompts stays English.
- **max_tokens:** 8000 per lens, 6000 for the orchestrator (non-streaming stays well
  under HTTP-timeout territory; adaptive thinking shares this budget).
- **Failure tolerance:** if a lens call fails, the orchestrator runs with the surviving
  reports and the failure is noted; if all three fail, the panel shows the error.
  `stop_reason` is checked before reading content (`max_tokens` → truncated-note,
  `refusal` → shown as error).
- **Cost:** ~26K input + ~6K output tokens per click ≈ **$0.11 at intro pricing**
  ($2/$10 per MTok until 2026-08-31), ≈ $0.17 after. Manual trigger = spend is directly
  user-controlled.

## 4. Technical execution (NT8 / .NET Framework 4.8)

- **No Anthropic C# SDK.** Verified: the SDK's dependency closure (`System.Text.Json`
  ≥10.0.6 etc.) collides with the versions NinjaTrader itself loads and pins via binding
  redirects in `NinjaTrader.exe.config`. Raw **static `HttpClient`** + **Newtonsoft.Json
  v13** (ships inside NT8's `bin/`, zero references to add).
- `ServicePointManager.SecurityProtocol |= Tls12` (not guaranteed default on every
  trader's Windows box) and `DefaultConnectionLimit` raised for the parallel lens calls.
  `HttpClient.Timeout = 180s` (thinking calls run long). One client per AppDomain.
- **Threading (the lessons already paid for in this repo):**
  - Nothing heavy ever runs on the market-data thread. `OnMarketData`/`OnMarketDepth`
    only append to bounded, locked buffers.
  - Button click (UI thread): capture context + screenshot synchronously (cheap), then
    `Task.Run` the entire HTTP pipeline.
  - Results marshaled back with `ChartControl.Dispatcher.InvokeAsync` only to draw.
  - `CancellationTokenSource` created in `State.Configure`, cancelled + disposed in
    `State.Terminated`; in-flight calls abort cleanly when the chart closes.
- **API key:** read at runtime from a plain text file. Parameter `ApiKeyFilePath`,
  default `Documents/NinjaTrader 8/claude_api_key.txt` (NT8 runs on Windows — the repo's
  gitignored `doc/` copy lives in WSL; deployment copies the key to the Documents path).
  The key is **never** hardcoded and **never** an indicator property (properties get
  serialized into workspaces/templates; only the *path* is a property). Key file missing
  or unreadable → clear panel error, no calls made.
- **File layout:** `BigPrints.cs` stays the detector and gains capture/UX code;
  a new `BigPrintsAiClient.cs` (plain class, same Indicators folder, not an `Indicator`)
  owns payload building, the four HTTP calls, and response parsing. Clean seam, both
  compiled by the NT8 editor into the same custom assembly. Compilation validated with
  `nt8c` per workspace hook.

## 5. New indicator parameters

| Parameter | Default | Notes |
|---|---|---|
| `EnableAiAdvisor` | true | Master switch; hides the button when off |
| `ApiKeyFilePath` | `Documents/NinjaTrader 8/claude_api_key.txt` | Path only — never the key |
| `ModelId` | `claude-sonnet-5` | |
| `BasePrompt` | English account-context template | Multiline; account size, risk/trade, instrument |
| `ResponseLanguage` | `English` | Language of the rationale shown on the chart |
| `EnableScreenshot` | true | Off = structured data only |
| `DomLevelsToSend` | 10 | Per side |
| `RecentClustersToSend` | 20 | |
| `BarsToSend` | 30 | |
| `AnalysisSoundFile` | empty (off) | Played via the winmm path on completion |

## 6. Testing plan

- `nt8c` compile check on every edit (existing PostToolUse hook); final validation with
  `nt8c build --custom-dir` staged build (cross-file: two source files now).
- Market Replay session on NQ/ES: verify button, capture (screenshot non-null, ladder
  populated), a full round-trip against the real API, panel + lines drawn, log written.
- Failure drills: missing key file, airplane-mode (HTTP failure), `EnableScreenshot=false`,
  chart closed mid-analysis (cancellation), L2-less data series.
- Review pass by `trading-code-reviewer` before deploy (workspace convention).

## 7. Future (explicitly deferred)

- Auto-trigger modes (per-print with cooldown, zone-accumulation detection) as parameters.
- Feeding verdicts to `BigPrintsStrategy.cs` via the JSON log.
- Share-Service screenshot fallback if NT8 ever breaks `Chart.GetScreenshot()`.
- Hit-rate scoring script over `analyses.jsonl`.
