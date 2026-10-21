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
            await using var stream = new FlushOnWriteStream(pipe.Writer.AsStream(leaveOpen: true));
            try { await produce(stream); }
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
        var native = new HashSet<int>();
        var rejected = false;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (rejected) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                await Write(line + "\n");
                continue;
            }
            var payload = line[5..].Trim();
            if (payload == "[DONE]")
            {
                if (pending.Values.Any(t => RawToolCallCure.FindStart(t.ToString()) >= 0))
                {
                    await Reject("incomplete_raw_call");
                    rejected = true;
                    continue;
                }
                if (pending.Values.Any(t => t.Length > 0))
                {
                    await RejectIncompleteStream();
                    rejected = true;
                    continue;
                }
                await Write(line + "\n");
                continue;
            }
            JsonNode? root;
            try { root = JsonNode.Parse(payload); }
            catch (JsonException) { await Write(line + "\n"); continue; }
            if (root is not JsonObject || root["choices"] is not JsonArray choices)
            {
                // Preserve upstream error codes verbatim; they take precedence over
                // any incomplete local candidate buffered before the error frame.
                if (root is JsonObject errorRoot && errorRoot["error"] is JsonObject) pending.Clear();
                await Write(line + "\n");
                continue;
            }
            foreach (var choice in choices)
            {
                if (choice?["delta"] is not JsonObject delta) continue;
                var index = choice["index"]?.GetValue<int>() ?? 0;
                var text = delta["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                if (!pending.TryGetValue(index, out var buffer)) pending[index] = buffer = new StringBuilder();
                if (delta["tool_calls"] is JsonArray { Count: > 0 }) native.Add(index);
                buffer.Append(text);
                if (buffer.Length > MaxBufferedChars)
                {
                    await Reject("buffer_limit");
                    rejected = true;
                    break;
                }
                var terminal = choice["finish_reason"] is JsonValue finish && finish.TryGetValue<string>(out var reason) && reason != null;
                if (terminal)
                {
                    var complete = buffer.ToString();
                    var hasRawCall = RawToolCallCure.FindStart(complete) >= 0;
                    if (native.Contains(index) && hasRawCall)
                    {
                        await Reject("mixed_native_raw_calls");
                        rejected = true;
                        break;
                    }
                    if (hasRawCall)
                    {
                        var finishReason = choice["finish_reason"]!.GetValue<string>();
                        if (finishReason is not ("stop" or "tool_calls"))
                        {
                            await Reject("non_tool_finish");
                            rejected = true;
                            break;
                        }
                        if (!RawToolCallCure.TryParse(complete, names, out var calls, out var content))
                        {
                            await Reject("invalid_raw_call");
                            rejected = true;
                            break;
                        }
                        for (var i = 0; i < calls.Count; i++) calls[i]!["index"] = i;
                        delta["tool_calls"] = calls;
                        delta["content"] = content.Length == 0 ? null : JsonValue.Create(content);
                        choice["finish_reason"] = "tool_calls";
                        onConverted?.Invoke(calls.Count);
                    }
                    else if (complete.Length > 0)
                        delta["content"] = complete;
                    buffer.Clear();
                }
                else
                {
                    // Buffer whole choice content so lexical state spans arbitrary SSE chunks.
                    if (text.Length > 0) delta["content"] = null;
                }
            }
            if (rejected) continue;
            await Write("data: " + root!.ToJsonString() + "\n");
        }
        if (!rejected && pending.Values.Any(t => RawToolCallCure.FindStart(t.ToString()) >= 0))
            await Reject("incomplete_raw_call");
        else if (!rejected && pending.Values.Any(t => t.Length > 0))
            await RejectIncompleteStream();

        async Task Write(string text)
        {
            await destination.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
            await destination.FlushAsync(ct);
        }

        async Task Reject(string reason)
        {
            await WriteError(UnavailableToolRetryService.RawToolErrorCode, reason,
                "The model response contained an invalid raw tool call (reason: " + reason + ").");
        }

        async Task RejectIncompleteStream()
        {
            await WriteError("upstream_stream_incomplete", "missing_finish",
                "The upstream stream ended before completion (reason: missing_finish).");
        }

        async Task WriteError(string code, string reason, string message)
        {
            var error = JsonSerializer.Serialize(new { error = new
            {
                type = "model_output_error", code, source = "mediallmproxy.raw_tool_guard", reason, message
            } });
            await Write("data: " + error + "\n\n");
            await Write("data: [DONE]\n\n");
        }
    }

    private sealed class FlushOnWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            inner.Flush();
        }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await inner.WriteAsync(buffer, offset, count, ct);
            await inner.FlushAsync(ct);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            await inner.FlushAsync(ct);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
