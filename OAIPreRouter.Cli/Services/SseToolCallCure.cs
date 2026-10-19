using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OAIPreRouter.Cli.Services;

public static class SseToolCallCure
{
    private const int MaxBufferedChars = 4 * 1024 * 1024;

    public static async Task PipeAsync(Func<Stream, Task> produce, Stream destination, ISet<string> names, CancellationToken ct,
        Action<int>? onConverted = null)
    {
        var pipe = new Pipe();
        var producer = Produce();
        var consumer = Consume();
        await Task.WhenAll(producer, consumer);

        async Task Produce()
        {
            Exception? error = null;
            try { await produce(pipe.Writer.AsStream(leaveOpen: true)); }
            catch (Exception ex) { error = ex; throw; }
            finally { await pipe.Writer.CompleteAsync(error); }
        }
        async Task Consume()
        {
            try { await ProcessAsync(pipe.Reader.AsStream(leaveOpen: true), destination, names, ct, onConverted); }
            finally { await pipe.Reader.CompleteAsync(); }
        }
    }

    public static async Task ProcessAsync(Stream source, Stream destination, ISet<string> names, CancellationToken ct,
        Action<int>? onConverted = null)
    {
        using var reader = new StreamReader(source, Encoding.UTF8, leaveOpen: true);
        var pending = new Dictionary<int, StringBuilder>();
        var detected = new HashSet<int>();
        var native = new HashSet<int>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                await Write(line + "\n");
                continue;
            }
            var payload = line[5..].Trim();
            if (payload == "[DONE]")
            {
                if (pending.Values.Any(t => t.Length > 0)) throw new InvalidDataException("Unfinished raw tool-call stream.");
                await Write(line + "\n");
                continue;
            }
            JsonNode? root;
            try { root = JsonNode.Parse(payload); }
            catch (JsonException) { await Write(line + "\n"); continue; }
            if (root is not JsonObject || root["choices"] is not JsonArray choices) { await Write(line + "\n"); continue; }
            foreach (var choice in choices)
            {
                if (choice?["delta"] is not JsonObject delta) continue;
                var index = choice["index"]?.GetValue<int>() ?? 0;
                var text = delta["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                if (!pending.TryGetValue(index, out var buffer)) pending[index] = buffer = new StringBuilder();
                if (delta["tool_calls"] is JsonArray { Count: > 0 }) native.Add(index);
                if (native.Contains(index))
                {
                    if (buffer.Length > 0) throw new InvalidDataException("Mixed native and raw tool calls.");
                    continue;
                }
                buffer.Append(text);
                if (buffer.Length > MaxBufferedChars) throw new InvalidDataException("Raw tool-call buffer exceeded character limit.");
                var start = detected.Contains(index) ? 0 : RawToolCallCure.FindStart(buffer.ToString());
                var terminal = choice["finish_reason"] is JsonValue finish && finish.TryGetValue<string>(out var reason) && reason != null;
                if (terminal && start >= 0)
                {
                    if (choice["finish_reason"]!.GetValue<string>() is not ("stop" or "tool_calls") ||
                        !RawToolCallCure.TryParse(buffer.ToString(), names, out var calls, out var content))
                        throw new InvalidDataException("Incomplete or invalid raw tool call; refusing to execute it.");
                    for (var i = 0; i < calls.Count; i++) calls[i]!["index"] = i;
                    delta["tool_calls"] = calls;
                    delta["content"] = content.Length == 0 ? null : JsonValue.Create(content);
                    choice["finish_reason"] = "tool_calls";
                    buffer.Clear();
                    detected.Remove(index);
                    onConverted?.Invoke(calls.Count);
                }
                else if (start >= 0)
                {
                    // Once detected, append without rescanning/copying a growing file payload.
                    delta["content"] = buffer.ToString(0, start);
                    buffer.Remove(0, start);
                    detected.Add(index);
                }
                else
                {
                    var buffered = buffer.ToString();
                    var keep = terminal ? 0 : RawToolCallCure.PartialSuffixLength(buffered);
                    var safe = buffered[..(buffered.Length - keep)];
                    buffer.Remove(0, buffered.Length - keep);
                    if (text.Length > 0 || safe.Length > 0) delta["content"] = safe;
                }
            }
            await Write("data: " + root!.ToJsonString() + "\n");
        }
        if (pending.Values.Any(t => t.Length > 0)) throw new InvalidDataException("Truncated raw tool-call stream.");

        async Task Write(string text)
        {
            await destination.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
            await destination.FlushAsync(ct);
        }
    }
}
