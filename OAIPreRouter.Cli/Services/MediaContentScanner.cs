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
                        "video" or "video_url" => MediaKind.Video,
                        "input_audio" or "audio" or "audio_url" => MediaKind.Audio,
                        _ => MediaKind.None
                    };
                    if (kind == MediaKind.None)
                        continue;

                    string? url = null;
                    if (part.TryGetProperty("image_url", out var iu) && iu.ValueKind == JsonValueKind.Object &&
                        iu.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                        url = u.GetString();
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
}
