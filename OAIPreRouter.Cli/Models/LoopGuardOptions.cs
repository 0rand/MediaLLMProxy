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

    // ─── Stage 3: mid-turn wedge ──────────────────────────────────────────────────────────

    /// <summary>When true, the streaming wedge is armed: reasoning grows past
    /// WedgeReasoningTokens with no content and no tool calls → judge → re-issue with
    /// thinking OFF + prior-reasoning context, streamed to the same client connection.</summary>
    public bool WedgeEnabled { get; init; } = false;

    /// <summary>Trigger: reasoning token estimate (chars/4) in the live stream.</summary>
    public int WedgeReasoningTokens { get; init; } = 8192;

    /// <summary>Re-issue with thinking disabled (chat_template_kwargs.thinking=false).</summary>
    public bool WedgeThinkingOff { get; init; } = true;

    /// <summary>Prior reasoning inclusion in the wedge message: "summary" (3B distills in the
    /// judge call) | "verbatim" (raw, capped at MaxReasoningChars) | "none".</summary>
    public string WedgeReasoningMode { get; init; } = "summary";

    /// <summary>Banner prepended to the re-issued stream (visible to the user AND persisted).</summary>
    public string WedgeBanner { get; init; } =
        "[LOOP GUARD WEDGE — your reasoning was truncated because you were overthinking. Thinking is now disabled — answer directly.]";
}
