namespace OAIPreRouter.Cli.Services;

using System.Text.Json;
using System.Net;
using System.Text;
using OAIPreRouter.Cli.Models;

/// <summary>
/// Recovers once from the stack's <c>unavailable_tool_call</c> error or a proxy
/// raw-tool validation abort. The stack returns:
///   non-streaming: HTTP 500 + {"error":{"type":"model_output_error","code":"unavailable_tool_call",...}}
///   streaming:     HTTP 200 + one SSE error frame with the same error object, then [DONE]
/// The retry appends a corrective user-turn banner without changing available tools
/// or thinking mode. Modeled on LoopGuardService.
/// </summary>
public static class UnavailableToolRetryService
{
    public const string ErrorCode = "unavailable_tool_call";
    public const string RawToolErrorCode = "invalid_raw_tool_call";

    /// <summary>True when the JSON body is the stack's unavailable-tool error object.</summary>
    public static bool IsErrorJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.Object &&
                   error.TryGetProperty("type", out var type) &&
                   type.ValueKind == JsonValueKind.String && type.GetString() == "model_output_error" &&
                   error.TryGetProperty("code", out var code) &&
                   code.ValueKind == JsonValueKind.String &&
                   code.GetString() == ErrorCode;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>True when an SSE stream contains the stack's unavailable-tool error frame.</summary>
    public static bool ContainsErrorFrame(string sseText)
    {
        foreach (var line in sseText.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
                continue;
            if (IsErrorJson(payload))
                return true;
        }
        return false;
    }

