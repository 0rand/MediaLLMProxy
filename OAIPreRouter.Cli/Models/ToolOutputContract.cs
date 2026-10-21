namespace OAIPreRouter.Cli.Models;

/// <summary>
/// Describes who owns executable tool-call parsing for a backend.
/// </summary>
public enum ToolOutputContract
{
    /// <summary>
    /// Legacy backend that can leak raw DSML/XML tool markup in answer content.
    /// The proxy may scan and cure it, with the known compatibility limits.
    /// </summary>
    LegacyRaw = 0,

    /// <summary>
    /// Strict backend that emits native <c>tool_calls</c> and validates all
    /// executable output itself. The proxy must not scan, rewrite, convert or
    /// buffer answer content for this backend. It may retry a typed strict
    /// guard rejection once with a corrective user message.
    /// </summary>
    StrictNative = 1
}
