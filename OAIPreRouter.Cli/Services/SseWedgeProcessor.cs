namespace OAIPreRouter.Cli.Services;

using System.Text;
using System.Text.Json;
using OAIPreRouter.Cli.Models;

/// <summary>
/// Mid-turn wedge: forwards an SSE stream live while watching reasoning growth. When
/// reasoning exceeds the threshold with no content and no tool calls, the judge is called;
/// on NUDGE, attempt 2 (thinking OFF + prior-reasoning context) is issued, attempt 1 is
/// aborted, a banner is injected, and attempt 2's stream is forwarded to the SAME client
/// stream. Fail-open: judge failure → keep attempt 1; attempt 2 failure → keep attempt 1.
/// </summary>
public static class SseWedgeProcessor
{
    public static async Task ProcessAsync(
        Stream source,
        Stream destination,
        LoopGuardOptions opts,
        Func<string, Task<(string? Nudge, string? Summary)>> judge,
        Func<string, string?, string?, Task<Stream?>> makeAttempt2,
        Action? abortAttempt1,
        CancellationToken ct)
    {
        using var reader = new StreamReader(source, Encoding.UTF8, leaveOpen: true);
        var reasoning = new StringBuilder();
        var hasContent = false;
        var hasToolCalls = false;
        var judged = false;
        var wedged = false;
        string? id = null;
        long created = 0;
        string? model = null;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var payload = line["data:".Length..].Trim();
                if (payload == "[DONE]")
                {
                    await WriteLineAsync(destination, line, ct);
                    await destination.FlushAsync(ct);
                    return;
                }
                if (payload.Length > 0)
                {
                    CaptureMeta(payload, ref id, ref created, ref model);
                    Accumulate(payload, reasoning, ref hasContent, ref hasToolCalls);
                    if (IsTerminalChunk(payload))
                    {
                        await WriteLineAsync(destination, line, ct);
                        await destination.FlushAsync(ct);
                        return;
                    }
                }
            }

            await WriteLineAsync(destination, line, ct);
            if (line.Length == 0)
                await destination.FlushAsync(ct);

            // Trigger: reasoning past threshold, no content/tool_calls yet, not judged/wedged.
            if (!judged && !wedged && !hasContent && !hasToolCalls &&
                reasoning.Length / 4 >= opts.WedgeReasoningTokens)
            {
                judged = true;
                var (nudge, summary) = await judge(reasoning.ToString());
                if (nudge == null)
                    continue; // NO_LOOP — keep streaming attempt 1, no more judging

                wedged = true;
                Stream? attempt2 = null;
                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var heartbeat = HeartbeatAsync(destination, heartbeatCts.Token);
                try
                {
                    attempt2 = await makeAttempt2(reasoning.ToString(), nudge, summary);
                }
                finally
                {
                    heartbeatCts.Cancel();
                    try { await heartbeat; } catch { /* heartbeat is best-effort */ }
                }

                if (attempt2 == null)
                {
                    // Attempt 2 failed — keep attempt 1 (fail-open), stop watching.
                    wedged = false;
                    continue;
                }

                abortAttempt1?.Invoke();
                await WriteBannerAsync(destination, id, created, model, opts.WedgeBanner + "\n\n" + nudge, ct);
                await ForwardAsync(attempt2, destination, ct);
                return;
            }
        }
        await destination.FlushAsync(ct);
    }

    private static async Task ForwardAsync(Stream source, Stream destination, CancellationToken ct)
    {
        using var reader = new StreamReader(source, Encoding.UTF8, leaveOpen: true);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            await WriteLineAsync(destination, line, ct);
            if (line.Length == 0)
                await destination.FlushAsync(ct);
        }
        await destination.FlushAsync(ct);
    }

    private static async Task HeartbeatAsync(Stream destination, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);
                await WriteLineAsync(destination, ": ping", ct);
                await WriteLineAsync(destination, "", ct);
                await destination.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task WriteBannerAsync(Stream destination, string? id, long created, string? model,
        string banner, CancellationToken ct)
    {
        var chunk = new Dictionary<string, object?>
        {
            ["id"] = id ?? "chatcmpl-loopguard-wedge",
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model ?? "",
            ["choices"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        // Banner rides the REASONING block, not content — the answer stays
                        // clean; the wedge note is part of the thinking narrative.
                        ["reasoning_content"] = "\n\n" + banner
                    },
                    ["finish_reason"] = null
                }
            }
        };
        await WriteLineAsync(destination, "data: " + JsonSerializer.Serialize(chunk), ct);
        await WriteLineAsync(destination, "", ct);
        await destination.FlushAsync(ct);
    }

    private static async Task WriteLineAsync(Stream destination, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await destination.WriteAsync(bytes, ct);
    }

    private static void CaptureMeta(string payload, ref string? id, ref long created, ref string? model)
    {
        if (id != null) return;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                id = idProp.GetString();
            if (doc.RootElement.TryGetProperty("created", out var cProp) && cProp.ValueKind == JsonValueKind.Number)
                created = cProp.GetInt64();
            if (doc.RootElement.TryGetProperty("model", out var mProp) && mProp.ValueKind == JsonValueKind.String)
                model = mProp.GetString();
        }
        catch { }
    }

    private static void Accumulate(string payload, StringBuilder reasoning, ref bool hasContent, ref bool hasToolCalls)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return;
            var delta = choices[0].TryGetProperty("delta", out var d) ? d : default;
            if (delta.ValueKind != JsonValueKind.Object) return;
            // vLLM streams reasoning in `reasoning`, llama.cpp/oMLX in `reasoning_content` — count both.
            if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                reasoning.Append(rc.GetString());
            else if (delta.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
                reasoning.Append(r.GetString());
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(c.GetString()))
                hasContent = true;
            if (delta.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array &&
                tc.GetArrayLength() > 0)
                hasToolCalls = true;
        }
        catch { }
    }

    private static bool IsTerminalChunk(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return false;
            var choice = choices[0];
            return choice.TryGetProperty("finish_reason", out var fr) &&
                   fr.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrEmpty(fr.GetString());
        }
        catch { return false; }
    }
}