    /// <summary>True when the request declares a non-empty tools array. The stack's guard
    /// applies to tool-enabled requests, including tool_choice: "none".</summary>
    public static bool HasDeclaredTools(string request)
    {
        try
        {
            using var doc = JsonDocument.Parse(request);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("tools", out var tools) &&
                   tools.ValueKind == JsonValueKind.Array &&
                   tools.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Build the retry body: original request + one new user message carrying
    /// the marker and banner. Returns null when the body cannot be rewritten.</summary>
    public static string? BuildRetryBody(string body, UnavailableToolRetryOptions opts, bool rawToolAbort = false)
    {
        return JsonBodyRewriter.TryAppendUserMessage(body, opts.InjectionMarker, rawToolAbort ? opts.RawToolCallBanner : opts.Banner);
    }

    /// <summary>
    /// Non-streaming responses are buffered and validated before exposing output. A streaming
    /// response is retried only when its first SSE data frame is the unavailable-tool error;
    /// once any frame can be forwarded, replaying would duplicate visible model output.
    /// The returned response is owned by the caller. Transport failures are not retried.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        Func<string, CancellationToken, Task<HttpResponseMessage>> send, string body, bool streaming,
        bool cureRawTools, UnavailableToolRetryOptions opts, BridgeMetrics metrics, CancellationToken ct,
        Action<string>? onRetry = null)
    {
        var request = body;
        var eligible = opts.Enabled && HasDeclaredTools(body);
        var names = eligible && cureRawTools ? RawToolCallCure.AllowedNames(body) : new HashSet<string>();
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            HttpResponseMessage response;
            try { response = await send(request, ct); }
            catch
            {
                if (attempt > 0) metrics.UnavailableToolRetryFail();
                throw;
            }
            try
            {
                if (!eligible) return response;
                if (streaming && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                {
                    var first = await ReadSseLineAsync(await response.Content.ReadAsStreamAsync(ct), ct);
                    if (IsUnavailableErrorFrame(first.Text))
                    {
                        var earlyRetryBody = attempt == 0 && opts.MaxRetries > 0 ? BuildRetryBody(body, opts) : null;
                        if (earlyRetryBody != null)
                        {
                            response.Dispose();
                            metrics.UnavailableToolRetry();
                            onRetry?.Invoke(ErrorCode);
                            request = earlyRetryBody;
                            continue;
                        }
                        response.Headers.Remove("X-PreRouter-Tool-Rejected");
                        response.Headers.TryAddWithoutValidation("X-PreRouter-Tool-Rejected", ErrorCode);
                    }
                    ReplaceContentWithPrefix(response, first.Bytes, first.Stream);
                    response.Headers.Remove("X-PreRouter-Tool-Retries");
                    response.Headers.TryAddWithoutValidation("X-PreRouter-Tool-Retries", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return response;
                }
                await response.Content.LoadIntoBufferAsync(opts.MaxResponseBytes, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                var unavailable = IsErrorJson(text) || (streaming && ContainsErrorFrame(text));
                var rawAbort = false;
                if (!ContainsAnyError(text, streaming) && cureRawTools && names.Count > 0 && response.IsSuccessStatusCode)
                {
                    if (streaming && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                    {
                        using var source = new MemoryStream(Encoding.UTF8.GetBytes(text));
                        using var destination = new MemoryStream();
                        try
                        {
                            await SseToolCallCure.ProcessAsync(source, destination, names, ct);
                            text = Encoding.UTF8.GetString(destination.ToArray());
                        }
                        catch (InvalidDataException) { rawAbort = true; }
                    }
                    else if (!streaming)
                    {
                        var rewritten = RawToolCallCure.RewriteJson(text, names);
                        using var doc = JsonDocument.Parse(rewritten);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                            foreach (var choice in choices.EnumerateArray())
                                if (choice.TryGetProperty("message", out var message) &&
                                    message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String &&
                                    !(message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0) &&
                                    RawToolCallCure.FindStart(content.GetString()!) >= 0)
                                    rawAbort = true;
                        text = rewritten;
                    }
                }

                var rejected = unavailable || rawAbort;
                var retryBody = rejected && attempt == 0 && opts.MaxRetries > 0 && (!rawAbort || opts.RetryRawToolCallAborts)
                    ? BuildRetryBody(body, opts, rawAbort) : null;
                if (retryBody != null)
                {
                    response.Dispose();
                    metrics.UnavailableToolRetry();
                    onRetry?.Invoke(rawAbort ? RawToolErrorCode : ErrorCode);
                    request = retryBody;
                    continue;
                }

                if (rawAbort)
                {
                    var error = JsonSerializer.Serialize(new { error = new
                    {
                        type = "model_output_error", code = RawToolErrorCode,
                        message = "The model response contained an invalid or incomplete raw tool call."
                    } });
                    text = streaming ? "data: " + error + "\n\ndata: [DONE]\n\n" : error;
                    response.StatusCode = streaming ? HttpStatusCode.OK : HttpStatusCode.BadGateway;
                }
                if (attempt > 0)
                {
                    if (rejected || !response.IsSuccessStatusCode || ContainsAnyError(text, streaming)) metrics.UnavailableToolRetryFail();
                    else metrics.UnavailableToolRetrySuccess();
                }
                response.Headers.Remove("X-PreRouter-Tool-Retries");
                response.Headers.TryAddWithoutValidation("X-PreRouter-Tool-Retries", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
                response.Headers.Remove("X-PreRouter-Tool-Rejected");
                if (rejected) response.Headers.TryAddWithoutValidation("X-PreRouter-Tool-Rejected", rawAbort ? RawToolErrorCode : ErrorCode);
                var mediaType = rawAbort ? streaming ? "text/event-stream" : "application/json"
                    : response.Content.Headers.ContentType?.MediaType ?? (streaming ? "text/event-stream" : "application/json");
                response.Content.Dispose();
                response.Content = new StringContent(text, Encoding.UTF8, mediaType);
                return response;
            }
            catch
            {
                if (attempt > 0) metrics.UnavailableToolRetryFail();
                response.Dispose();
                throw;
            }
        }
    }

    private static bool ContainsAnyError(string text, bool streaming)
    {
        foreach (var line in streaming ? text.Split('\n') : new[] { text })
        {
            var payload = streaming ? line.StartsWith("data:", StringComparison.Ordinal) ? line[5..].Trim() : "" : line;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out _)) return true;
            }
            catch (JsonException) { }
        }
        return false;
    }

    private static bool IsUnavailableErrorFrame(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return false;
        var payload = line[5..].Trim();
        return payload.Length > 0 && payload != "[DONE]" && IsErrorJson(payload);
    }

    private static async Task<(string Text, byte[] Bytes, Stream Stream)> ReadSseLineAsync(Stream stream, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var one = new byte[1];
        while (await stream.ReadAsync(one.AsMemory(), ct) is var read && read > 0)
        {
            bytes.WriteByte(one[0]);
            if (one[0] == (byte)'\n') break;
        }
        var prefix = bytes.ToArray();
        return (Encoding.UTF8.GetString(prefix).TrimEnd('\r', '\n'), prefix, stream);
    }

    private static void ReplaceContentWithPrefix(HttpResponseMessage response, byte[] prefix, Stream stream)
    {
        var headers = response.Content.Headers.ToArray();
        var content = new StreamContent(new PrefixStream(prefix, stream));
        foreach (var header in headers) content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = content;
    }

    private sealed class PrefixStream(byte[] prefix, Stream inner) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (position < prefix.Length)
            {
                var copied = Math.Min(buffer.Length, prefix.Length - position);
                prefix.AsSpan(position, copied).CopyTo(buffer);
                position += copied;
                return copied;
            }
            return inner.Read(buffer);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (position < prefix.Length)
            {
                var copied = Math.Min(buffer.Length, prefix.Length - position);
                prefix.AsMemory(position, copied).CopyTo(buffer);
                position += copied;
                return copied;
            }
            return await inner.ReadAsync(buffer, ct);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
