namespace OAIPreRouter.Cli.Services;

using System.Text.Json;

public static class MediaContentScanner
{
    public enum MediaKind { None, Image, Video, Audio }

    public sealed record MediaPart(MediaKind Kind, int MessageIndex, int PartIndex, string? Url);

    /// <summary>Scans messages[].content[] for media parts. Fail-closed: on any parse anomaly, returns empty (no bridge).</summary>
    public static List<MediaPart> Scan(string body)
    {
        var found = new List<MediaPart>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
                return found;

            for (var mi = 0; mi < messages.GetArrayLength(); mi++)
            {
                var msg = messages[mi];
                if (msg.ValueKind != JsonValueKind.Object ||
                    !msg.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array)
                    continue;

                for (var pi = 0; pi < content.GetArrayLength(); pi++)
                {
                    var part = content[pi];
                    if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type) ||
                        type.ValueKind != JsonValueKind.String)
                        continue;

                    var kind = type.GetString() switch
                    {
                        "image_url" => MediaKind.Image,
                        "video" or "video_url" or "input_video" => MediaKind.Video,
                        "input_audio" or "audio" or "audio_url" => MediaKind.Audio,
                        _ => MediaKind.None
                    };
                    if (kind == MediaKind.None)
                        continue;

                    string? url = null;
                    if (part.TryGetProperty("image_url", out var iu) && iu.ValueKind == JsonValueKind.Object &&
                        iu.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                        url = u.GetString();
                    else if (kind == MediaKind.Video && part.TryGetProperty("video_url", out var vu))
                    {
                        // OpenAI chat shape: {"type":"input_video","video_url":"data:video/mp4;base64,..."}
                        // Anthropic-ish shape: {"type":"video_url","video_url":{"url":"..."}}
                        if (vu.ValueKind == JsonValueKind.String)
                            url = vu.GetString();
                        else if (vu.ValueKind == JsonValueKind.Object &&
                                 vu.TryGetProperty("url", out var vuu) && vuu.ValueKind == JsonValueKind.String)
                            url = vuu.GetString();
                    }
                    else if (kind == MediaKind.Video && part.TryGetProperty("video", out var v) &&
                             v.ValueKind == JsonValueKind.String)
                        url = v.GetString();
                    else if (kind == MediaKind.Audio &&
                             part.TryGetProperty("input_audio", out var ia) && ia.ValueKind == JsonValueKind.Object &&
                             ia.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String &&
                             ia.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String)
                        url = $"data:audio/{fmt.GetString()};base64,{d.GetString()}";
                    else if (part.TryGetProperty("url", out var u2) && u2.ValueKind == JsonValueKind.String)
                        url = u2.GetString();

                    found.Add(new MediaPart(kind, mi, pi, url));
                }
            }
        }
        catch
        {
            return new List<MediaPart>(); // fail-closed
        }
        return found;
    }

    /// <summary>Number of messages in the request body (0 on parse failure / no messages array).</summary>
    public static int GetMessageCount(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array)
                return messages.GetArrayLength();
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// Returns only the media parts belonging to the LAST message — the current turn.
    /// Media in earlier messages is history: its observations are already durable in the
    /// conversation (gated blocks appended to prior responses), so it is stripped but never
    /// detoured. This is what stops the vision model from being reloaded on every turn.
    /// </summary>
    public static List<MediaPart> LastTurnMedia(string body, IReadOnlyList<MediaPart> media)
    {
        var lastMessageIndex = GetMessageCount(body) - 1;
        return media.Where(m => m.MessageIndex == lastMessageIndex).ToList();
    }
}
