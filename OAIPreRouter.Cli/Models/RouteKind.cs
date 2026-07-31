namespace OAIPreRouter.Cli.Models;

/// <summary>
/// Routing decision result. Contains the resolved backend name and the reason for the decision.
/// </summary>
public record RoutingDecision(
    string BackendName,
    string Reason,
    bool IsPrimary
)
{
    /// <summary>
    /// Creates a routing decision for the primary backend.
    /// </summary>
    public static RoutingDecision Primary(string reason = "Default") =>
        new("primary", reason, true);

    /// <summary>
    /// Creates a routing decision for a named backend.
    /// </summary>
    public static RoutingDecision Named(string backendName, string reason) =>
        new(backendName, reason, false);

    /// <summary>
    /// Creates a routing decision that fell back to primary due to concurrency limit.
    /// </summary>
    public static RoutingDecision ConcurrencyFallback(string requestedBackend) =>
        Primary($"ConcurrencyLimit({requestedBackend})");
}
