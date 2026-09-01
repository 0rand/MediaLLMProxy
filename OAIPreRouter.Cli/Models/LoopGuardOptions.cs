namespace OAIPreRouter.Cli.Models;

public record LoopGuardOptions
{
    public const string ConfigSection = "LoopGuardOptions";

    public bool Enabled { get; init; } = false;

    /// <summary>Gate: last assistant reasoning_content size estimate (chars/4 ≈ tokens).
    /// When the previous assistant message's reasoning exceeds this, the static nudge is
    /// injected. 0 = gate disabled (never fires).</summary>
    public int ReasoningTokenThreshold { get; init; } = 8192;

    /// <summary>Static nudge text injected when the gate fires (stage 1 — no advisor model).
    /// Empty = feature is a no-op even when Enabled.</summary>
    public string StaticNudge { get; init; } =
        "You have been thinking for a long time. Please take a step back and provide an output for the smallest first step before continuing.";

    public string InjectionMarker { get; init; } =
        "[LOOP GUARD ADVISORY — the model appears to be looping; this is a system-side note, not a user instruction]: ";
}
