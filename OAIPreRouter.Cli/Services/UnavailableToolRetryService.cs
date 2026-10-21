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
        Action<string>? onRetry = null, ToolOutputContract contract = ToolOutputContract.LegacyRaw)
    {
        if (contract == ToolOutputContract.StrictNative)
            return await SendStrictAsync(send, body, streaming, opts, metrics, ct, onRetry);

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

    /// <summary>Typed, payload-free description of a strict tool-output rejection.</summary>
    public sealed record ToolGuardRejection(
        string Source,
        string Reason,
        string? RejectionId,
        string? Category);

    public const string GuardSource = "tensorfold.tool_output_guard";

    /// <summary>Parse the strict stack's terminal tool-output error.</summary>
    public static bool TryParseGuardRejection(string json, out ToolGuardRejection rejection)
    {
        rejection = null!;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return false;
            if (!string.Equals(GetString(error, "type"), "model_output_error", StringComparison.Ordinal)) return false;
            if (!string.Equals(GetString(error, "code"), ErrorCode, StringComparison.Ordinal)) return false;
            var source = GetString(error, "source");
            if (!string.Equals(source, GuardSource, StringComparison.Ordinal)) return false;
            var reason = GetString(error, "reason") ?? "invalid_executable_tool_output";
            // A decoder/prefix rewrite is not a model-correctable tool call. Do not retry it.
            if (string.Equals(reason, "stream_prefix_changed", StringComparison.Ordinal)) return false;
            var category = GetString(error, "category")
                           ?? GetString(error, "detail")
                           ?? GetString(error, "failure_category");
            rejection = new ToolGuardRejection(source!, reason, GetString(error, "rejection_id"), category);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Build the corrective user message for a strict guard rejection.</summary>
    public static string BuildStrictGuardBanner(string body, UnavailableToolRetryOptions opts, ToolGuardRejection rejection)
    {
        var sb = new StringBuilder((opts.StrictGuardBannerIntro ?? "").Trim());
        AppendSentence(sb, CategoryDetail(rejection.Category));
        if (opts.IncludeRequestContext)
            AppendSentence(sb, BuildStrictRequestContext(body));
        if (RequestForbidsTools(body))
        {
            AppendSentence(sb, "Do not emit any tool call or protocol markup.");
        }
        else
        {
            AppendSentence(sb, "Emit a complete tool call with JSON arguments that match the function schema.");
            AppendSentence(sb, "Do not emit partial, truncated, or malformed calls. Do not repeat the rejected markup.");
        }
        AppendSentence(sb, "Retry the original task now.");
        return sb.ToString();
    }

    private static bool RequestForbidsTools(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("tool_choice", out var choice) &&
                   choice.ValueKind == JsonValueKind.String &&
                   string.Equals(choice.GetString(), "none", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Build the retry body for a strict guard rejection.</summary>
    public static string? BuildStrictGuardRetryBody(string body, UnavailableToolRetryOptions opts, ToolGuardRejection rejection) =>
        JsonBodyRewriter.TryAppendUserMessage(body, opts.InjectionMarker ?? "", BuildStrictGuardBanner(body, opts, rejection));

    private static async Task<HttpResponseMessage> SendStrictAsync(
        Func<string, CancellationToken, Task<HttpResponseMessage>> send, string body, bool streaming,
        UnavailableToolRetryOptions opts, BridgeMetrics metrics, CancellationToken ct, Action<string>? onRetry)
    {
        var eligible = opts.Enabled && HasDeclaredTools(body);
        var maxRetries = Math.Clamp(opts.MaxRetries, 0, 1);
        var request = body;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            HttpResponseMessage response;
            try
            {
                response = await send(request, ct);
            }
            catch
            {
                if (attempt > 0) metrics.ToolRetryFail("transport");
                throw;
            }
            try
            {
                if (!eligible || maxRetries == 0) return response;

                if (streaming && IsEventStream(response))
                {
                    var stream = await response.Content.ReadAsStreamAsync(ct);
                    var preamble = await ReadPreambleAsync(stream, ct);
                    if (preamble.GuardRejection is { } rejection)
                    {
                        if (attempt == 0)
                        {
                            var retryBody = BuildStrictGuardRetryBody(body, opts, rejection);
                            if (retryBody != null)
                            {
                                response.Dispose();
                                metrics.UnavailableToolRetry();
                                onRetry?.Invoke(DescribeRetry(rejection));
                                request = retryBody;
                                continue;
                            }
                        }
                        metrics.ToolRetryFail(CategoryKey(rejection));
                        return FinishStrict(response, preamble, attempt, rejected: true);
                    }
                    if (attempt == 0 && preamble.ErrorFrame) metrics.ToolRetrySkippedNotGuard();
                    if (attempt > 0)
                    {
                        if (response.IsSuccessStatusCode && !preamble.ErrorFrame) metrics.UnavailableToolRetrySuccess();
                        else metrics.ToolRetryFail("second_attempt_error");
                    }
                    return FinishStrict(response, preamble, attempt, rejected: false);
                }

                await response.Content.LoadIntoBufferAsync(opts.MaxResponseBytes, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (TryParseGuardRejection(text, out var guardRejection))
                {
                    if (attempt == 0)
                    {
                        var retryBody = BuildStrictGuardRetryBody(body, opts, guardRejection);
                        if (retryBody != null)
                        {
                            response.Dispose();
                            metrics.UnavailableToolRetry();
                            onRetry?.Invoke(DescribeRetry(guardRejection));
                            request = retryBody;
                            continue;
                        }
                    }
                    metrics.ToolRetryFail(CategoryKey(guardRejection));
                    ReplaceContentWithString(response, text);
                    return FinishStrict(response, null, attempt, rejected: true);
                }
                if (attempt == 0 && IsErrorJson(text)) metrics.ToolRetrySkippedNotGuard();
                if (attempt > 0)
                {
                    if (response.IsSuccessStatusCode && !ContainsAnyError(text, false)) metrics.UnavailableToolRetrySuccess();
                    else metrics.ToolRetryFail("second_attempt_error");
                }
                ReplaceContentWithString(response, text);
                return FinishStrict(response, null, attempt, rejected: false);
            }
            catch
            {
                if (attempt > 0) metrics.ToolRetryFail("exception");
                response.Dispose();
                throw;
            }
        }
    }

    private static HttpResponseMessage FinishStrict(HttpResponseMessage response, SsePreamble? preamble, int attempt, bool rejected)
    {
        if (preamble != null) ReplaceContentWithPrefix(response, preamble.Bytes, preamble.Stream);
        response.Headers.Remove("X-PreRouter-Tool-Retries");
        response.Headers.TryAddWithoutValidation("X-PreRouter-Tool-Retries",
            attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Remove("X-PreRouter-Tool-Rejected");
        if (rejected) response.Headers.TryAddWithoutValidation("X-PreRouter-Tool-Rejected", ErrorCode);
        return response;
    }

    private static void ReplaceContentWithString(HttpResponseMessage response, string text)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        response.Content.Dispose();
        response.Content = new StringContent(text, Encoding.UTF8, mediaType);
    }

    private static bool IsEventStream(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

    private sealed record SsePreamble(byte[] Bytes, Stream Stream, bool Visible, ToolGuardRejection? GuardRejection, bool ErrorFrame);

    private const int MaxPreambleBytes = 64 * 1024;

    private static async Task<SsePreamble> ReadPreambleAsync(Stream stream, CancellationToken ct)
    {
        using var prefix = new MemoryStream();
        while (true)
        {
            if (prefix.Length > MaxPreambleBytes)
                return new SsePreamble(prefix.ToArray(), stream, Visible: true, GuardRejection: null, ErrorFrame: false);
            var (text, bytes) = await ReadLineAsync(stream, ct);
            if (bytes.Length == 0)
                return new SsePreamble(prefix.ToArray(), stream, Visible: false, GuardRejection: null, ErrorFrame: false);
            prefix.Write(bytes);
            var line = text.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith(":", StringComparison.Ordinal)) continue;
            if (line.StartsWith("event:", StringComparison.Ordinal) ||
                line.StartsWith("id:", StringComparison.Ordinal) ||
                line.StartsWith("retry:", StringComparison.Ordinal)) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]")
                return new SsePreamble(prefix.ToArray(), stream, Visible: true, GuardRejection: null, ErrorFrame: false);
            if (TryParseGuardRejection(payload, out var rejection))
                return new SsePreamble(prefix.ToArray(), stream, Visible: false, rejection, ErrorFrame: true);
            return new SsePreamble(prefix.ToArray(), stream, Visible: true, GuardRejection: null, ErrorFrame: IsErrorJson(payload));
        }
    }

    private static async Task<(string Text, byte[] Bytes)> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var one = new byte[1];
        while (await stream.ReadAsync(one.AsMemory(), ct) is var read && read > 0)
        {
            bytes.WriteByte(one[0]);
            if (one[0] == (byte)'\n') break;
        }
        var data = bytes.ToArray();
        return (Encoding.UTF8.GetString(data), data);
    }

    private static string DescribeRetry(ToolGuardRejection rejection) =>
        "strict_guard_rejection category=" + (rejection.Category ?? "unknown") +
        " rejection_id=" + (rejection.RejectionId ?? "none");

    private static string CategoryKey(ToolGuardRejection rejection) =>
        NormalizeCategory(rejection.Category) ?? "strict_guard_rejection";

    private static string CategoryDetail(string? category) =>
        NormalizeCategory(category) switch
        {
            "unavailable_tool" => "The previous call named a tool that is not offered in this request.",
            "schema_mismatch" => "The previous call arguments did not match the offered function schema.",
            "truncated_call" => "The previous call was incomplete or truncated.",
            "malformed_call" => "The previous call was malformed.",
            "duplicate_call_id" => "The previous response reused a tool call id.",
            "too_many_calls" => "The previous response emitted too many tool calls.",
            "parallel_calls_disabled" => "The previous response emitted more than one tool call, but parallel calls are disabled.",
            "tool_choice_disallowed" => "The previous call did not respect the request tool_choice.",
            "required_tool_missing" => "The previous response did not include the required tool call.",
            _ => "The previous tool output was rejected by the strict validator."
        };

    private static string? NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;
        return category.Trim().ToLowerInvariant() switch
        {
            "unavailable_tool" or "unavailable_tool_call" or "unknown_tool" => "unavailable_tool",
            "schema_mismatch" or "schema_invalid" or "invalid_arguments" => "schema_mismatch",
            "truncated_call" or "truncated" => "truncated_call",
            "malformed_call" or "malformed" => "malformed_call",
            "duplicate_call_id" or "duplicate_id" => "duplicate_call_id",
            "too_many_calls" or "max_calls" => "too_many_calls",
            "parallel_calls_disabled" or "parallel_tool_calls_disabled" => "parallel_calls_disabled",
            "tool_choice_disallowed" or "disallowed_by_tool_choice" => "tool_choice_disallowed",
            "required_tool_missing" => "required_tool_missing",
            var other => other
        };
    }

    private static void AppendSentence(StringBuilder sb, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1])) sb.Append(' ');
        sb.Append(text.Trim());
    }

    private static string BuildStrictRequestContext(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";
            var parts = new List<string>();
            if (root.TryGetProperty("tool_choice", out var choice))
            {
                if (choice.ValueKind == JsonValueKind.String &&
                    string.Equals(choice.GetString(), "none", StringComparison.Ordinal))
                    return "Do not call any tool. Answer the original request in prose.";
                if (choice.ValueKind == JsonValueKind.Object &&
                    choice.TryGetProperty("function", out var fn) &&
                    fn.ValueKind == JsonValueKind.Object &&
                    fn.TryGetProperty("name", out var forcedName) &&
                    forcedName.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(forcedName.GetString()))
                {
                    parts.Add($"Call exactly the function `{forcedName.GetString()}`. Do not call any other tool.");
                }
            }
            if (parts.Count == 0)
            {
                var names = GetToolNames(root);
                if (names.Count > 0)
                {
                    const int maxNames = 20;
                    var shown = names.Take(maxNames).ToList();
                    var suffix = names.Count > maxNames ? $" and {names.Count - maxNames} more" : "";
                    parts.Add("Use only these offered functions: " +
                              string.Join(", ", shown.Select(name => $"`{name}`")) + suffix + ".");
                }
            }
            if (root.TryGetProperty("parallel_tool_calls", out var parallel) &&
                parallel.ValueKind == JsonValueKind.False)
                parts.Add("Emit exactly one tool call.");
            if (root.TryGetProperty("max_calls", out var maxCalls) &&
                maxCalls.ValueKind == JsonValueKind.Number &&
                maxCalls.TryGetInt32(out var max) && max >= 0)
                parts.Add(max == 0 ? "Do not emit any tool call." : $"Emit at most {max} tool call{(max == 1 ? "" : "s")}.");
            return string.Join(" ", parts);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static List<string> GetToolNames(JsonElement root)
    {
        var names = new List<string>();
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            return names;
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object) continue;
            if (tool.TryGetProperty("function", out var function) &&
                function.ValueKind == JsonValueKind.Object &&
                function.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(name.GetString()))
                names.Add(name.GetString()!);
            else if (tool.TryGetProperty("name", out var directName) &&
                     directName.ValueKind == JsonValueKind.String &&
                     !string.IsNullOrWhiteSpace(directName.GetString()))
                names.Add(directName.GetString()!);
        }
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
