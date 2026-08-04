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
    /// Rewrites media parts out and observations in, in ONE validated pass.
    /// Returns null on parse failure (caller then fails closed with 502).
    /// </summary>
    public static string? TryRewriteMedia(string json,
        IReadOnlyList<MediaContentScanner.MediaPart> parts,
        IReadOnlyDictionary<int, string> observationsByMessageIndex,
        MultimodalOptions opts)
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
                                WriteRewrittenContent(writer, content, i, stripSet, observationsByMessageIndex, opts);
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

    private static void WriteRewrittenContent(Utf8JsonWriter writer, JsonElement content, int messageIndex,
        HashSet<(int msgIdx, int partIdx)> stripSet,
        IReadOnlyDictionary<int, string> observationsByMessageIndex,
        MultimodalOptions opts)
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

            // If no parts remain after stripping, add placeholder
            if (strippedParts.Count == 0)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", "[media removed by proxy]");
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
