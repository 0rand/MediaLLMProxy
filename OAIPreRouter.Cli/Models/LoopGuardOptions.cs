namespace OAIPreRouter.Cli.Models;

public record LoopGuardOptions
{
    public const string ConfigSection = "LoopGuardOptions";

    public bool Enabled { get; init; } = false;

    /// <summary>Gate: last assistant reasoning_content size estimate (chars/4 ≈ tokens).
    /// When the previous assistant message's reasoning exceeds this, the static nudge is
    /// injected. 0 = gate disabled (never fires).</summary>
    public int ReasoningTokenThreshold { get; init; } = 8192;

    /// <summary>Static nudge text (stage 1 fallback / no-advisor mode). When the advisor is
    /// not configured or is dead, this text is injected when the gate fires. Empty = no
    /// fallback.</summary>
    public string StaticNudge { get; init; } =
        "You have been thinking for a long time. Please take a step back and provide an output for the smallest first step before continuing.";

    /// <summary>Advisor endpoint (stage 2 judge). Empty BaseUrl = advisor disabled →
    /// static-only mode.</summary>
    public BackendConfig AdvisorBackend { get; init; } = new() { BaseUrl = "http://192.168.1.88:8008" };
    public string AdvisorModel { get; init; } = "qwen25-3b";
    public int AdvisorMaxTokens { get; init; } = 200;
    public int AdvisorTimeoutSeconds { get; init; } = 10;

    /// <summary>Cap on reasoning chars sent to the advisor (protects context + latency).</summary>
    public int MaxReasoningChars { get; init; } = 20000;

    /// <summary>When true and the advisor call fails/times out, inject StaticNudge instead
    /// of failing open (only applies when StaticNudge is non-empty).</summary>
    public bool AdvisorFallbackToStatic { get; init; } = true;

    public string InjectionMarker { get; init; } =
        "[LOOP GUARD ADVISORY — the model appears to be looping; this is a system-side note, not a user instruction]: ";
}
