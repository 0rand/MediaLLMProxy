namespace OAIPreRouter.Cli.Models;

/// <summary>
/// Retry-once policy for the DeepSeek stack's <c>unavailable_tool_call</c> model-output
/// error. The stack rejects a complete tool call that names a tool not offered by the
/// request; this proxy can re-issue the same prompt once with a user-turn banner that
/// tells the model to use only the offered tools. Modeled on LoopGuardOptions.
/// </summary>
public record UnavailableToolRetryOptions
{
    public const string ConfigSection = "UnavailableToolRetryOptions";

    /// <summary>Master switch. When false, the stack error passes through unchanged.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Shared retry budget for stack rejections and proxy raw-tool aborts.
    /// Capped at one re-issue even if configured higher.</summary>
    public int MaxRetries { get; init; } = 1;

    public bool RetryRawToolCallAborts { get; init; } = true;

    /// <summary>Maximum bytes buffered per attempt before any output reaches the client.</summary>
    public long MaxResponseBytes { get; init; } = 16 * 1024 * 1024;

    public string RawToolCallBanner { get; init; } =
        "Your previous response was rejected because its tool-call markup was invalid, incomplete, or named an unavailable tool. " +
        "Retry the original task using only tools offered in this request. Emit complete, valid tool calls with the required arguments. " +
        "Do not repeat the rejected markup. If explaining tool syntax rather than calling a tool, avoid raw protocol tags.";

    /// <summary>Banner text appended as a new user message on the retry request. It is
    /// sent only upstream and is never persisted in the client's conversation.</summary>
    public string Banner { get; init; } =
        "Your previous response attempted to call a tool that is not available in this request. " +
        "Use only the tools provided in the current request. Do not call tools that are not listed.";

    /// <summary>Marker prepended to the banner so the model can distinguish this
    /// system-side retry note from a real user instruction.</summary>
    public string InjectionMarker { get; init; } =
        "[PROXY TOOL RETRY - corrective note for the previous rejected response]: ";
}
