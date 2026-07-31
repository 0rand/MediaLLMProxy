namespace OAIPreRouter.Cli.Services;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OAIPreRouter.Cli.Models;

public class VisionDetourClient(HttpClient http, IOptions<MultimodalOptions> opts, ILogger<VisionDetourClient>? log)
{
    /// <summary>Result of a vision observation request. ErrorKind: "timeout" | "upstream" | "parse" | "policy"</summary>
    public sealed record Observation(string Text, bool Success, string? ErrorKind);

    /// <summary>
    /// Security boundary: media URLs that may be forwarded to the vision backend.
    /// data: URLs allowed per config; https:// hosts allowed only if host is in UrlPolicy.AllowHttpsHosts.
    /// Everything else fails closed (returns false).
    /// </summary>
    public static bool IsUrlAllowed(string? url, MediaUrlPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return policy.AllowDataUrls;

        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = url["https://".Length..];
            var host = rest.Split('/', '?', '#')[0];
            // strip port
            var colon = host.LastIndexOf(':');
            if (colon > 0 && host[..colon].Contains('.'))
                host = host[..colon];
            host = host.TrimEnd('.');
            return policy.AllowHttpsHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>Terse-observer system prompt — probe-verified (2026-07-31): 35B-VL is verbose by default; this gets the factual observation within the token cap.</summary>
    public const string TerseObserverSystemPrompt =
        "You are a terse vision observer. State only the factual observation, no commentary, no preamble.";

    public static string BuildPayload(MultimodalOptions o, string userText, IReadOnlyList<MediaContentScanner.MediaPart> parts)
    {
        var content = new List<object>();
        if (!string.IsNullOrWhiteSpace(userText))
            content.Add(new { type = "text", text = userText });
        foreach (var p in parts)
        {
            if (p.Kind == MediaContentScanner.MediaKind.Image && p.Url != null)
                content.Add(new { type = "image_url", image_url = new { url = p.Url } });
        }
        return JsonSerializer.Serialize(new
        {
            model = o.VisionModel,
            messages = new object[]
            {
                new { role = "system", content = TerseObserverSystemPrompt },
                new { role = "user", content }
            },
            max_tokens = o.MaxObservationTokens
        });
    }

    public async Task<Observation> GetObservationAsync(string userText, IReadOnlyList<MediaContentScanner.MediaPart> parts, CancellationToken ct, string? clientApiKey = null)
    {
        var o = opts.Value;
        // URL policy enforcement — security boundary
        foreach (var part in parts)
        {
            if (part.Url != null && !IsUrlAllowed(part.Url, o.UrlPolicy))
                return new Observation("", false, "policy");
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
        try
        {
            var payload = BuildPayload(o, userText, parts);
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{o.VisionBackend.BaseUrl.TrimEnd('/')}/v1/chat/completions")
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };

            // Auth: client-supplied key wins; otherwise the configured VisionBackend.ApiKey
            var apiKey = string.IsNullOrWhiteSpace(clientApiKey) ? o.VisionBackend.ApiKey : clientApiKey;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                if (!apiKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    apiKey = $"Bearer {apiKey}";
                req.Headers.TryAddWithoutValidation("Authorization", apiKey);
            }

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
                return new Observation("", false, "upstream");

            var text = "";
            try
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return new Observation("", false, "parse");
            }
            return new Observation(text.Trim(), true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new Observation("", false, "client_cancel");
        }
        catch (OperationCanceledException)
        {
            return new Observation("", false, "timeout");
        }
        catch
        {
            return new Observation("", false, "upstream");
        }
    }
}
