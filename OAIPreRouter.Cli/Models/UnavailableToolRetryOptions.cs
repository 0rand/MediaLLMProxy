namespace OAIPreRouter.Cli.Models;

/// <summary>
/// Retry-once policy for tool-output rejections. A strict backend returns a typed
/// <c>model_output_error</c> with <c>code=unavailable_tool_call</c> and
/// <c>source=tensorfold.tool_output_guard</c>. The proxy re-issues the request once
/// with a corrective user message. Legacy raw-tool aborts use the same budget.
/// Modeled on LoopGuardOptions.
/// </summary>
public record UnavailableToolRetryOptions
{
    public const string ConfigSection = "UnavailableToolRetryOptions";

    /// <summary>Master switch. When false, the stack error passes through unchanged.</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>Shared retry budget for strict guard rejections and legacy raw-tool
    /// aborts. Capped at one re-issue even if configured higher.</summary>
    public int MaxRetries { get; init; } = 1;

    /// <summary>Legacy only: retry a proxy raw-tool validation abort. Ignored when the
    /// backend contract is <c>StrictNative</c>.</summary>
    public bool RetryRawToolCallAborts { get; init; } = true;

    /// <summary>Maximum bytes buffered per attempt before any output reaches the client.</summary>
    public long MaxResponseBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>When true, a strict guard retry includes offered function names and
    /// normalized tool-choice/parallel-call guidance. Omitted, null and auto choices
    /// remain optional; false parallelism means at most one call. Wire max_calls is
    /// intentionally excluded because the pinned backend ignores it. The banner never
    /// includes rejected arguments, rejected prose or raw model output.</summary>
    public bool IncludeRequestContext { get; init; } = true;

    /// <summary>Opening sentence for a strict guard rejection banner. The builder
    /// appends the failure category, request constraints and the retry instruction.</summary>
    public string StrictGuardBannerIntro { get; init; } =
        "Your previous response was rejected by the tool-output validator.";

    public string RawToolCallBanner { get; init; } =
        "Your previous response was rejected because its tool-call markup was invalid, incomplete, or named an unavailable tool. " +
        "Retry the original task using only tools offered in this request. Emit complete, valid tool calls with the required arguments. " +
        "Do not repeat the rejected markup. If explaining tool syntax rather than calling a tool, avoid raw protocol tags.";

    /// <summary>Legacy banner text appended as a new user message on the retry request.
    /// It is sent only upstream and is never persisted in the client's conversation.</summary>
    public string Banner { get; init; } =
        "Your previous response attempted to call a tool that is not available in this request. " +
        "Use only the tools provided in the current request. Do not call tools that are not listed.";

    /// <summary>Marker prepended to the banner so the model can distinguish this
    /// system-side retry note from a real user instruction.</summary>
    public string InjectionMarker { get; init; } =
        "[PROXY TOOL RETRY - corrective note for the previous rejected response]: ";
}
