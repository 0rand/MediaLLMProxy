namespace OAIPreRouter.Cli.Services;

using System.Text;
using System.Text.Json;
using OAIPreRouter.Cli.Models;

/// <summary>
/// LoopGuard stage 2: gate + LLM judge. When the LAST assistant message's reasoning_content
/// exceeds the token threshold (chars/4 estimate), the advisor model (small fast LLM) judges
/// whether the reasoning is looping. NUDGE → inject the advisor's nudge into the last user
/// message; NO_LOOP → forward unchanged. Advisor dead → fall back to the static nudge
/// (stage 1) when configured. Fail-open: any anomaly → body passes through unchanged.
/// </summary>
public static class LoopGuardService
{
    public const string AdvisorPrompt = """
        You are a loop-detection judge. Your ONLY job is to decide whether the reasoning below is looping or overthinking.

        Rules:
        - Do NOT solve the task. Do NOT give technical advice. Do NOT answer the underlying question.
        - If the reasoning is healthy and making progress, reply with exactly: NO_LOOP
        - If the reasoning is looping (repeating the same idea, going in circles, rehashing without progress), reply with a short nudge (1-3 sentences) that steers the model to break out. Start with "NUDGE:".

        REASONING:
        """;

    /// <summary>Extract the reasoning_content of the LAST assistant message (string only).</summary>
    public static string? ExtractLastReasoning(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
                return null;
            for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
            {
                var m = messages[i];
                if (m.ValueKind != JsonValueKind.Object) continue;
                if (!m.TryGetProperty("role", out var r) || r.GetString() != "assistant") continue;
                if (m.TryGetProperty("reasoning_content", out var rc) &&
                    rc.ValueKind == JsonValueKind.String)
                {
                    var text = rc.GetString();
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }
                return null; // last assistant has no reasoning → no gate
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Gate: reasoning size estimate (chars/4 ≈ tokens) vs threshold.</summary>
    public static bool GateFires(string? reasoning, int thresholdTokens)
    {
        if (string.IsNullOrWhiteSpace(reasoning) || thresholdTokens <= 0) return false;
        return reasoning.Length / 4 >= thresholdTokens;
    }

    /// <summary>Parse the advisor verdict: "NUDGE: <text>" → nudge text; anything else → null.</summary>
    public static string? ParseVerdict(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        if (t.StartsWith("NUDGE:", StringComparison.OrdinalIgnoreCase))
        {
            var nudge = t["NUDGE:".Length..].Trim();
            return string.IsNullOrWhiteSpace(nudge) ? null : nudge;
        }
        return null;
    }

    /// <summary>Call the advisor endpoint (OpenAI-compatible). Returns raw content or null on failure.</summary>
    public static async Task<string?> CallAdvisorAsync(HttpClient client, string baseUrl, string model,
        string prompt, int maxTokens, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model,
                messages = new[] { new { role = "user", content = prompt } },
                temperature = 0,
                max_tokens = maxTokens
            };
            var url = baseUrl.TrimEnd('/') + "/v1/chat/completions";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var choice = doc.RootElement.GetProperty("choices")[0];
            return choice.GetProperty("message").TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Evaluate the loop guard on a request body. Returns the (possibly rewritten) body and
    /// the verdict ("nudge" | "no_loop" | null). Advisor dead → static fallback when configured.
    /// </summary>
    public static async Task<(string? Body, string? Verdict, long AdvisorMs)> EvaluateAsync(
        string body, LoopGuardOptions opts, ObservationCache? cache, HttpClient? advisorClient, CancellationToken ct)
    {
        if (!opts.Enabled) return (null, null, 0);
        var reasoning = ExtractLastReasoning(body);
        if (!GateFires(reasoning, opts.ReasoningTokenThreshold)) return (null, null, 0);

        var advisorConfigured = !string.IsNullOrWhiteSpace(opts.AdvisorBackend.BaseUrl);
        var nudge = (string?)null;
        var verdict = (string?)null;
        long advisorMs = 0;

        if (advisorConfigured && advisorClient != null)
        {
            var capped = reasoning!.Length > opts.MaxReasoningChars
                ? reasoning[..opts.MaxReasoningChars]
                : reasoning;
            var cacheKey = ObservationCache.BuildKey(capped, opts.AdvisorModel, "loopguard-v1");
            string? raw = null;
            if (cache != null && cache.TryGet(cacheKey, out var cached))
            {
                raw = cached;
            }
            else
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                raw = await CallAdvisorAsync(advisorClient, opts.AdvisorBackend.BaseUrl, opts.AdvisorModel,
                    AdvisorPrompt + capped, opts.AdvisorMaxTokens, ct);
                sw.Stop();
                advisorMs = sw.ElapsedMilliseconds;
                if (raw != null && cache != null)
                    cache.Set(cacheKey, raw);
            }

            if (raw != null)
            {
                nudge = ParseVerdict(raw);
                verdict = nudge != null ? "nudge" : "no_loop";
            }
            else
            {
                // Advisor dead → stage 1 fallback
                if (opts.AdvisorFallbackToStatic && !string.IsNullOrWhiteSpace(opts.StaticNudge))
                {
                    nudge = opts.StaticNudge;
                    verdict = "nudge";
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(opts.StaticNudge))
        {
            // Static-only mode (no advisor configured)
            nudge = opts.StaticNudge;
            verdict = "nudge";
        }

        if (nudge == null) return (null, verdict, advisorMs);
        var rewritten = JsonBodyRewriter.TryInjectLoopGuardNudge(body, opts.InjectionMarker, nudge);
        return rewritten == null ? (null, verdict, advisorMs) : (rewritten, verdict, advisorMs);
    }
}
