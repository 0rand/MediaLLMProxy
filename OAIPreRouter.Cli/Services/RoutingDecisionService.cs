namespace OAIPreRouter.Cli.Services;

using OAIPreRouter.Cli.Models;
using System.Text.Json;
using System.Text;

/// <summary>
/// Encapsulates size-based routing detection logic.
/// 
/// Phase 1 (observation): Detects sub-agent vs main-agent requests and logs the decision,
/// but always returns primary backend. Fast/Heavy backends are disabled until signatures are validated.
/// 
/// Detection order:
/// 1. Check first message — if system role with large content → main agent → primary
/// 2. Otherwise → sub-agent detected, classify by body size (fast vs heavy)
/// 3. Decision is logged but NOT applied until backends are enabled in config
/// </summary>
public static class RoutingDecisionService
{
    /// <summary>
    /// Analyzes the request and returns a routing decision with detection metadata.
    /// In phase 1, always routes to primary but logs what would have been chosen.
    /// </summary>
    public static RoutingDecision DecideRoute(string body, RoutingOptions opts, ConnectionLimiter limiter)
    {
        var analysis = AnalyzeRequest(body, opts);

        // Phase 1: always route to primary, log detection
        if (analysis.IsMainAgent)
            return RoutingDecision.Primary("MainAgent(SystemPrompt>{0}B)".Replace("{0}", opts.SystemPromptThresholdBytes.ToString()));

        // Sub-agent detected — determine fast vs heavy but still route to primary in phase 1
        var intendedBackend = analysis.TotalBytes <= opts.FastLaneThresholdBytes ? "fast" : "heavy";
        return RoutingDecision.Primary("SubAgent({0},body={1}B)".Replace("{0}", intendedBackend).Replace("{1}", analysis.TotalBytes.ToString()));
    }

    /// <summary>
    /// Analyzes the request body to determine if it's a main agent or sub-agent request.
    /// </summary>
    public static RequestAnalysis AnalyzeRequest(string body, RoutingOptions opts)
    {
        var totalBytes = Encoding.UTF8.GetByteCount(body);

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("messages", out var messages))
                return new RequestAnalysis(true, 0, "unknown", 0, totalBytes);

            var count = messages.GetArrayLength();
            if (count == 0)
                return new RequestAnalysis(true, 0, "empty", 0, totalBytes);

            var first = messages[0];
            var role = "unknown";
            var contentBytes = 0;

            if (first.ValueKind == JsonValueKind.Object)
            {
                if (first.TryGetProperty("role", out var r))
                    role = r.GetString() ?? "null";

                if (first.TryGetProperty("content", out var c))
                {
                    var contentStr = c.GetString();
                    contentBytes = contentStr != null ? Encoding.UTF8.GetByteCount(contentStr) : 0;
                }
            }

            var isMainAgent = role == "system" && contentBytes > opts.SystemPromptThresholdBytes;
            return new RequestAnalysis(isMainAgent, count, role, contentBytes, totalBytes);
        }
        catch
        {
            return new RequestAnalysis(true, -1, "parse_error", 0, totalBytes);
        }
    }

    /// <summary>
    /// Intermediate analysis result for structured logging.
    /// </summary>
    public sealed record RequestAnalysis(
        bool IsMainAgent,
        int MessageCount,
        string FirstMessageRole,
        int FirstMessageBytes,
        int TotalBytes);
}
