namespace OAIPreRouter.Cli.Models;

/// <summary>
/// Configuration options for routing requests between backends.
/// All backends use OpenAI-compatible protocol (/v1/chat/completions).
/// 
/// Routing logic (no tags needed):
/// 1. Detect if first message is a large system prompt (>SystemPromptThresholdBytes) → PrimaryBackend (main agent)
/// 2. If lean prompt (sub-agent), route by total size:
///    - Below FastLaneThresholdBytes → "fast" backend (5070 Ti, 9B)
///    - Above → "heavy" backend (GB10, 35B A3E)
/// 3. Concurrency limits enforced per backend; overflow falls back to primary.
/// </summary>
public record RoutingOptions
{
    public const string ConfigSection = "RoutingOptions";

    /// <summary>
    /// Gets the URL where this router listens for incoming requests.
    /// </summary>
    public string ListenUrl { get; init; } = "http://0.0.0.0:7071";

    /// <summary>
    /// Gets the primary backend — destination for main agent requests (large system prompts).
    /// Typical: GB10, Qwen 3.6 27B dense.
    /// </summary>
    public BackendConfig PrimaryBackend { get; init; } = new()
    {
        BaseUrl = "http://localhost:8000"
    };

    /// <summary>
    /// Gets the fast backend — destination for simple sub-agent tasks (small context).
    /// Typical: 5070 Ti, Qwen 3.5 9B, max 2 concurrent.
    /// </summary>
    public BackendConfig FastBackend { get; init; } = new()
    {
        BaseUrl = "http://localhost:8000",
        MaxConcurrentConnections = 2
    };

    /// <summary>
    /// Gets the heavy backend — destination for complex sub-agent tasks (large context).
    /// Typical: GB10, Qwen 3.6 35B A3E, max 2 concurrent.
    /// </summary>
    public BackendConfig HeavyBackend { get; init; } = new()
    {
        BaseUrl = "http://localhost:8000",
        MaxConcurrentConnections = 2
    };

    /// <summary>
    /// Gets the byte threshold for detecting main agent vs sub-agent requests.
    /// If the first message (system prompt) exceeds this, it's treated as a main agent request → primary.
    /// Default: 100000 bytes (~50K tokens at 2 bytes/token).
    /// </summary>
    public int SystemPromptThresholdBytes { get; init; } = 100000;

    /// <summary>
    /// Gets the total body byte threshold for fast vs heavy sub-agent routing.
    /// Sub-agent requests below this go to fast backend, above go to heavy backend.
    /// Default: 65536 bytes (~32K tokens at 2 bytes/token).
    /// </summary>
    public int FastLaneThresholdBytes { get; init; } = 65536;

    /// <summary>
    /// Gets whether routing decisions should be logged.
    /// </summary>
    public bool LogDecisions { get; init; } = true;

    /// <summary>
    /// Gets whether request/response bodies should be logged.
    /// </summary>
    public bool LogBodies { get; init; } = false;

    /// <summary>
    /// Gets whether to log all incoming requests with full details (method, path, headers, body preview).
    /// Useful for debugging and identifying unknown endpoints. Console-only output.
    /// </summary>
    public bool VerboseRequests { get; init; } = false;

    /// <summary>
    /// When true, the rewritten (media-stripped + observation-injected) body is
    /// logged before forwarding to the text backend — shows exactly what the
    /// text model receives after the vision/STT detour answered.
    /// </summary>
    public bool VerboseRewrites { get; init; } = false;
}
