namespace OAIPreRouter.Cli.Services;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OAIPreRouter.Cli.Models;

public class SttDetourClient(HttpClient http, IOptions<MultimodalOptions> opts, ILogger<SttDetourClient>? log)
{
    /// <summary>Result of an STT transcription request. ErrorKind: "timeout" | "upstream" | "parse" | "unsupported_codec" | "non_speech" | "internal" | "policy"</summary>
    public sealed record Transcription(string Text, bool Success, string? ErrorKind);

    private static readonly HashSet<string> AllowedAudioFormats = new(StringComparer.OrdinalIgnoreCase) { "wav", "mp3", "m4a" };

    /// <summary>
    /// Transcribe audio from a data: URL. Only data: URLs are supported in Stage 2.
    /// </summary>
    public async Task<Transcription> TranscribeAsync(string audioDataUrl, CancellationToken ct)
    {
        // URL policy: only data: URLs supported in Stage 2
        if (!audioDataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return new Transcription("", false, "policy");

        // Parse data:audio/<fmt>;base64,<b64>
        var parseResult = ParseAudioDataUrl(audioDataUrl);
        if (parseResult == null)
            return new Transcription("", false, "unsupported_codec");

        var (format, base64Data) = parseResult.Value;
        if (!AllowedAudioFormats.Contains(format))
            return new Transcription("", false, "unsupported_codec");

        var o = opts.Value;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(150));

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                audio_base64 = base64Data,
                format = format
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:8085/transcribe")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (resp.StatusCode == HttpStatusCode.OK)
            {
                string text;
                try
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                    text = doc.RootElement.GetProperty("text").GetString() ?? "";
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    return new Transcription("", false, "parse");
                }

                if (string.IsNullOrWhiteSpace(text))
                    return new Transcription("", false, "non_speech");

                return new Transcription(text.Trim(), true, null);
            }

            // Map specific status codes
            if (resp.StatusCode == HttpStatusCode.UnsupportedMediaType)
                return new Transcription("", false, "unsupported_codec");

            if (resp.StatusCode == HttpStatusCode.RequestEntityTooLarge || resp.StatusCode == HttpStatusCode.UnprocessableEntity)
                return new Transcription("", false, "internal");

            return new Transcription("", false, "upstream");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new Transcription("", false, "client_cancel");
        }
        catch (OperationCanceledException)
        {
            return new Transcription("", false, "timeout");
        }
        catch
        {
            return new Transcription("", false, "upstream");
        }
    }

    private static (string Format, string Base64)? ParseAudioDataUrl(string url)
    {
        // Expected: data:audio/<fmt>;base64,<b64data>
        const string prefix = "data:";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var rest = url[prefix.Length..];

        // Find the comma separating metadata from base64 data
        var commaIdx = rest.LastIndexOf(',');
        if (commaIdx <= 0)
            return null;

        var base64Data = rest[(commaIdx + 1)..];
        var metadata = rest[..commaIdx].Trim();

        // Parse format from metadata like "audio/wav;base64"
        var parts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1)
            return null;

        var mediaType = parts[0].Trim().ToLowerInvariant();
        if (!mediaType.StartsWith("audio/"))
            return null;

        var fmt = mediaType["audio/".Length..];
        if (string.IsNullOrEmpty(fmt))
            return null;

        return (fmt, base64Data);
    }
}
