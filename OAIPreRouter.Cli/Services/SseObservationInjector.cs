namespace OAIPreRouter.Cli.Services;

using System.Text;
using System.Text.Json;

/// <summary>
/// Passes an SSE chat-completions stream through to the client, injecting a final assistant
/// delta carrying the media observation block just BEFORE the terminal chunk (the one with a
/// non-null finish_reason). The block therefore lands inside the assistant message the client
/// persists, making the observation durable in conversation history — so later turns never need
/// to re-detour historical media.
///
/// Writes go straight to the destination stream with async writes only — Kestrel forbids
/// synchronous IO on the response body (StreamWriter.AutoFlush triggers a sync Flush and throws).
/// </summary>
public static class SseObservationInjector
{
    public static async Task InjectAsync(Stream source, Stream destination, string observationBlock, CancellationToken ct)
    {
        using var reader = new StreamReader(source, Encoding.UTF8, leaveOpen: true);

        string? id = null;
        long created = 0;
        string? model = null;
        var injected = false;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var payload = line["data:".Length..].Trim();
                if (payload == "[DONE]")
                {
                    // Clean end-of-stream without a terminal finish_reason chunk: inject the
                    // observation here so it is not lost. (Unexpected EOF is NOT treated as a
                    // clean end — an aborted/failed stream must not be made to look complete.)
                    if (!injected)
                    {
                        await WriteLineAsync(destination, "data: " + BuildObservationChunk(id, created, model, observationBlock), ct);
                        await WriteLineAsync(destination, "", ct);
                        injected = true;
                    }
                    await WriteLineAsync(destination, line, ct);
                    continue;
                }

                if (payload.Length > 0)
                {
                    CaptureChunkMeta(payload, ref id, ref created, ref model);
                    if (!injected && IsTerminalChunk(payload))
                    {
                        await WriteLineAsync(destination, "data: " + BuildObservationChunk(id, created, model, observationBlock), ct);
                        await WriteLineAsync(destination, "", ct);
                        injected = true;
                    }
                }
            }
            await WriteLineAsync(destination, line, ct);
            // Flush at SSE event boundaries (blank line = event separator) so the client
            // receives chunks promptly — preserves streaming semantics.
            if (line.Length == 0)
                await destination.FlushAsync(ct);
        }
        await destination.FlushAsync(ct);
    }

    private static async Task WriteLineAsync(Stream destination, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await destination.WriteAsync(bytes, ct);
    }

    private static void CaptureChunkMeta(string payload, ref string? id, ref long created, ref string? model)
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

    private static string BuildObservationChunk(string? id, long created, string? model, string observationBlock)
    {
        var chunk = new Dictionary<string, object?>
        {
            ["id"] = id ?? "chatcmpl-media-observation",
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
                        ["content"] = "\n\n" + observationBlock
                    },
                    ["finish_reason"] = null
                }
            }
        };
        return JsonSerializer.Serialize(chunk);
    }
}
