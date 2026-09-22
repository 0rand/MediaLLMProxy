namespace OAIPreRouter.Cli.Models;

/// <summary>
/// MiMo-specific compatibility rule for vLLM's Qwen3-compatible reasoning
/// parser. MiMo can emit a strict JSON answer before </think> when tools remain
/// present on a response_format turn; vLLM then routes the JSON to reasoning
/// rather than content. Disable thinking only for those structured turns.
/// </summary>
public record StructuredOutputOptions
{
    public const string ConfigSection = "StructuredOutputOptions";

    /// <summary>
    /// When a request has a top-level response_format, force the MiMo template
    /// to seed an empty think block (<think></think>) via enable_thinking=false.
    /// Ordinary requests preserve the caller's thinking setting unchanged.
    /// </summary>
    public bool DisableThinkingForResponseFormat { get; init; } = false;
}
