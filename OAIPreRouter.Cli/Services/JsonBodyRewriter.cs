namespace OAIPreRouter.Cli.Services;

using System.Text;
using System.Text.Json;
using OAIPreRouter.Cli.Models;

public static class JsonBodyRewriter
{
    /// <summary>
    /// Prepends a system message to the messages array of a chat-completions body.
    /// Returns null when the body has no messages array (nothing to inject into).
    /// The injected message lands BEFORE the client's own messages (including its
    /// system prompt), so it acts as a guard that cannot be overridden by later content.
    /// </summary>
    public static string? TryInjectSystemPrompt(string json, string prompt)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
                return null;

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("messages"))
                    {
                        writer.WritePropertyName("messages");
                        writer.WriteStartArray();
                        writer.WriteStartObject();
                        writer.WriteString("role", "system");
                        writer.WriteString("content", prompt);
                        writer.WriteEndObject();
                        foreach (var msg in messages.EnumerateArray())
                            msg.WriteTo(writer);
                        writer.WriteEndArray();
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    public static string? TryRewriteModel(string json, string localModel)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);

            writer.WriteStartObject();

            var wroteModel = false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("model"))
                {
                    writer.WriteString("model", localModel);
                    wroteModel = true;
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            if (!wroteModel)
                writer.WriteString("model", localModel);

            writer.WriteEndObject();
            writer.Flush();

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sets a top-level numeric sampling field (e.g. "temperature" or "top_p") on a
    /// chat-completions body, replacing any client-supplied value. Mirrors the
    /// opencode-compat-proxy model-alias override: applied LAST so configured
    /// sampling cannot be superseded by the caller. Returns null on parse failure
    /// (caller then keeps the original body).
    /// </summary>
    public static string? TryRewriteSampling(string json, string field, double value)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                var wrote = false;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals(field))
                    {
                        writer.WriteNumber(field, value);
                        wrote = true;
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                if (!wrote)
                    writer.WriteNumber(field, value);
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Rewrites media parts out and observations in, in ONE validated pass.
    /// When <paramref name="allMedia"/> is provided AND opts.RehomeToolMedia is true, media parts
    /// found inside role:"tool" messages that are NOT part of <paramref name="parts"/> (i.e. not
    /// behind an open detour gate) are MOVED into a fresh role:"user" message inserted immediately
    /// after the tool message — byte-for-byte, so backends that only accept media in user messages
    /// (DeepSeek vLLM: "Images are supported in user messages only") still see the pixels natively.
    /// Returns null on parse failure (caller then fails closed with 502).
    /// </summary>
    public static string? TryRewriteMedia(string json,
        IReadOnlyList<MediaContentScanner.MediaPart> parts,
        IReadOnlyDictionary<int, string> observationsByMessageIndex,
        MultimodalOptions opts,
        IReadOnlyList<MediaContentScanner.MediaPart>? allMedia = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();

                // Find the first user message index in the original array
                var firstUserIdx = -1;
                JsonElement? messagesElem = null;
                int messagesLength = 0;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("messages"))
                    {
                        messagesElem = prop.Value;
                        messagesLength = prop.Value.GetArrayLength();
                        for (var i = 0; i < messagesLength; i++)
                        {
                            var msg = prop.Value[i];
                            if (msg.ValueKind == JsonValueKind.Object &&
                                msg.TryGetProperty("role", out var r) &&
                                r.ValueKind == JsonValueKind.String &&
                                r.GetString() == "user")
                            {
                                firstUserIdx = i;
                                break;
                            }
                        }
                        break;
                    }
                }

                // Build a lookup for media parts to strip: (MessageIndex, PartIndex) -> true
                var stripSet = new HashSet<(int msgIdx, int partIdx)>();
                foreach (var part in parts)
                {
                    stripSet.Add((part.MessageIndex, part.PartIndex));
                }

                // RehomeAtEnd: rehomed user messages are buffered here (raw JSON) and appended
                // after the last original message instead of after their tool message.
                List<string>? deferredRehome = opts.RehomeAtEnd ? new List<string>() : null;

                // Build the re-home map: media parts inside role:"tool" messages that survive the
                // rewrite (not in stripSet) are moved into a fresh role:"user" message inserted
                // immediately after the tool message. This is the DeepSeek-shaped backend fix —
                // those templates only inject images in user turns and 400 on tool-message media.
                var rehomeByMessageIndex = new Dictionary<int, List<int>>();
                if (opts.RehomeToolMedia && allMedia != null)
                {
                    // First pass: remember which message indexes are role:"tool".
                    var toolIndexes = new HashSet<int>();
                    for (var i = 0; i < messagesLength; i++)
                    {
                        var m = messagesElem!.Value[i];
                        if (m.ValueKind == JsonValueKind.Object &&
                            m.TryGetProperty("role", out var r) &&
                            r.ValueKind == JsonValueKind.String &&
                            r.GetString() == "tool")
                        {
                            toolIndexes.Add(i);
                        }
                    }

                    foreach (var part in allMedia)
                    {
                        if (!toolIndexes.Contains(part.MessageIndex))
                            continue;
                        if (stripSet.Contains((part.MessageIndex, part.PartIndex)))
                            continue; // open-gate media: stripped + observed, nothing left to re-home
                        if (!rehomeByMessageIndex.TryGetValue(part.MessageIndex, out var list))
                            rehomeByMessageIndex[part.MessageIndex] = list = new List<int>();
                        if (!list.Contains(part.PartIndex))
                            list.Add(part.PartIndex);
                    }

                    // Rehomed parts are STRIPPED from the tool message (they live on in the
                    // injected user message) — otherwise the pixels would travel twice.
                    foreach (var kv in rehomeByMessageIndex)
                    {
                        foreach (var pi in kv.Value)
                            stripSet.Add((kv.Key, pi));
                    }
                }

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("messages"))
                    {
                        writer.WritePropertyName("messages");
                        writer.WriteStartArray();

                        for (var i = 0; i < messagesLength; i++)
                        {
                            // Insert policy system message before the first user message
                            if (i == firstUserIdx && firstUserIdx >= 0)
                            {
                                writer.WriteStartObject();
                                writer.WriteString("role", "system");
                                writer.WriteString("content", opts.PolicySystemPrompt);
                                writer.WriteEndObject();
                            }

                            var msg = messagesElem.Value[i];
                            writer.WriteStartObject();

                            // Write role
                            if (msg.TryGetProperty("role", out var role))
                            {
                                writer.WritePropertyName("role");
                                writer.WriteStringValue(role.GetString());
                            }

                            // Write content (rewritten)
                            if (msg.TryGetProperty("content", out var content))
                            {
                                writer.WritePropertyName("content");
                                WriteRewrittenContent(writer, content, i, stripSet,
                                    observationsByMessageIndex, opts,
                                    rehomed: rehomeByMessageIndex.ContainsKey(i));
                            }
                            else
                            {
                                writer.WriteNull("content");
                            }

                            // Write any remaining properties (tool_calls, etc.)
                            foreach (var prop2 in msg.EnumerateObject())
                            {
                                if (prop2.Name != "role" && prop2.Name != "content")
                                {
                                    prop2.WriteTo(writer);
                                }
                            }

                            writer.WriteEndObject();

                            // Re-home: emit a fresh role:"user" message carrying the tool message's
                            // media parts byte-for-byte, so backends that only accept media in user
                            // messages (DeepSeek vLLM) still receive the pixels natively. Default
                            // placement: immediately after the tool message — protocol-valid: assistant
                            // tool_calls → tool result → user turn. With RehomeAtEnd the message is
                            // deferred to the end of the array instead (mlx-serve splices vision
                            // tokens into the FINAL user turn only; mid-conversation images are
                            // silently dropped — see MultimodalOptions.RehomeAtEnd).
                            if (rehomeByMessageIndex.TryGetValue(i, out var rehomeParts))
                            {
                                rehomeParts.Sort();
                                if (deferredRehome != null)
                                {
                                    using var sideMs = new MemoryStream();
                                    using (var sideWriter = new Utf8JsonWriter(sideMs))
                                    {
                                        WriteRehomeUserMessage(sideWriter, msg, rehomeParts, opts);
                                    }
                                    deferredRehome.Add(Encoding.UTF8.GetString(sideMs.ToArray()));
                                }
                                else
                                {
                                    WriteRehomeUserMessage(writer, msg, rehomeParts, opts);
                                }
                            }
                        }

                        // RehomeAtEnd: append the deferred rehomed user messages after every
                        // original message, so the image lands in the final user turn.
                        if (deferredRehome != null)
                        {
                            foreach (var raw in deferredRehome)
                            {
                                using var parsed = JsonDocument.Parse(raw);
                                parsed.RootElement.WriteTo(writer);
                            }
                        }

                        writer.WriteEndArray();
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the rehomed role:"user" message: marker text part, optional durable-record
    /// instruction, then the media parts byte-for-byte from the source tool message.
    /// Shared by both placements (after-tool-message and end-of-array).
    /// </summary>
    private static void WriteRehomeUserMessage(Utf8JsonWriter writer, JsonElement msg,
        List<int> rehomeParts, MultimodalOptions opts)
    {
        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WritePropertyName("content");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", opts.RehomeMarker);
        writer.WriteEndObject();
        // Durable-record instruction: the model writes its own description
        // into the answer so the record survives in client history — the
        // pixels themselves are only visible on this turn.
        if (!string.IsNullOrWhiteSpace(opts.RehomePersistPrompt))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", opts.RehomePersistPrompt);
            writer.WriteEndObject();
        }
        if (msg.TryGetProperty("content", out var srcContent) &&
            srcContent.ValueKind == JsonValueKind.Array)
        {
            foreach (var pi in rehomeParts)
            {
                if (pi >= 0 && pi < srcContent.GetArrayLength())
                    srcContent[pi].WriteTo(writer);
            }
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Appends a gated observation block to choices[0].message.content of a NON-streaming
    /// chat-completions response, so the observation becomes durable in the client's history.
    /// Returns null on parse failure or when the shape is unexpected (caller keeps the raw body).
    /// </summary>
    public static string? TryAppendObservationToJson(string json, string observationBlock)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return null;
            var choice = choices[0];
            if (!choice.TryGetProperty("message", out var message))
                return null;

            var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.NameEquals("choices"))
                    {
                        writer.WritePropertyName("choices");
                        writer.WriteStartArray();
                        var first = true;
                        foreach (var ch in choices.EnumerateArray())
                        {
                            writer.WriteStartObject();
                            foreach (var cp in ch.EnumerateObject())
                            {
                                if (first && cp.NameEquals("message"))
                                {
                                    writer.WritePropertyName("message");
                                    writer.WriteStartObject();
                                    foreach (var mp in cp.Value.EnumerateObject())
                                    {
                                        if (mp.NameEquals("content"))
                                            writer.WriteString("content", content + "\n\n" + observationBlock);
                                        else
                                            mp.WriteTo(writer);
                                    }
                                    writer.WriteEndObject();
                                }
                                else
                                {
                                    cp.WriteTo(writer);
                                }
                            }
                            writer.WriteEndObject();
                            first = false;
                        }
                        writer.WriteEndArray();
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Appends a loop-guard nudge to the LAST user message (string content → appended
    /// string; array content → appended text part; no user message → new user message
    /// inserted after the last message). Returns null on parse failure.
    /// </summary>
    public static string? TryInjectLoopGuardNudge(string json, string marker, string nudge)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
                return null;

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!prop.NameEquals("messages")) { prop.WriteTo(writer); continue; }
                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();
                    var lastUserIdx = -1;
                    for (var i = 0; i < messages.GetArrayLength(); i++)
                    {
                        var m = messages[i];
                        if (m.ValueKind == JsonValueKind.Object &&
                            m.TryGetProperty("role", out var r) &&
                            r.ValueKind == JsonValueKind.String && r.GetString() == "user")
                            lastUserIdx = i;
                    }
                    var injected = false;
                    for (var i = 0; i < messages.GetArrayLength(); i++)
                    {
                        var m = messages[i];
                        if (i == lastUserIdx && !injected)
                        {
                            writer.WriteStartObject();
                            foreach (var mp in m.EnumerateObject())
                            {
                                if (mp.NameEquals("content") && mp.Value.ValueKind == JsonValueKind.String)
                                {
                                    writer.WritePropertyName("content");
                                    writer.WriteStringValue(mp.Value.GetString() + "\n\n" + marker + nudge);
                                }
                                else if (mp.NameEquals("content") && mp.Value.ValueKind == JsonValueKind.Array)
                                {
                                    writer.WritePropertyName("content");
                                    writer.WriteStartArray();
                                    foreach (var part in mp.Value.EnumerateArray())
                                        part.WriteTo(writer);
                                    writer.WriteStartObject();
                                    writer.WriteString("type", "text");
                                    writer.WriteString("text", marker + nudge);
                                    writer.WriteEndObject();
                                    writer.WriteEndArray();
                                }
                                else
                                {
                                    mp.WriteTo(writer);
                                }
                            }
                            writer.WriteEndObject();
                            injected = true;
                        }
                        else
                        {
                            m.WriteTo(writer);
                        }
                    }
                    if (!injected)
                    {
                        // No user message: insert one after the last message.
                        writer.WriteStartObject();
                        writer.WriteString("role", "user");
                        writer.WritePropertyName("content");
                        writer.WriteStringValue(marker + nudge);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static void WriteRewrittenContent(Utf8JsonWriter writer, JsonElement content, int messageIndex,
        HashSet<(int msgIdx, int partIdx)> stripSet,
        IReadOnlyDictionary<int, string> observationsByMessageIndex,
        MultimodalOptions opts,
        bool rehomed = false)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            // Content is a string — convert to array
            var str = content.GetString();
            var hasObservation = observationsByMessageIndex.TryGetValue(messageIndex, out var obs);
            var hasMedia = stripSet.Any(k => k.msgIdx == messageIndex);

            if (hasMedia)
            {
                // String content shouldn't have media parts (scanner only finds array parts),
                // but be defensive: if there are media entries for this message, treat as empty
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", "[media removed by proxy]");
                writer.WriteEndObject();

                if (hasObservation)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", opts.ObservationMarker + obs);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            else
            {
                // No media, no stripping needed — but may have observation
                if (hasObservation)
                {
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", str);
                    writer.WriteEndObject();

                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", opts.ObservationMarker + obs);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteStringValue(str);
                }
            }
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            // Content is an array — strip media parts, add placeholder if empty, add observation
            var strippedParts = new List<(string type, string text)>();

            for (var pi = 0; pi < content.GetArrayLength(); pi++)
            {
                var part = content[pi];
                if (part.ValueKind != JsonValueKind.Object)
                    continue;

                if (!part.TryGetProperty("type", out var typeProp) ||
                    typeProp.ValueKind != JsonValueKind.String)
                    continue;

                var partType = typeProp.GetString();
                var key = (messageIndex, pi);

                if (stripSet.Contains(key))
                {
                    // This is a media part — skip it (strip it)
                    continue;
                }

                // Keep non-media parts
                if (partType == "text" && part.TryGetProperty("text", out var textProp) &&
                    textProp.ValueKind == JsonValueKind.String)
                {
                    strippedParts.Add((partType, textProp.GetString()!));
                }
                else
                {
                    // Non-text, non-media part — write it as-is
                    writer.WriteStartArray();
                    // Write stripped parts first
                    foreach (var sp in strippedParts)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", sp.type);
                        writer.WriteString("text", sp.text);
                        writer.WriteEndObject();
                    }
                    strippedParts.Clear();

                    // Write the raw part
                    part.WriteTo(writer);

                    // Check for observation
                    if (observationsByMessageIndex.TryGetValue(messageIndex, out var obs))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", "text");
                        writer.WriteString("text", opts.ObservationMarker + obs);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    return;
                }
            }

            // All parts processed — write the result array
            writer.WriteStartArray();

            // If no parts remain after stripping, add placeholder (rehomed messages get a
            // placeholder pointing at the follow-up user turn instead of the generic removal one)
            if (strippedParts.Count == 0)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", rehomed
                    ? "[media rehomed to user message — see the user turn immediately after this tool result]"
                    : "[media removed by proxy]");
                writer.WriteEndObject();
            }
            else
            {
                foreach (var sp in strippedParts)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", sp.type);
                    writer.WriteString("text", sp.text);
                    writer.WriteEndObject();
                }
            }

            // Append observation if present
            if (observationsByMessageIndex.TryGetValue(messageIndex, out var obs2))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", opts.ObservationMarker + obs2);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        else
        {
            // Fallback: write as-is
            content.WriteTo(writer);
        }
    }
}
