namespace OAIPreRouter.Cli.Models;

/// <summary>
/// Represents a configured outbound backend with URL and protocol information.
/// All backends use OpenAI-compatible protocol (/v1/chat/completions).
/// </summary>
public record BackendConfig
{
    /// <summary>
    /// Gets the base URL of the backend service.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Gets an optional API key to inject in the Authorization header when routing to this backend.
    /// When provided, replaces any incoming Authorization header for remote backends.
    /// Format: "Bearer YOUR_KEY" or "YOUR_KEY" (Bearer prefix is added automatically if missing).
    /// For local backends, incoming Authorization headers are always stripped regardless of this setting.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets the optional model name to rewrite for requests routed to this backend.
    /// When set, replaces the "model" field in the request body before forwarding.
    /// </summary>
    public string? RewriteModel { get; init; }

    /// <summary>
    /// Gets the client-facing model name advertised via /v1/models when RewriteModel is set.
    /// The proxy is a one-model gateway: clients ask for this alias, the proxy executes
    /// RewriteModel. Defaults to RewriteModel when unset. Use "main" for a model-agnostic
    /// surface that doesn't leak the actual model id to clients.
    /// </summary>
    public string? ModelAlias { get; init; }

    /// <summary>
    /// Gets the maximum number of concurrent connections allowed for this backend.
    /// Zero means unlimited. Default is 0 (unlimited).
    /// For VRAM-constrained backends (e.g., GPU with limited memory), set this to prevent OOM.
    /// </summary>
    public int MaxConcurrentConnections { get; init; } = 0;
}
