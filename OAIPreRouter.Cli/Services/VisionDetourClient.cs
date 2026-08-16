namespace OAIPreRouter.Cli.Services;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OAIPreRouter.Cli.Models;

public class VisionDetourClient(HttpClient http, IOptions<MultimodalOptions> opts, ILogger<VisionDetourClient>? log)
{
    /// <summary>Result of a vision observation request. ErrorKind: "timeout" | "upstream" | "parse" | "policy".</summary>
    public sealed record Observation(string Text, bool Success, string? ErrorKind, bool UsedFallback = false);

    public static bool IsUrlAllowed(string? url, MediaUrlPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return policy.AllowDataUrls;
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        var host = url["https://".Length..].Split('/', '?', '#')[0];
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host[..colon].Contains('.')) host = host[..colon];
        return policy.AllowHttpsHosts.Contains(host.TrimEnd('.'), StringComparer.OrdinalIgnoreCase);
    }

    public const string TerseObserverSystemPrompt =
        "You are a terse vision observer. State only the factual observation, no commentary, no preamble.";

    public static string BuildPayload(MultimodalOptions o, string userText, IReadOnlyList<MediaContentScanner.MediaPart> parts, string? model = null, BackendConfig? backend = null)
    {
        var content = new List<object>();
        if (!string.IsNullOrWhiteSpace(userText)) content.Add(new { type = "text", text = userText });
        foreach (var p in parts.Where(p => p.Kind == MediaContentScanner.MediaKind.Image && p.Url != null))
            content.Add(new { type = "image_url", image_url = new { url = p.Url } });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model ?? o.VisionModel,
            ["messages"] = new object[] { new { role = "system", content = TerseObserverSystemPrompt }, new { role = "user", content } },
            ["max_tokens"] = o.MaxObservationTokens
        };
        // Sampling overrides: when the backend config pins temperature/top_p,
        // the observation request carries them (deterministic OCR vs creative
        // description is a per-backend choice). Null = backend default.
        if (backend?.Temperature is double temp) payload["temperature"] = temp;
        if (backend?.TopP is double topP) payload["top_p"] = topP;
        return JsonSerializer.Serialize(payload);
    }

    public async Task<Observation> GetObservationAsync(string userText, IReadOnlyList<MediaContentScanner.MediaPart> parts, CancellationToken ct, string? clientApiKey = null)
    {
        var o = opts.Value;
        if (parts.Any(part => part.Url != null && !IsUrlAllowed(part.Url, o.UrlPolicy)))
            return new Observation("", false, "policy");

        var primary = await GetObservationFromBackendAsync(o, o.VisionBackend, o.VisionModel, userText, parts, ct, clientApiKey);
        if (primary.Success || primary.ErrorKind is "policy" or "client_cancel") return primary;

        if (o.VisionFallbackBackend is null || string.IsNullOrWhiteSpace(o.VisionFallbackBackend.BaseUrl) || string.IsNullOrWhiteSpace(o.VisionFallbackModel))
            return primary;

        log?.LogWarning("Primary vision backend failed ({ErrorKind}); attempting configured fallback", primary.ErrorKind);
        var fallback = await GetObservationFromBackendAsync(o, o.VisionFallbackBackend, o.VisionFallbackModel, userText, parts, ct, clientApiKey);
        return fallback with { UsedFallback = true };
    }

    private async Task<Observation> GetObservationFromBackendAsync(MultimodalOptions o, BackendConfig backend, string model, string userText, IReadOnlyList<MediaContentScanner.MediaPart> parts, CancellationToken ct, string? clientApiKey)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{backend.BaseUrl.TrimEnd('/')}/v1/chat/completions")
            { Content = new StringContent(BuildPayload(o, userText, parts, model, backend), Encoding.UTF8, "application/json") };
            // A configured backend token is authoritative. Only use the caller's token when
            // this backend has no configured credential (local/dev compatibility).
            var apiKey = !string.IsNullOrWhiteSpace(backend.ApiKey) ? backend.ApiKey : clientApiKey;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                if (!apiKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) apiKey = $"Bearer {apiKey}";
                req.Headers.TryAddWithoutValidation("Authorization", apiKey);
            }
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return new Observation("", false, "upstream");
            try
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                return new Observation(text.Trim(), true, null);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            { return new Observation("", false, "parse"); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new Observation("", false, "client_cancel"); }
        catch (OperationCanceledException) { return new Observation("", false, "timeout"); }
        catch { return new Observation("", false, "upstream"); }
    }
}
