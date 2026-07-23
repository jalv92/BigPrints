# BigPrints AI Advisor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a manual-trigger AI analysis layer to the BigPrints NT8 indicator: an Analyze button captures L2 ladder + recent big-print clusters + bars + chart screenshot, runs 3 parallel lens agents + an orchestrator on `claude-sonnet-5`, and draws BUY/SELL/HOLD + Entry/SL/TP on the chart, logging every analysis to JSONL.

**Architecture:** `BigPrints.cs` stays the detector and gains the data-capture layer (OnMarketDepth book, cluster memory, session stats) and the chart UX (button, panel, lines). A new plain class `BigPrintsAiClient.cs` owns payload building, the four Messages-API calls (raw HttpClient + Newtonsoft), response parsing, and JSONL logging. Spec: `docs/specs/2026-07-23-ai-advisor-design.md`.

**Tech Stack:** NinjaScript (C#, .NET Framework 4.8), Newtonsoft.Json v13 (ships inside NT8 — zero new references), Anthropic Messages API (`claude-sonnet-5`, structured outputs), `nt8c` for compile validation.

## Global Constraints

- **.NET Framework 4.8, NT8 sandbox.** No NuGet packages. Only assemblies NT8 itself ships. The official Anthropic C# SDK is BANNED (verified assembly-version collision with NT8's pinned `System.Text.Json` et al.). **CORRECTION (post-mortem 2026-07-23):** Newtonsoft.Json ships in NT8's `bin/` but the NinjaScript compiler does NOT reference it by default — it requires a one-time manual reference in the NinjaScript Editor (References… → Add → `C:\Program Files\NinjaTrader 8\bin\Newtonsoft.Json.dll`), mirrored into nt8c's refs cache. The original claim "zero references to add" was wrong; nt8c's first failing build was a true positive that got masked by patching the tool's cache instead of adding the Editor reference.
- **Explicit `using` directives always** — `nt8c check` per-file does not catch missing cross-namespace usings (known trap); write every using explicitly, fully-qualify on collisions (existing file already does `System.IO.Path` for this reason).
- **Compile validation:** the PostToolUse hook runs `nt8c check <file>` on every edit — fix any CS error before proceeding. Each task ends with the cross-file staged build (`--custom-dir` requires the NT8 Custom folder layout — stage first):
  ```bash
  rm -rf /tmp/bigprints-staging && mkdir -p /tmp/bigprints-staging/Indicators /tmp/bigprints-staging/Strategies
  cp BigPrints.cs BigPrintsAiClient.cs /tmp/bigprints-staging/Indicators/ && cp BigPrintsStrategy.cs /tmp/bigprints-staging/Strategies/
  nt8c build --custom-dir /tmp/bigprints-staging --out /tmp/bigprints-staged.dll
  ```
  → expected exit 0.
- **No runtime test harness exists for NinjaScript.** Per-task cycle = compile validation + code review; runtime verification is consolidated in Task 5 (Market Replay + failure drills). This is the plan's sanctioned deviation from strict TDD.
- **Threading:** never block the market-data thread (`OnMarketData`/`OnMarketDepth` only append to locked, bounded structures). UI work only via `ChartControl.Dispatcher.InvokeAsync` (never synchronous `.Invoke`). The button `Click` handler runs on the UI thread. Bar reads outside market-data events use absolute accessors (`Bars.GetClose(idx)`), never the `barsAgo` indexer.
- **Sound:** only the existing winmm P/Invoke path. NT8's `PlaySound()` is BANNED in this repo (verified use-after-free crash — see BigPrints.cs header).
- **API params:** model `claude-sonnet-5`; NO `temperature`/`top_p`/`top_k` (rejected with 400); `thinking` field omitted (adaptive is the model default); non-streaming with `max_tokens` 8000 (lenses) / 6000 (orchestrator).
- **API key:** runtime read from a file; never in source, never in an indicator property, never in logs.
- **Language:** all code identifiers, comments, prompts, and docs in English.
- **Commits:** one per task, conventional style, ending with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

---

### Task 1: Data-capture layer in BigPrints.cs (L2 book, cluster memory, session stats)

**Files:**
- Modify: `projects/Trading/BigPrints/BigPrints.cs`

**Interfaces:**
- Consumes: existing fields `_clusterIsBuy`, `_clusterVolume`, `_clusterPrice`, `_clusterMaxTime` inside `FinalizeCluster`; existing `OnMarketData` buy/sell classification (`isBuy`, `e.Volume`, `e.Price`).
- Produces (used by Task 4):
  - `internal string SerializeLadder(int maxLevels)` — `"ASK 28524.00 x 12"` lines, asks top-down then bids.
  - `internal string SerializeRecentClusters(int max)` — one line per cluster, oldest→newest.
  - `internal string SerializeRecentBars(int count)` — one OHLCV line per bar, oldest→newest.
  - `internal string SerializeSessionStats()` — cum delta, high/low since load, current bid/ask.

- [ ] **Step 1: Add the L2 book fields and `OnMarketDepth` override**

Add after the existing `_lastSoundTick` field declaration:

```csharp
        // ---- AI Advisor data-capture layer -------------------------------------------
        // L2 book maintained from OnMarketDepth, per NinjaTrader's own SampleLevel2Book:
        // lists indexed by e.Position (NOT price-keyed — a Remove at position 0 shifts
        // every lower level up, and NT sends the matching Update/Remove sequence).
        // All mutations AND reads lock on Instrument.SyncMarketDepth — the platform's
        // own sanctioned lock object for depth state.
        private class LadderRow
        {
            public double Price;
            public long   Volume;
        }
        private readonly List<LadderRow> _askRows = new List<LadderRow>(10);
        private readonly List<LadderRow> _bidRows = new List<LadderRow>(10);

        // Recent finalized big-print clusters (the ones that were drawn), bounded.
        // Appended on the market-data thread, read on the UI thread at Analyze time.
        private class ClusterRecord
        {
            public DateTime Time;
            public bool     IsBuy;
            public double   Price;
            public long     Volume;
        }
        private readonly object _clusterMemLock = new object();
        private readonly Queue<ClusterRecord> _recentClusters = new Queue<ClusterRecord>();
        private const int MaxClusterMemory = 50;

        // Session stats since chart load (approximation of session — honest label is
        // applied at serialize time). Written on the market-data thread only; reads
        // from the UI thread tolerate tearing (doubles/longs, advisory context only).
        private long   _cumDelta;
        private double _sessionHigh = double.MinValue;
        private double _sessionLow  = double.MaxValue;
```

```csharp
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            lock (e.Instrument.SyncMarketDepth)
            {
                List<LadderRow> rows = e.MarketDataType == MarketDataType.Ask ? _askRows : _bidRows;

                if (e.Operation == Operation.Add ||
                    (e.Operation == Operation.Update && (rows.Count == 0 || rows.Count <= e.Position)))
                {
                    var row = new LadderRow { Price = e.Price, Volume = e.Volume };
                    if (rows.Count <= e.Position) rows.Add(row);
                    else                          rows.Insert(e.Position, row);
                }
                else if (e.Operation == Operation.Remove && rows.Count > e.Position)
                {
                    rows.RemoveAt(e.Position);
                }
                else if (e.Operation == Operation.Update)
                {
                    rows[e.Position].Price  = e.Price;
                    rows[e.Position].Volume = e.Volume;
                }
            }
        }
```

The defensive `Add`-on-out-of-range-`Update` branch is verbatim from NinjaTrader's own sample — keep it.

- [ ] **Step 2: Accumulate session stats and cluster memory in the existing paths**

In `OnMarketData`, immediately after the `isBuy` classification block (after the `else return;` for between-market prints), add:

```csharp
            // AI Advisor session stats — same thread as all other Last-print handling.
            _cumDelta += isBuy ? e.Volume : -e.Volume;
            if (e.Price > _sessionHigh) _sessionHigh = e.Price;
            if (e.Price < _sessionLow)  _sessionLow  = e.Price;
```

In `FinalizeCluster`, right after the `if (_clusterVolume < MinVolume) return;` guard, add:

```csharp
            // AI Advisor cluster memory — record every drawn cluster, bounded.
            lock (_clusterMemLock)
            {
                _recentClusters.Enqueue(new ClusterRecord
                {
                    Time   = _clusterMaxTime,
                    IsBuy  = _clusterIsBuy,
                    Price  = _clusterPrice,
                    Volume = _clusterVolume,
                });
                if (_recentClusters.Count > MaxClusterMemory)
                    _recentClusters.Dequeue();
            }
```

- [ ] **Step 3: Add the four serializers**

Add as new methods (below `FinalizeCluster`):

```csharp
        // ---- AI Advisor serializers (called on the UI thread at Analyze time) --------

        internal string SerializeLadder(int maxLevels)
        {
            var sb = new System.Text.StringBuilder();
            lock (Instrument.SyncMarketDepth)
            {
                for (int i = Math.Min(maxLevels, _askRows.Count) - 1; i >= 0; i--)
                    sb.AppendLine("ASK " + _askRows[i].Price.ToString("F2") + " x " + _askRows[i].Volume);
                for (int i = 0; i < Math.Min(maxLevels, _bidRows.Count); i++)
                    sb.AppendLine("BID " + _bidRows[i].Price.ToString("F2") + " x " + _bidRows[i].Volume);
            }
            return sb.ToString().TrimEnd();
        }

        internal string SerializeRecentClusters(int max)
        {
            var sb = new System.Text.StringBuilder();
            lock (_clusterMemLock)
            {
                int skip = Math.Max(0, _recentClusters.Count - max);
                int i = 0;
                foreach (var c in _recentClusters)
                {
                    if (i++ < skip) continue;
                    sb.AppendLine(c.Time.ToString("HH:mm:ss") + " " + (c.IsBuy ? "BUY " : "SELL")
                        + " " + c.Volume + " contracts @ " + c.Price.ToString("F2"));
                }
            }
            return sb.ToString().TrimEnd();
        }

        internal string SerializeRecentBars(int count)
        {
            // UI-thread caller: absolute accessors only (barsAgo indexer is unsafe here).
            var sb = new System.Text.StringBuilder();
            try
            {
                if (CurrentBar < 0)
                    return "bar data unavailable";
                int first = Math.Max(0, CurrentBar - count + 1);
                for (int idx = first; idx <= CurrentBar; idx++)
                {
                    sb.AppendLine(Bars.GetTime(idx).ToString("HH:mm") 
                        + " O:" + Bars.GetOpen(idx).ToString("F2")
                        + " H:" + Bars.GetHigh(idx).ToString("F2")
                        + " L:" + Bars.GetLow(idx).ToString("F2")
                        + " C:" + Bars.GetClose(idx).ToString("F2")
                        + " V:" + Bars.GetVolume(idx));
                }
            }
            catch (Exception) { return "bar data unavailable"; }
            return sb.ToString().TrimEnd();
        }

        internal string SerializeSessionStats()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("cumulative delta since chart load: " + _cumDelta + " contracts (buy-aggressor minus sell-aggressor)");
            if (_sessionHigh > double.MinValue)
                sb.AppendLine("high/low since chart load: " + _sessionHigh.ToString("F2") + " / " + _sessionLow.ToString("F2"));
            sb.AppendLine("current inside market: bid " + _bid.ToString("F2") + " / ask " + _ask.ToString("F2"));
            return sb.ToString().TrimEnd();
        }
```

- [ ] **Step 4: Verify compile**

The edit hook already ran `nt8c check` per edit. Run the staged cross-file build:

Run: `nt8c build --custom-dir "/home/javlo/Code Projects/main-project/projects/Trading/BigPrints" --out /tmp/bigprints-staged.dll`
Expected: exit 0, no CS errors.

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BigPrints"
git add BigPrints.cs
git commit -m "feat(ai): data-capture layer — L2 book via OnMarketDepth, cluster memory, session stats

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: BigPrintsAiClient.cs — DTOs, HttpClient core, single Messages call

**Files:**
- Create: `projects/Trading/BigPrints/BigPrintsAiClient.cs`

**Interfaces:**
- Consumes: nothing from other tasks (self-contained).
- Produces (used by Tasks 3 & 4):
  - `class ContextSnapshot` — fields `Instrument`, `ChartTimeframe`, `LadderText`, `ClustersText`, `BarsText`, `SessionText`, `BasePrompt`, `ResponseLanguage`, `ScreenshotBase64` (null = none), `CapturedAt` (all public).
  - `class LensReport` — `Lens`, `Report` (null on failure), `Error`.
  - `class AiVerdict` — `Decision` ("buy"/"sell"/"hold"/"error"), `Confidence`, `Entry`/`Stop`/`Target` (`double?`), `Rationale`, `Error`, `List<LensReport> LensReports`, `InputTokens`, `OutputTokens`.
  - `class MessagesResult` — `Text`, `StopReason`, `InputTokens`, `OutputTokens`, `Error`.
  - `BigPrintsAiClient(string apiKey, string modelId)` ctor.
  - `static string LoadApiKey(string path)` — returns trimmed key or null.
  - `Task<MessagesResult> CallMessagesAsync(string systemPrompt, string userText, string screenshotBase64, int maxTokens, JObject outputFormat, CancellationToken ct)`.

- [ ] **Step 1: Create the file with DTOs and the HTTP core**

```csharp
#region Using declarations
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#endregion

// BigPrintsAiClient — the Anthropic Messages API layer for the BigPrints AI Advisor.
// Plain class (NOT an Indicator); compiled into the same NT8 custom assembly.
// Raw HttpClient + Newtonsoft (ships with NT8) by design: the official Anthropic C#
// SDK's dependency closure collides with assembly versions NT8 pins in its own
// process (verified 2026-07-23) — do not "upgrade" this to the SDK.
namespace NinjaTrader.NinjaScript.Indicators
{
    public class ContextSnapshot
    {
        public string   Instrument;
        public string   ChartTimeframe;
        public string   LadderText;
        public string   ClustersText;
        public string   BarsText;
        public string   SessionText;
        public string   BasePrompt;
        public string   ResponseLanguage;
        public string   ScreenshotBase64;   // null = no image captured
        public DateTime CapturedAt;
    }

    public class LensReport
    {
        public string Lens;
        public string Report;               // null when the call failed
        public string Error;                // null when the call succeeded
        public long   InputTokens;
        public long   OutputTokens;
    }

    public class AiVerdict
    {
        public string  Decision = "error";  // "buy" | "sell" | "hold" | "error"
        public int     Confidence;
        public double? Entry;
        public double? Stop;
        public double? Target;
        public string  Rationale;
        public string  Error;               // non-null only when Decision == "error"
        public List<LensReport> LensReports = new List<LensReport>();
        public long    InputTokens;
        public long    OutputTokens;
    }

    public class MessagesResult
    {
        public string Text;
        public string StopReason;
        public long   InputTokens;
        public long   OutputTokens;
        public string Error;                // non-null on any failure
    }

    public class BigPrintsAiClient
    {
        // ONE HttpClient for the AppDomain — never per-call (net48 socket-exhaustion trap).
        private static readonly HttpClient Http = BuildHttpClient();

        private static HttpClient BuildHttpClient()
        {
            // net48: TLS 1.2 is not guaranteed OS-default on every box — force it once.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            // Default per-host cap is 2 — too low for 3 parallel lens calls.
            ServicePointManager.DefaultConnectionLimit = 20;
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
            client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            return client;
        }

        private readonly string _apiKey;
        private readonly string _modelId;

        public BigPrintsAiClient(string apiKey, string modelId)
        {
            _apiKey  = apiKey;
            _modelId = modelId;
        }

        public static string LoadApiKey(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path))
                    return null;
                string key = System.IO.File.ReadAllText(path).Trim();
                return key.Length > 0 ? key : null;
            }
            catch (Exception) { return null; }
        }

        public async Task<MessagesResult> CallMessagesAsync(string systemPrompt, string userText,
            string screenshotBase64, int maxTokens, JObject outputFormat, CancellationToken ct)
        {
            var content = new JArray();
            if (!string.IsNullOrEmpty(screenshotBase64))
            {
                content.Add(new JObject
                {
                    ["type"]   = "image",
                    ["source"] = new JObject
                    {
                        ["type"]       = "base64",
                        ["media_type"] = "image/png",
                        ["data"]       = screenshotBase64,
                    },
                });
            }
            content.Add(new JObject { ["type"] = "text", ["text"] = userText });

            var body = new JObject
            {
                ["model"]      = _modelId,
                ["max_tokens"] = maxTokens,
                ["system"]     = systemPrompt,
                ["messages"]   = new JArray
                {
                    new JObject { ["role"] = "user", ["content"] = content },
                },
                // No temperature/top_p/top_k (rejected by claude-sonnet-5);
                // no thinking field (adaptive is the model default).
            };
            if (outputFormat != null)
                body["output_config"] = new JObject { ["format"] = outputFormat };

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
                {
                    req.Headers.Add("x-api-key", _apiKey);
                    req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        string raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            string apiMsg = null;
                            try { apiMsg = (string)JObject.Parse(raw)?["error"]?["message"]; }
                            catch (Exception) { /* non-JSON error body — fall through to raw */ }
                            return new MessagesResult { Error = "HTTP " + (int)resp.StatusCode + ": " + (apiMsg ?? raw) };
                        }

                        var json = JObject.Parse(raw);
                        var sb = new StringBuilder();
                        var blocks = json["content"] as JArray;
                        if (blocks != null)
                            foreach (var block in blocks)
                                if ((string)block["type"] == "text")
                                    sb.Append((string)block["text"]);

                        return new MessagesResult
                        {
                            Text         = sb.ToString(),
                            StopReason   = (string)json["stop_reason"],
                            InputTokens  = (long?)json["usage"]?["input_tokens"]  ?? 0,
                            OutputTokens = (long?)json["usage"]?["output_tokens"] ?? 0,
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return new MessagesResult { Error = "cancelled" };
            }
            catch (Exception ex)
            {
                return new MessagesResult { Error = ex.Message };
            }
        }
    }
}
```

- [ ] **Step 2: Verify compile**

Run: `nt8c build --custom-dir "/home/javlo/Code Projects/main-project/projects/Trading/BigPrints" --out /tmp/bigprints-staged.dll`
Expected: exit 0.

- [ ] **Step 3: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BigPrints"
git add BigPrintsAiClient.cs
git commit -m "feat(ai): API client core — DTOs, static HttpClient (TLS1.2, 180s), Messages call with vision

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Lens pipeline, prompts, orchestrator with structured output, JSONL log

**Files:**
- Modify: `projects/Trading/BigPrints/BigPrintsAiClient.cs`

**Interfaces:**
- Consumes: `CallMessagesAsync`, DTOs from Task 2.
- Produces (used by Task 4): `Task<AiVerdict> AnalyzeAsync(ContextSnapshot ctx, CancellationToken ct)`.

- [ ] **Step 1: Add the prompts as constants**

Add inside `BigPrintsAiClient` (prompt craft: role assignment, XML-tagged data, explicit rules with escape hatches, bounded output; no prefill — rejected by claude-sonnet-5; structured output replaces it on the orchestrator):

```csharp
        private const int LensMaxTokens         = 8000;
        private const int OrchestratorMaxTokens = 6000;

        private const string OrderFlowSystemPrompt =
@"You are an order-flow analyst on a futures trading desk. You read the tape and the limit-order book.

You will receive a snapshot of the current market: account context, session stats, the Level-2 ladder, a list of recent large aggressive prints (side, price, volume, time), recent OHLCV bars, and possibly a chart screenshot.

Analyze ONLY the order-flow dimension:
- Who is aggressing (buyers or sellers) and whether price is responding to that aggression.
- Absorption: large aggressive volume hitting a level while price fails to move through it.
- Iceberg behavior: repeated large prints at or near the same price with the ladder refilling on that side.
- Exhaustion: aggression climaxing without follow-through.
- Ladder imbalances that support or contradict what the tape shows.

Rules:
- Base every claim on the data provided. If a signal is not present in the data, do not invent it.
- If the ladder says 'L2 data unavailable', say so and reason from the tape alone.
- Note data limitations explicitly rather than guessing.

Write a concise report (max 200 words): the dominant order-flow read, the key evidence (cite specific prices and volumes from the data), and which side - if any - order flow currently favors.";

        private const string StructureSystemPrompt =
@"You are a market-structure analyst on a futures trading desk. You read price action, trend, and location.

You will receive a snapshot of the current market: account context, session stats, the Level-2 ladder, recent large aggressive prints, recent OHLCV bars, and possibly a chart screenshot.

Analyze ONLY the structure dimension:
- Trend and momentum on the chart timeframe: sequence of highs/lows, impulse vs correction.
- Location: where price sits relative to the session high/low, recent swing points, and round numbers.
- If a screenshot is provided, read the visual structure (ranges, levels, reactions) and cross-check it against the bar data.
- Whether current price is at a level where a reaction is plausible, or in the middle of a range.

Rules:
- Base every claim on the data provided; cite specific prices from the bars or session stats.
- If the bar data and the screenshot disagree, trust the bar data and say so.

Write a concise report (max 200 words): the structural context, the key levels above and below current price, and which side - if any - structure currently favors.";

        private const string RiskSystemPrompt =
@"You are the risk manager on a futures trading desk. Your job is to decide whether ANY trade here offers acceptable risk-reward - and to veto when it does not. A veto is a fully acceptable outcome; most moments in a session deserve one.

You will receive a snapshot of the current market: account context (including account size and max risk per trade), session stats, the Level-2 ladder, recent large aggressive prints, recent OHLCV bars, and possibly a chart screenshot.

Analyze ONLY the risk dimension:
- The nearest logical invalidation for a long and for a short (a level that, if traded through, proves the idea wrong).
- The realistic target current structure offers for each side, and the implied reward:risk.
- Whether current volatility (bar ranges) is compatible with the stop distance the account context allows.
- Anything that makes this moment untradeable: mid-range chop, erratic bars, thin or distorted ladder.

Rules:
- Use concrete price levels from the data for every stop or target you mention.
- If neither side offers at least roughly 1.5:1 reward-to-risk with a structure-based stop, say 'no trade' plainly.

Write a concise report (max 200 words): viable long setup (entry/stop/target, or 'none'), viable short setup (entry/stop/target, or 'none'), and your overall risk verdict.";

        private const string OrchestratorSystemPrompt =
@"You are the head trader synthesizing three specialist reports into one decision. The specialists analyzed the same market snapshot through different lenses: order flow, market structure, and risk.

Rules:
- 'hold' means no trade. It is the correct call when the lenses disagree without a strong tiebreaker, when the risk lens found no viable setup, or when the edge is marginal. Do not force a trade.
- Output 'buy' or 'sell' only when the evidence aligns across lenses AND the risk lens found a viable setup on that side. The risk lens has veto power.
- confidence: integer 0-100; below 50 means you would not size this trade normally.
- entry, stop, target: concrete prices taken from the risk lens's levels (adjust only if you disagree and say why in the rationale); all three null when the decision is hold.
- rationale: 2 to 4 sentences naming the deciding evidence. If a lens report was unavailable, mention that the decision was made without it.";
```

- [ ] **Step 2: Add the user-context builder, verdict schema, pipeline, and log**

```csharp
        private static string BuildUserContext(ContextSnapshot ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<account_context>");
            sb.AppendLine(ctx.BasePrompt);
            sb.AppendLine("</account_context>");
            sb.AppendLine();
            sb.AppendLine("<instrument>" + ctx.Instrument + " | chart timeframe: " + ctx.ChartTimeframe
                + " | captured: " + ctx.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss") + "</instrument>");
            sb.AppendLine();
            sb.AppendLine("<session_stats>");
            sb.AppendLine(ctx.SessionText);
            sb.AppendLine("</session_stats>");
            sb.AppendLine();
            sb.AppendLine("<l2_ladder>");
            sb.AppendLine(string.IsNullOrEmpty(ctx.LadderText) ? "L2 data unavailable" : ctx.LadderText);
            sb.AppendLine("</l2_ladder>");
            sb.AppendLine();
            sb.AppendLine("<recent_big_prints>");
            sb.AppendLine(string.IsNullOrEmpty(ctx.ClustersText) ? "none recorded yet" : ctx.ClustersText);
            sb.AppendLine("</recent_big_prints>");
            sb.AppendLine();
            sb.AppendLine("<recent_bars>");
            sb.AppendLine(ctx.BarsText);
            sb.AppendLine("</recent_bars>");
            return sb.ToString();
        }

        // Structured-output schema for the orchestrator — guaranteed-parseable JSON.
        // No numeric min/max (unsupported by structured outputs) — confidence bounds
        // are enforced by the prompt and clamped client-side.
        private static readonly JObject VerdictFormat = new JObject
        {
            ["type"]   = "json_schema",
            ["schema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["decision"]   = new JObject { ["type"] = "string", ["enum"] = new JArray("buy", "sell", "hold") },
                    ["confidence"] = new JObject { ["type"] = "integer" },
                    ["entry"]      = new JObject { ["type"] = new JArray("number", "null") },
                    ["stop"]       = new JObject { ["type"] = new JArray("number", "null") },
                    ["target"]     = new JObject { ["type"] = new JArray("number", "null") },
                    ["rationale"]  = new JObject { ["type"] = "string" },
                },
                ["required"] = new JArray("decision", "confidence", "entry", "stop", "target", "rationale"),
                ["additionalProperties"] = false,
            },
        };

        private async Task<LensReport> RunLensAsync(string lensName, string systemPrompt,
            ContextSnapshot ctx, string userContext, CancellationToken ct)
        {
            string userText = userContext
                + "\nAnalyze the snapshot above through your lens and write your report now.";
            MessagesResult r = await CallMessagesAsync(systemPrompt, userText, ctx.ScreenshotBase64,
                LensMaxTokens, null, ct).ConfigureAwait(false);

            if (r.Error != null)
                return new LensReport { Lens = lensName, Error = r.Error, InputTokens = r.InputTokens, OutputTokens = r.OutputTokens };
            if (r.StopReason == "refusal")
                return new LensReport { Lens = lensName, Error = "model refused", InputTokens = r.InputTokens, OutputTokens = r.OutputTokens };

            string report = r.Text;
            if (r.StopReason == "max_tokens")
                report += "\n[report truncated at token limit]";
            return new LensReport { Lens = lensName, Report = report, InputTokens = r.InputTokens, OutputTokens = r.OutputTokens };
        }

        public async Task<AiVerdict> AnalyzeAsync(ContextSnapshot ctx, CancellationToken ct)
        {
            string userContext = BuildUserContext(ctx);

            LensReport[] reports = await Task.WhenAll(
                RunLensAsync("order_flow", OrderFlowSystemPrompt, ctx, userContext, ct),
                RunLensAsync("structure",  StructureSystemPrompt, ctx, userContext, ct),
                RunLensAsync("risk",       RiskSystemPrompt,      ctx, userContext, ct)
            ).ConfigureAwait(false);

            var verdict = new AiVerdict();
            verdict.LensReports.AddRange(reports);

            int surviving = 0;
            var lensBlock = new StringBuilder();
            foreach (LensReport lr in reports)
            {
                lensBlock.AppendLine("<" + lr.Lens + ">");
                if (lr.Report != null) { lensBlock.AppendLine(lr.Report); surviving++; }
                else                     lensBlock.AppendLine("REPORT UNAVAILABLE (" + lr.Error + ")");
                lensBlock.AppendLine("</" + lr.Lens + ">");
            }

            if (surviving == 0)
            {
                verdict.Error = "all three lens calls failed: " + reports[0].Error;
                AppendLog(ctx, verdict);
                return verdict;
            }

            string orchestratorUser =
                "<market_context>\n" + userContext + "</market_context>\n\n"
                + "<lens_reports>\n" + lensBlock + "</lens_reports>\n\n"
                + "Write the rationale in " + ctx.ResponseLanguage + ". Decide now: buy, sell, or hold.";

            // Orchestrator gets no screenshot — it reasons over the reports + text context.
            MessagesResult or_ = await CallMessagesAsync(OrchestratorSystemPrompt, orchestratorUser,
                null, OrchestratorMaxTokens, VerdictFormat, ct).ConfigureAwait(false);

            // Usage totals = 3 lenses + orchestrator, recorded even on orchestrator failure.
            foreach (LensReport lr in reports)
            {
                verdict.InputTokens  += lr.InputTokens;
                verdict.OutputTokens += lr.OutputTokens;
            }
            verdict.InputTokens  += or_.InputTokens;
            verdict.OutputTokens += or_.OutputTokens;

            if (or_.Error != null)      { verdict.Error = "orchestrator failed: " + or_.Error; AppendLog(ctx, verdict); return verdict; }
            if (or_.StopReason == "refusal") { verdict.Error = "orchestrator refused"; AppendLog(ctx, verdict); return verdict; }

            try
            {
                JObject v = JObject.Parse(or_.Text);
                verdict.Decision   = (string)v["decision"];
                verdict.Confidence = Math.Max(0, Math.Min(100, (int?)v["confidence"] ?? 0));
                verdict.Entry      = (double?)v["entry"];
                verdict.Stop       = (double?)v["stop"];
                verdict.Target     = (double?)v["target"];
                verdict.Rationale  = (string)v["rationale"];
                verdict.Error      = null;
            }
            catch (Exception ex)
            {
                verdict.Decision = "error";
                verdict.Error    = "verdict parse failed: " + ex.Message;
            }

            AppendLog(ctx, verdict);
            return verdict;
        }
```

**Note:** delete the dead `foreach`/`lensIn` bookkeeping placeholder above before committing — token totals must be accumulated properly instead: change `RunLensAsync` to also copy `r.InputTokens`/`r.OutputTokens` into two new `LensReport` fields (`public long InputTokens; public long OutputTokens;`), and in `AnalyzeAsync` sum them into `verdict.InputTokens`/`verdict.OutputTokens` together with the orchestrator's usage. (Spelled out so the implementer does not ship the placeholder.)

- [ ] **Step 3: Add the JSONL log**

```csharp
        private static readonly object LogLock = new object();

        private static void AppendLog(ContextSnapshot ctx, AiVerdict verdict)
        {
            try
            {
                string dir = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "BigPrintsAI");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "analyses.jsonl");

                var record = new JObject
                {
                    ["timestamp"]           = ctx.CapturedAt.ToString("o"),
                    ["instrument"]          = ctx.Instrument,
                    ["timeframe"]           = ctx.ChartTimeframe,
                    ["screenshot_included"] = !string.IsNullOrEmpty(ctx.ScreenshotBase64), // never the base64 itself
                    ["session"]             = ctx.SessionText,
                    ["ladder"]              = ctx.LadderText,
                    ["clusters"]            = ctx.ClustersText,
                    ["bars"]                = ctx.BarsText,
                    ["lens_reports"]        = new JArray(),
                    ["decision"]            = verdict.Decision,
                    ["confidence"]          = verdict.Confidence,
                    ["entry"]               = verdict.Entry,
                    ["stop"]                = verdict.Stop,
                    ["target"]              = verdict.Target,
                    ["rationale"]           = verdict.Rationale,
                    ["error"]               = verdict.Error,
                    ["input_tokens"]        = verdict.InputTokens,
                    ["output_tokens"]       = verdict.OutputTokens,
                };
                foreach (LensReport lr in verdict.LensReports)
                    ((JArray)record["lens_reports"]).Add(new JObject
                    {
                        ["lens"] = lr.Lens, ["report"] = lr.Report, ["error"] = lr.Error,
                    });

                lock (LogLock)
                    System.IO.File.AppendAllText(path, record.ToString(Formatting.None) + Environment.NewLine);
            }
            catch (Exception) { /* logging must never break an analysis */ }
        }
```

- [ ] **Step 4: Verify compile**

Run: `nt8c build --custom-dir "/home/javlo/Code Projects/main-project/projects/Trading/BigPrints" --out /tmp/bigprints-staged.dll`
Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BigPrints"
git add BigPrintsAiClient.cs
git commit -m "feat(ai): lens pipeline — 3 parallel analysts + orchestrator with structured output + JSONL log

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Chart UX — parameters, Analyze button, capture, rendering

**Files:**
- Modify: `projects/Trading/BigPrints/BigPrints.cs`

**Interfaces:**
- Consumes: Task 1 serializers (`SerializeLadder`, `SerializeRecentClusters`, `SerializeRecentBars`, `SerializeSessionStats`); Task 2/3 `BigPrintsAiClient`, `BigPrintsAiClient.LoadApiKey`, `ContextSnapshot`, `AiVerdict`, `AnalyzeAsync`.
- Produces: end-user feature (button → panel + lines). Nothing downstream.

- [ ] **Step 1: Add usings and fields**

Add to the `#region Using declarations` (explicit — cross-file trap):

```csharp
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
```

(`System.Windows` and `System.Windows.Media` are already there. `NinjaTrader.Gui.Chart` types are fully qualified below to avoid the `Chart` name ambiguity.)

Add fields:

```csharp
        // ---- AI Advisor UX -----------------------------------------------------------
        private BigPrintsAiClient _aiClient;
        private CancellationTokenSource _aiCts;
        private bool _analysisRunning;
        private DateTime _analysisStartedUtc;
        private Grid   _analyzeGrid;
        private Button _analyzeButton;
        private DispatcherTimer _elapsedTimer;

        private const string DefaultBasePrompt =
@"Account size: $50,000. Max risk per trade: $500.
Trading style: intraday futures, one position at a time, structure-based stops, no overnight positions.";
```

- [ ] **Step 2: Add the parameters**

In `State.SetDefaults`, after the existing defaults:

```csharp
                EnableAiAdvisor      = true;
                ApiKeyFilePath       = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "claude_api_key.txt");
                ModelId              = "claude-sonnet-5";
                BasePrompt           = DefaultBasePrompt;
                ResponseLanguage     = "English";
                EnableScreenshot     = true;
                DomLevelsToSend      = 10;
                RecentClustersToSend = 20;
                BarsToSend           = 30;
                AnalysisSoundFile    = "";
```

In the `#region Properties`, after `SellBrushSerialize`:

```csharp
        [NinjaScriptProperty]
        [Display(Name = "Enable AI Advisor", Description = "Master switch for the Analyze button and AI analysis.", Order = 20, GroupName = "AI Advisor")]
        public bool EnableAiAdvisor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "API Key File Path", Description = "Text file containing ONLY the Anthropic API key. The key itself is never stored in the indicator.", Order = 21, GroupName = "AI Advisor")]
        public string ApiKeyFilePath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Model Id", Description = "Anthropic model id used for all calls.", Order = 22, GroupName = "AI Advisor")]
        public string ModelId { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Base Prompt", Description = "Account context sent to the AI: account size, max risk per trade, style. English recommended.", Order = 23, GroupName = "AI Advisor")]
        public string BasePrompt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Response Language", Description = "Language of the rationale shown on the chart.", Order = 24, GroupName = "AI Advisor")]
        public string ResponseLanguage { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Send Chart Screenshot", Description = "Attach a screenshot of this chart to the analysis.", Order = 25, GroupName = "AI Advisor")]
        public bool EnableScreenshot { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "DOM Levels To Send", Description = "L2 ladder depth per side.", Order = 26, GroupName = "AI Advisor")]
        public int DomLevelsToSend { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Recent Clusters To Send", Description = "How many recent big-print clusters to include.", Order = 27, GroupName = "AI Advisor")]
        public int RecentClustersToSend { get; set; }

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "Bars To Send", Description = "How many recent bars (OHLCV) to include.", Order = 28, GroupName = "AI Advisor")]
        public int BarsToSend { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Analysis Sound File", Description = "WAV in the NT8 sounds folder played when an analysis completes. Empty = silent.", Order = 29, GroupName = "AI Advisor")]
        public string AnalysisSoundFile { get; set; }
```

- [ ] **Step 3: Client init + button lifecycle in OnStateChange**

In `State.DataLoaded` (after existing resets):

```csharp
                if (EnableAiAdvisor)
                {
                    _aiCts = new CancellationTokenSource();
                    string key = BigPrintsAiClient.LoadApiKey(ApiKeyFilePath);
                    if (key != null)
                        _aiClient = new BigPrintsAiClient(key, ModelId);
                    else
                        Print("BigPrints AI: API key file not found or empty at '" + ApiKeyFilePath + "' — Analyze disabled.");
                }
```

Add a new `State.Historical` branch (button UI — `UserControlCollection` is NT8's sanctioned surface for indicator buttons; add here, NOT in DataLoaded where ChartControl is not ready):

```csharp
            else if (State == State.Historical)
            {
                if (!EnableAiAdvisor || ChartControl == null)
                    return;

                ChartControl.Dispatcher.InvokeAsync(new Action(() =>
                {
                    // Duplicate guard — the lifecycle can re-enter Historical on the same instance.
                    if (_analyzeGrid != null && UserControlCollection.Contains(_analyzeGrid))
                        return;

                    _analyzeGrid = new Grid
                    {
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment   = VerticalAlignment.Bottom,
                        Margin              = new Thickness(0, 0, 12, 32),
                    };
                    _analyzeButton = new Button
                    {
                        Content    = "Analyze",
                        Padding    = new Thickness(10, 4, 10, 4),
                        Foreground = Brushes.White,
                        Background = Brushes.DarkSlateGray, // predefined brush — thread-safe, no Freeze needed
                    };
                    _analyzeButton.Click += OnAnalyzeClick;
                    _analyzeGrid.Children.Add(_analyzeButton);
                    UserControlCollection.Add(_analyzeGrid);

                    _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _elapsedTimer.Tick += OnElapsedTick;
                }));
            }
```

In `State.Terminated`, before the existing `FinalizeCluster(true);`:

```csharp
                _aiCts?.Cancel();
                _aiCts?.Dispose();
                _aiCts = null;

                if (ChartControl != null)
                {
                    ChartControl.Dispatcher.InvokeAsync(new Action(() =>
                    {
                        _elapsedTimer?.Stop();
                        if (_analyzeButton != null)
                        {
                            _analyzeButton.Click -= OnAnalyzeClick;
                            _analyzeGrid?.Children.Remove(_analyzeButton);
                            _analyzeButton = null;
                        }
                        if (_analyzeGrid != null)
                        {
                            UserControlCollection.Remove(_analyzeGrid);
                            _analyzeGrid = null;
                        }
                    }));
                }
```

- [ ] **Step 4: Click handler, capture, pipeline dispatch, rendering**

Add as new methods:

```csharp
        // ---- AI Advisor: click → capture → pipeline → render -------------------------
        // Click fires on the ChartControl UI thread (WPF routed event) — capture and
        // screenshot run here directly; only the HTTP pipeline goes to Task.Run.

        private void OnAnalyzeClick(object sender, RoutedEventArgs e)
        {
            if (_analysisRunning)
                return;
            if (_aiClient == null)
            {
                DrawAiPanel("AI: API key not loaded\ncheck 'API Key File Path' parameter", Brushes.Orange);
                return;
            }

            _analysisRunning    = true;
            _analysisStartedUtc = DateTime.UtcNow;
            _analyzeButton.IsEnabled = false;
            _elapsedTimer.Start();
            DrawAiPanel("Analyzing... 0s", Brushes.Gainsboro);

            ContextSnapshot ctx = CaptureContext();
            CancellationToken ct = _aiCts.Token;

            Task.Run(async () =>
            {
                AiVerdict verdict;
                try
                {
                    verdict = await _aiClient.AnalyzeAsync(ctx, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    verdict = new AiVerdict { Decision = "error", Error = ex.Message };
                }
                ChartControl?.Dispatcher.InvokeAsync(new Action(() => OnAnalysisComplete(verdict)));
            });
        }

        private void OnElapsedTick(object sender, EventArgs e)
        {
            int secs = (int)(DateTime.UtcNow - _analysisStartedUtc).TotalSeconds;
            DrawAiPanel("Analyzing... " + secs + "s", Brushes.Gainsboro);
        }

        private ContextSnapshot CaptureContext()
        {
            string screenshotB64 = null;
            if (EnableScreenshot)
            {
                try
                {
                    // NT8-internal API (same mechanism as the Share feature). Works only
                    // when this chart tab is active — always true on a manual click.
                    var chartWindow = System.Windows.Window.GetWindow(ChartControl) as NinjaTrader.Gui.Chart.Chart;
                    var bmp = chartWindow == null ? null
                        : chartWindow.GetScreenshot(NinjaTrader.Gui.Chart.ShareScreenshotType.Chart);
                    if (bmp != null)
                    {
                        bmp.Freeze();
                        using (var ms = new System.IO.MemoryStream())
                        {
                            var enc = new PngBitmapEncoder();
                            enc.Frames.Add(BitmapFrame.Create(bmp));
                            enc.Save(ms);
                            screenshotB64 = Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }
                catch (Exception ex)
                {
                    Print("BigPrints AI: screenshot failed, sending without image — " + ex.Message);
                }
            }

            return new ContextSnapshot
            {
                Instrument       = Instrument.FullName,
                ChartTimeframe   = BarsPeriod.ToString(),
                LadderText       = SerializeLadder(DomLevelsToSend),
                ClustersText     = SerializeRecentClusters(RecentClustersToSend),
                BarsText         = SerializeRecentBars(BarsToSend),
                SessionText      = SerializeSessionStats(),
                BasePrompt       = BasePrompt,
                ResponseLanguage = ResponseLanguage,
                ScreenshotBase64 = screenshotB64,
                CapturedAt       = DateTime.Now,
            };
        }

        private void OnAnalysisComplete(AiVerdict verdict)
        {
            _elapsedTimer?.Stop();
            _analysisRunning = false;
            if (_analyzeButton != null)
                _analyzeButton.IsEnabled = true;

            RemoveDrawObject("BigPrintsAiEntry");
            RemoveDrawObject("BigPrintsAiStop");
            RemoveDrawObject("BigPrintsAiTarget");

            if (verdict.Error != null)
            {
                DrawAiPanel("AI ERROR\n" + WrapText(verdict.Error, 60), Brushes.Orange);
                return;
            }

            Brush decisionBrush =
                verdict.Decision == "buy"  ? Brushes.Lime :
                verdict.Decision == "sell" ? Brushes.Red  : Brushes.Silver;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("BIG PRINTS AI  " + DateTime.Now.ToString("HH:mm:ss"));
            sb.AppendLine(verdict.Decision.ToUpper() + "  (confidence " + verdict.Confidence + ")");
            if (verdict.Entry.HasValue)
                sb.AppendLine("Entry " + verdict.Entry.Value.ToString("F2")
                    + " | Stop " + (verdict.Stop.HasValue ? verdict.Stop.Value.ToString("F2") : "-")
                    + " | Target " + (verdict.Target.HasValue ? verdict.Target.Value.ToString("F2") : "-"));
            sb.AppendLine(WrapText(verdict.Rationale ?? "", 60));
            sb.Append("tokens " + verdict.InputTokens + " in / " + verdict.OutputTokens + " out");
            DrawAiPanel(sb.ToString(), decisionBrush);

            if (verdict.Decision == "buy" || verdict.Decision == "sell")
            {
                if (verdict.Entry.HasValue)
                    Draw.HorizontalLine(this, "BigPrintsAiEntry", false, verdict.Entry.Value, decisionBrush, DashStyleHelper.Solid, 2);
                if (verdict.Stop.HasValue)
                    Draw.HorizontalLine(this, "BigPrintsAiStop", false, verdict.Stop.Value, Brushes.OrangeRed, DashStyleHelper.Dash, 2);
                if (verdict.Target.HasValue)
                    Draw.HorizontalLine(this, "BigPrintsAiTarget", false, verdict.Target.Value, Brushes.DeepSkyBlue, DashStyleHelper.Dash, 2);
            }

            if (!string.IsNullOrEmpty(AnalysisSoundFile))
            {
                string fullPath = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "sounds", AnalysisSoundFile);
                if (System.IO.File.Exists(fullPath))
                    WinmmPlaySound(fullPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
            }
        }

        private void DrawAiPanel(string text, Brush brush)
        {
            Draw.TextFixed(this, "BigPrintsAiPanel", text, TextPosition.TopRight,
                brush, new SimpleFont("Consolas", 12), Brushes.Transparent, Brushes.Black, 60);
        }

        // TextFixed does not word-wrap — insert newlines at word boundaries.
        private static string WrapText(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new System.Text.StringBuilder();
            int lineLen = 0;
            foreach (string word in text.Split(' '))
            {
                if (lineLen + word.Length + 1 > width) { sb.Append('\n'); lineLen = 0; }
                else if (lineLen > 0)                  { sb.Append(' ');  lineLen++; }
                sb.Append(word);
                lineLen += word.Length;
            }
            return sb.ToString();
        }
```

If `nt8c` rejects a `Draw.HorizontalLine`/`Draw.TextFixed` overload, consult the local `nt8-drawing-tools` skill for the exact signature of this NT8 build and adjust the argument list — do not remove the call.

- [ ] **Step 5: Verify compile**

Run: `nt8c build --custom-dir "/home/javlo/Code Projects/main-project/projects/Trading/BigPrints" --out /tmp/bigprints-staged.dll`
Expected: exit 0.

- [ ] **Step 6: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BigPrints"
git add BigPrints.cs
git commit -m "feat(ai): chart UX — Analyze button, context+screenshot capture, verdict panel and SL/TP lines

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: Review, docs, deploy, runtime verification

**Files:**
- Modify: `projects/Trading/BigPrints/README.md`
- Deploy (copy, not tracked): both `.cs` files → NT8, key file → NT8 Documents.

**Interfaces:**
- Consumes: everything above. Produces: reviewed, deployed, runtime-verified feature.

- [ ] **Step 1: Code review**

Dispatch `trading-code-reviewer` (read-only) over `BigPrints.cs` + `BigPrintsAiClient.cs` with the spec as context. Fix every real finding (correctness, threading, NT8 API misuse) before deploying; re-run the staged build after fixes and amend/commit as `fix(ai): review findings`.

- [ ] **Step 2: README section**

Append to `README.md`:

```markdown
## AI Advisor

Manual decision support: an **Analyze** button on the chart sends the current market
context (L2 ladder, recent big-print clusters, recent bars, session stats, chart
screenshot) to `claude-sonnet-5` — three parallel lens analysts (order flow,
structure, risk) plus an orchestrator — and draws the verdict on the chart:
BUY/SELL/HOLD, confidence, Entry/SL/TP lines. Every analysis is appended to
`Documents/NinjaTrader 8/BigPrintsAI/analyses.jsonl` (audit trail; screenshots are
not logged). The AI never places orders.

**Setup:** put your Anthropic API key (the key only, one line) in
`Documents/NinjaTrader 8/claude_api_key.txt` (or change the *API Key File Path*
parameter). Edit *Base Prompt* with your account size and max risk per trade.

**Cost:** ~$0.11 per click at claude-sonnet-5 intro pricing (~$0.17 after 2026-08-31).
Requires an L2 data feed for the ladder (analysis still runs without it).
Design: `docs/specs/2026-07-23-ai-advisor-design.md`.
```

- [ ] **Step 3: Deploy to NT8**

```bash
cp "/home/javlo/Code Projects/main-project/projects/Trading/BigPrints/BigPrints.cs" \
   "/home/javlo/Code Projects/main-project/projects/Trading/BigPrints/BigPrintsAiClient.cs" \
   "/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom/Indicators/"
tr -d '\r\n' < "/home/javlo/Code Projects/main-project/projects/Trading/BigPrints/doc/Anthropic Key.txt" \
   > "/mnt/c/Users/javlo/Documents/NinjaTrader 8/claude_api_key.txt"
```

Then (human step): NT8 Editor → F5 compile. Expected: clean compile.

- [ ] **Step 4: Runtime verification (human, Market Replay on NQ/ES 1-min)**

Checklist for Javier — each item pass/fail:

1. Button appears bottom-right; chart loads normally with `EnableAiAdvisor=false` too (no button).
2. Click Analyze → "Analyzing… Ns" counts up; button disabled while running.
3. Verdict panel renders with decision color; BUY/SELL draws the three lines; HOLD draws none.
4. `Documents/NinjaTrader 8/BigPrintsAI/analyses.jsonl` has one complete record (open it: lens reports present, no base64).
5. Ladder text populated (L2 feed connected) — check the log record.
6. Failure drills: (a) rename the key file → click → orange "API key not loaded" panel, no crash; (b) disconnect network → click → orange error panel ≤180s, no crash; (c) `EnableScreenshot=false` → analysis completes, log shows `screenshot_included: false`; (d) close the chart mid-analysis → no crash, no orphaned draw objects on reopen.
7. Cost sanity: token counts on the panel are in the expected range (~20-35K in total).

- [ ] **Step 5: Final commit + memory**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BigPrints"
git add README.md
git commit -m "docs(ai): README — AI Advisor usage, key setup, cost

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

Update workspace memory `bigprints-project.md`: AI Advisor implemented + verified (or note outstanding drill failures).
