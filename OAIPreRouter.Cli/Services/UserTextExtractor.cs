namespace OAIPreRouter.Cli.Services;

using System.Text;
using System.Text.Json;

public static class UserTextExtractor
{
    /// <summary>Returns text parts of the media-bearing user message; falls back to last user message; never throws.</summary>
    public static string Extract(string body, IReadOnlyList<MediaContentScanner.MediaPart> media)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
                return "Analyze the attached media.";

            var mediaMsgIdx = media.Count > 0 ? media[media.Count - 1].MessageIndex : -1;
            var target = mediaMsgIdx >= 0 ? messages[mediaMsgIdx] : messages[messages.GetArrayLength() - 1];
            if (target.ValueKind != JsonValueKind.Object ||
                !target.TryGetProperty("content", out var content))
                return "Analyze the attached media.";

            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? "Analyze the attached media.";

            if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object &&
                        part.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                        part.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(txt.GetString());
                    }
                }
                if (sb.Length > 0) return sb.ToString();
            }
        }
        catch { }
        return "Analyze the attached media.";
    }
}
