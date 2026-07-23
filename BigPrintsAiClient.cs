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

        public async Task<MessagesResult> CallMessagesAsync(string systemPrompt, string userText,
            string screenshotBase64, int maxTokens, JObject outputFormat, CancellationToken ct)
        {
            try
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

                using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
                {
                    req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
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
    }
}
