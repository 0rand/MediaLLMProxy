namespace OAIPreRouter.Cli.Services;

using System.Buffers;
using System.Text;
using System.Text.Json;
using OAIPreRouter.Cli.Models;

/// <summary>
/// Mid-turn wedge: forwards an SSE stream live while watching reasoning growth. When
/// reasoning exceeds the threshold with no content and no tool calls, the judge is called;
/// on NUDGE, attempt 2 (thinking OFF + prior-reasoning context) is issued, attempt 1 is
/// aborted, a banner is injected, and attempt 2's stream is forwarded to the SAME client
/// stream. Fail-open: judge failure or attempt 2 failure keeps attempt 1.
/// </summary>
public static class SseWedgeProcessor
{
    // Inspection is deliberately bounded. Raw SSE bytes are still forwarded when an event is
    // too large to inspect, but that event cannot trigger a wedge.
    private const int MaxInspectedEventChars = 64 * 1024;

    public static async Task ProcessAsync(
        Stream source,
        Stream destination,
        LoopGuardOptions opts,
        Func<string, Task<(string? Nudge, string? Summary)>> judge,
        Func<string, string?, string?, Task<Stream?>> makeAttempt2,
        Action? abortAttempt1,
        CancellationToken ct)
    {
        var state = new WedgeState(opts.MaxReasoningChars);
        await using var lines = new RawSseLineReader(source);

        while ((await lines.TryReadEventAsync(destination, ct)) is { } sseEvent)
        {
            var eventInfo = Inspect(sseEvent, state);
            if (eventInfo.IsDone)
            {
                await destination.FlushAsync(ct);
                return;
            }

            // finish_reason disables judging, but does not terminate output. The client must
            // receive the finish event's separator plus trailing usage and complete DONE event.
            if (eventInfo.HasFinishReason)
                state.TerminalSeen = true;

            await destination.FlushAsync(ct);

            if (state.TerminalSeen || state.InspectionLost || state.Judged || state.Wedged || state.HasContent || state.HasToolCalls ||
                state.ReasoningChars / 4 < opts.WedgeReasoningTokens)
                continue;

            state.Judged = true;
            (string? nudge, string? summary) verdict;
            try
            {
                verdict = await judge(state.Reasoning.ToString());
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (verdict.nudge == null)
                continue;

            Stream? attempt2 = null;
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = HeartbeatAsync(destination, heartbeatCts.Token);
            try
            {
                attempt2 = await makeAttempt2(state.Reasoning.ToString(), verdict.nudge, verdict.summary);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Attempt 2 setup is fail-open. Continue attempt 1 without another judge.
            }
            finally
            {
                heartbeatCts.Cancel();
                try
                {
                    await heartbeat;
                }
                catch
                {
                    // Heartbeats are only best-effort while the replacement is prepared.
                }
            }

            if (ct.IsCancellationRequested)
            {
                if (attempt2 != null)
                    await attempt2.DisposeAsync();
                ct.ThrowIfCancellationRequested();
            }

            if (attempt2 == null)
                continue;

            state.Wedged = true;
            try
            {
                abortAttempt1?.Invoke();
                await WriteBannerAsync(destination, state.Id, state.Created, state.Model,
                    opts.WedgeBanner + "\n\n" + verdict.nudge, ct);
                await ForwardAsync(attempt2, destination, ct);
            }
            finally
            {
                await attempt2.DisposeAsync();
            }
            return;
        }

        // EOF is not a terminal SSE event. Do not create a finish or [DONE] marker.
        await destination.FlushAsync(ct);
    }

    private static async Task ForwardAsync(Stream source, Stream destination, CancellationToken ct)
    {
        await using var lines = new RawSseLineReader(source);
        while ((await lines.TryReadEventAsync(destination, ct)) is { } sseEvent)
        {
            if (InspectTerminalOnly(sseEvent))
                return;
        }

        await destination.FlushAsync(ct);
    }

    private static EventInfo Inspect(RawSseEvent sseEvent, WedgeState state)
    {
        if (!sseEvent.Inspectable)
        {
            state.InspectionLost = true;
            return default;
        }
        var data = ReadData(sseEvent);
        if (data == null)
            return default;
        if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
            return new EventInfo(IsDone: true, HasFinishReason: false);

        try
        {
            using var doc = JsonDocument.Parse(data);
            CaptureMeta(doc.RootElement, state);
            Accumulate(doc.RootElement, state);
            return new EventInfo(IsDone: false, HasFinishReason: HasFinishReason(doc.RootElement));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool InspectTerminalOnly(RawSseEvent sseEvent)
    {
        if (!sseEvent.Inspectable)
            return false;
        var data = ReadData(sseEvent);
        return data != null && string.Equals(data, "[DONE]", StringComparison.Ordinal);
    }

    private static string? ReadData(RawSseEvent sseEvent)
    {
        StringBuilder? data = null;
        foreach (var rawLine in sseEvent.Lines)
        {
            var line = GetLineText(rawLine);
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var value = line["data:".Length..];
            if (value.StartsWith(' '))
                value = value[1..];

            data ??= new StringBuilder();
            if (data.Length > 0)
                data.Append('\n');
            if (data.Length + value.Length > MaxInspectedEventChars)
                return null;
            data.Append(value);
        }
        return data?.ToString();
    }

    private static string GetLineText(byte[] rawLine)
    {
        var length = rawLine.Length;
        if (length > 0 && rawLine[length - 1] == (byte)'\n')
            length--;
        if (length > 0 && rawLine[length - 1] == (byte)'\r')
            length--;
        return Encoding.UTF8.GetString(rawLine, 0, length);
    }

    private static async Task HeartbeatAsync(Stream destination, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct);
                await destination.WriteAsync(": ping\n\n"u8.ToArray(), ct);
                await destination.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
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
                        ["reasoning_content"] = "\n\n" + banner
                    },
                    ["finish_reason"] = null
                }
            }
        };
        await destination.WriteAsync(Encoding.UTF8.GetBytes("data: " + JsonSerializer.Serialize(chunk) + "\n\n"), ct);
        await destination.FlushAsync(ct);
    }

    private static void CaptureMeta(JsonElement root, WedgeState state)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return;
        if (state.Id != null)
            return;
        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            state.Id = id.GetString();
        if (root.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number)
            state.Created = created.GetInt64();
        if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
            state.Model = model.GetString();
    }

    private static void Accumulate(JsonElement root, WedgeState state)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return;
        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            return;

        if (delta.TryGetProperty("reasoning_content", out var reasoningContent) && reasoningContent.ValueKind == JsonValueKind.String)
            state.AppendReasoning(reasoningContent.GetString());
        else if (delta.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
            state.AppendReasoning(reasoning.GetString());

        if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(content.GetString()))
            state.HasContent = true;
        if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array &&
            toolCalls.GetArrayLength() > 0)
            state.HasToolCalls = true;
    }

    private static bool HasFinishReason(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return false;
        var choice = choices[0];
        return choice.TryGetProperty("finish_reason", out var finishReason) &&
               finishReason.ValueKind == JsonValueKind.String &&
               !string.IsNullOrEmpty(finishReason.GetString());
    }

    private readonly record struct EventInfo(bool IsDone, bool HasFinishReason);

    private sealed class WedgeState(int maxReasoningChars)
    {
        private readonly int _maxReasoningChars = Math.Max(0, maxReasoningChars);
        public StringBuilder Reasoning { get; } = new();
        public long ReasoningChars { get; private set; }
        public bool HasContent { get; set; }
        public bool HasToolCalls { get; set; }
        public bool Judged { get; set; }
        public bool Wedged { get; set; }
        public bool TerminalSeen { get; set; }
        public bool InspectionLost { get; set; }
        public string? Id { get; set; }
        public long Created { get; set; }
        public string? Model { get; set; }

        public void AppendReasoning(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            ReasoningChars += value.Length;
            var remaining = _maxReasoningChars - Reasoning.Length;
            if (remaining > 0)
                Reasoning.Append(value.AsSpan(0, Math.Min(remaining, value.Length)));
        }
    }

    private sealed class RawSseEvent(List<byte[]> lines, bool inspectable)
    {
        public List<byte[]> Lines { get; } = lines;
        public bool Inspectable { get; } = inspectable;
    }

    private sealed class RawSseLineReader(Stream source) : IAsyncDisposable
    {
        private readonly Stream _source = source;
        private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(4096);
        private readonly byte[] _pendingWrite = new byte[4096];
        private int _offset;
        private int _count;
        private int _pendingWriteCount;

        public async ValueTask<RawSseEvent?> TryReadEventAsync(Stream destination, CancellationToken ct)
        {
            var lines = new List<byte[]>();
            MemoryStream? line = new();
            var inspectedBytes = 0;
            var inspectable = true;
            var sawAny = false;
            var lineLength = 0;
            byte first = 0;
            byte second = 0;

            while (true)
            {
                var next = await ReadByteAsync(ct);
                if (next == null)
                {
                    await FlushPendingAsync(destination, ct);
                    line?.Dispose();
                    return sawAny ? new RawSseEvent(lines, inspectable) : null;
                }

                sawAny = true;
                await WriteByteAsync(destination, next.Value, ct);
                lineLength++;
                if (lineLength == 1) first = next.Value;
                else if (lineLength == 2) second = next.Value;

                if (inspectable)
                {
                    if (inspectedBytes == MaxInspectedEventChars)
                    {
                        inspectable = false;
                        line!.Dispose();
                        line = null;
                        lines.Clear();
                    }
                    else
                    {
                        line!.WriteByte(next.Value);
                        inspectedBytes++;
                    }
                }

                if (next.Value != (byte)'\n')
                    continue;

                var blank = lineLength == 1 || lineLength == 2 && first == (byte)'\r' && second == (byte)'\n';
                if (inspectable)
                {
                    lines.Add(line!.ToArray());
                    line.Dispose();
                    line = new MemoryStream();
                }

                if (!blank)
                {
                    lineLength = 0;
                    first = 0;
                    second = 0;
                    continue;
                }

                await FlushPendingAsync(destination, ct);
                line?.Dispose();
                return new RawSseEvent(lines, inspectable);
            }
        }

        private async ValueTask<byte?> ReadByteAsync(CancellationToken ct)
        {
            if (_offset == _count)
            {
                _count = await _source.ReadAsync(_buffer.AsMemory(), ct);
                _offset = 0;
                if (_count == 0)
                    return null;
            }
            return _buffer[_offset++];
        }

        private async ValueTask WriteByteAsync(Stream destination, byte value, CancellationToken ct)
        {
            _pendingWrite[_pendingWriteCount++] = value;
            if (_pendingWriteCount == _pendingWrite.Length)
                await FlushPendingAsync(destination, ct);
        }

        private async ValueTask FlushPendingAsync(Stream destination, CancellationToken ct)
        {
            if (_pendingWriteCount == 0)
                return;
            await destination.WriteAsync(_pendingWrite.AsMemory(0, _pendingWriteCount), ct);
            _pendingWriteCount = 0;
            await destination.FlushAsync(ct);
        }

        public ValueTask DisposeAsync()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            return ValueTask.CompletedTask;
        }
    }
}
