namespace OAIPreRouter.Cli.Services;

using System.Text.Json;
using OAIPreRouter.Cli.Models;

/// <summary>
/// LoopGuard stage 1: static nudge. When the LAST assistant message's reasoning_content
/// exceeds the token threshold (chars/4 estimate), a static nudge is injected into the last
/// user message before forwarding. No advisor model — pure gate + inject. Fail-open: any
/// parse anomaly → body passes through unchanged.
/// </summary>
public static class LoopGuardService
{
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

    /// <summary>
    /// Evaluate the loop guard on a request body. Returns the (possibly rewritten) body and
    /// the verdict ("nudge" | null). Static mode only: gate fires → inject StaticNudge.
    /// </summary>
    public static (string? Body, string? Verdict) Evaluate(string body, LoopGuardOptions opts)
    {
        if (!opts.Enabled) return (null, null);
        if (string.IsNullOrWhiteSpace(opts.StaticNudge)) return (null, null);

        var reasoning = ExtractLastReasoning(body);
        if (!GateFires(reasoning, opts.ReasoningTokenThreshold)) return (null, null);

        var rewritten = JsonBodyRewriter.TryInjectLoopGuardNudge(body, opts.InjectionMarker, opts.StaticNudge);
        return rewritten == null ? (null, null) : (rewritten, "nudge");
    }
}
