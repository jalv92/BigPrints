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
