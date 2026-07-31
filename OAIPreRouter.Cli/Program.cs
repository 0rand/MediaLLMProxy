using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OAIPreRouter.Cli.Extensions;
using OAIPreRouter.Cli.Models;
using OAIPreRouter.Cli.Services;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OAIPreRouter.Cli;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            o.SingleLine = true;
        });

        builder.Services.Configure<RoutingOptions>(builder.Configuration.GetSection(RoutingOptions.ConfigSection));
        builder.Services.Configure<MultimodalOptions>(builder.Configuration.GetSection(MultimodalOptions.ConfigSection));
        builder.Services.AddSingleton<ConnectionLimiter>();
        builder.Services.AddSingleton<ObservationCache>();
        builder.Services.AddSingleton<BridgeMetrics>();

        builder.Services.AddHttpClient("proxy")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseProxy = false
            });

        builder.Services.AddHttpClient<VisionDetourClient>(c => c.Timeout = TimeSpan.FromSeconds(90))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });

        builder.Services.AddHttpClient<SttDetourClient>(c => c.Timeout = TimeSpan.FromSeconds(150))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });

        var app = builder.Build();

        var options = app.Services.GetRequiredService<IOptions<RoutingOptions>>().Value;
        var limiter = app.Services.GetRequiredService<ConnectionLimiter>();
        var log = app.Logger;
        var multimodal = app.Services.GetRequiredService<IOptions<MultimodalOptions>>().Value;
        var visionDetour = app.Services.GetRequiredService<VisionDetourClient>();
        var sttDetour = app.Services.GetRequiredService<SttDetourClient>();
        var obsCache = app.Services.GetRequiredService<ObservationCache>();
        var metrics = app.Services.GetRequiredService<BridgeMetrics>();

        log.LogInformation("OAIPreRouter starting — PHASE 1: observation mode (all requests → primary)");
        log.LogInformation("PrimaryBackend: {Url}", options.PrimaryBackend.BaseUrl);
        log.LogInformation("FastBackend: {Url} (disabled in phase 1)", options.FastBackend.BaseUrl);
        log.LogInformation("HeavyBackend: {Url} (disabled in phase 1)", options.HeavyBackend.BaseUrl);
        log.LogInformation("SystemPromptThresholdBytes: {Threshold}", options.SystemPromptThresholdBytes);
        log.LogInformation("FastLaneThresholdBytes: {Threshold}", options.FastLaneThresholdBytes);

        // === Multimodal bridge startup validation ===
        if (multimodal.Enabled)
        {
            var failures = new List<string>();
            if (string.IsNullOrWhiteSpace(multimodal.VisionBackend.BaseUrl))
                failures.Add("VisionBackend.BaseUrl");
            if (string.IsNullOrWhiteSpace(multimodal.AudioBackend.BaseUrl))
                failures.Add("AudioBackend.BaseUrl");
            if (multimodal.TimeoutSeconds <= 0)
                failures.Add("TimeoutSeconds");
            if (multimodal.MaxObservationTokens <= 0)
                failures.Add("MaxObservationTokens");
            if (multimodal.MaxFrames < 1)
                failures.Add("MaxFrames");
            if (multimodal.CacheCapacity <= 0)
                failures.Add("CacheCapacity");
            if (multimodal.CacheTtlHours < 0)
                failures.Add("CacheTtlHours");

            if (failures.Count > 0)
            {
                foreach (var f in failures)
                    log.LogError("Multimodal bridge ENABLED but invalid config: {Failure}", f);
                Environment.Exit(1);
            }
        }

        log.LogInformation("Multimodal bridge: enabled={Enabled}{VisionDetails}", multimodal.Enabled,
            multimodal.Enabled ? $" (vision via {multimodal.VisionBackend.BaseUrl}, model {multimodal.VisionModel}; audio via {multimodal.AudioBackend.BaseUrl})" : "");

        app.MapPost("v1/chat/completions", HandleChatCompletion);
        app.MapPost("chat/completions", HandleChatCompletion);

        app.MapGet("health", () =>
        {
            return Results.Ok(new
            {
                ok = true,
                phase = "observation",
                primary = new { url = options.PrimaryBackend.BaseUrl },
                fast = new { url = options.FastBackend.BaseUrl, enabled = false },
                heavy = new { url = options.HeavyBackend.BaseUrl, enabled = false },
                systemPromptThresholdBytes = options.SystemPromptThresholdBytes,
                fastLaneThresholdBytes = options.FastLaneThresholdBytes,
                bridge = multimodal.Enabled
                    ? new { enabled = true, vision = (string?)multimodal.VisionBackend.BaseUrl, model = (string?)multimodal.VisionModel, audio = (string?)multimodal.AudioBackend.BaseUrl, metrics = (object?)metrics.Snapshot() }
                    : new { enabled = false, vision = (string?)null, model = (string?)null, audio = (string?)null, metrics = (object?)null }
            });
        });

        app.MapGet("v1/models", async (IHttpClientFactory httpClientFactory) =>
        {
            // Single-model surface: when the primary backend enforces a RewriteModel,
            // advertise exactly that model (or its ModelAlias) — the proxy is a
            // one-model multimodal gateway; clients ask for the alias, the proxy
            // executes the configured RewriteModel.
            if (!string.IsNullOrWhiteSpace(options.PrimaryBackend.RewriteModel))
            {
                var advertised = !string.IsNullOrWhiteSpace(options.PrimaryBackend.ModelAlias)
                    ? options.PrimaryBackend.ModelAlias!
                    : options.PrimaryBackend.RewriteModel;
                return Results.Ok(new
                {
                    data = new[]
                    {
                        new
                        {
                            id = advertised,
                            obj = "model",
                            created = 0,
                            owned_by = "media-llm-proxy"
                        }
                    }
                });
            }

            var client = httpClientFactory.CreateClient("proxy");
            try
            {
                var url = JoinUrl(options.PrimaryBackend.BaseUrl, "/v1/models");
                var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                    return Results.Stream(await response.Content.ReadAsStreamAsync(), "application/json");
            }
            catch { }

            return Results.Ok(new { data = Array.Empty<object>() });
        });

        app.MapFallback(async ctx =>
        {
            var requestId = Guid.NewGuid().ToString("N")[..8];

            string bodyPreview = "";
            if (ctx.Request.ContentLength > 0 && ctx.Request.ContentLength < 5000)
            {
                ctx.Request.EnableBuffering();
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
                bodyPreview = await reader.ReadToEndAsync();
                ctx.Request.Body.Position = 0;
                if (bodyPreview.Length > 300)
                    bodyPreview = bodyPreview[..300] + "...<truncated>";
            }

            log.LogError("[FALLBACK-404] requestId={RequestId} method={Method} path={Path} query={QueryString} headers={HeaderCount} body_preview={BodyPreview}",
                requestId, ctx.Request.Method, ctx.Request.Path, ctx.Request.QueryString,
                ctx.Request.Headers.Count, bodyPreview);

            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "Not found",
                request_path = ctx.Request.Path.Value,
                method = ctx.Request.Method,
                request_id = requestId,
                available_endpoints = new[] { "POST /v1/chat/completions", "GET /health", "GET /v1/models" }
            });
        });

        app.Run(options.ListenUrl);

        // === Shared handler for all chat completion endpoints ===
        async Task<IResult> HandleChatCompletion(HttpContext ctx, IHttpClientFactory httpClientFactory)
        {
            var opts = options;
            var requestId = Guid.NewGuid().ToString("N")[..8];
            var sw = Stopwatch.StartNew();

            ctx.Request.EnableBuffering();

            string body;
            using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync();
                ctx.Request.Body.Position = 0;
            }

            // === Detection & Analysis ===
            var analysis = RoutingDecisionService.AnalyzeRequest(body, opts);
            var decision = RoutingDecisionService.DecideRoute(body, opts, limiter);

            // Phase 1: always route to primary
            var backend = opts.PrimaryBackend;

            // === Model enforcement: rewrite the request model to the configured main model ===
            if (!string.IsNullOrWhiteSpace(backend.RewriteModel))
                body = JsonBodyRewriter.TryRewriteModel(body, backend.RewriteModel) ?? body;
            var targetUri = JoinUrl(backend.BaseUrl, "/v1/chat/completions");

            // === Structured Detection Log ===
            var requestedModel = JsonFieldReader.TryReadTopLevelString(body, "model") ?? "<missing>";
            var detectionType = analysis.IsMainAgent ? "MAIN_AGENT" : "SUB_AGENT";
            var intendedLane = analysis.IsMainAgent ? "-" : (body.Length <= opts.FastLaneThresholdBytes ? "fast" : "heavy");

            log.LogInformation(
                "[{RequestId}] DETECT type={DetectionType} role={FirstRole} msgCount={MsgCount} firstMsgBytes={FirstBytes} bodyBytes={BodyBytes} intendedLane={Lane} model={Model} route=primary target={Target}",
                requestId, detectionType, analysis.FirstMessageRole, analysis.MessageCount,
                analysis.FirstMessageBytes, body.Length, intendedLane, requestedModel, targetUri);

            // === Verbose JSON body logging ===
            if (opts.VerboseRequests)
            {
                // Pretty-print the JSON for readability
                string prettyBody;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    prettyBody = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                }
                catch
                {
                    prettyBody = body;
                }

                // Truncate for log safety but keep structure visible
                var preview = prettyBody.Length > 3000 ? prettyBody[..3000] + "\n...<truncated>" : prettyBody;
                log.LogWarning("[{RequestId}] BODY:\n{Body}", requestId, preview);
            }

            // === Multimodal bridge (images + video + audio) ===
            string? forwardBody = null;
            var mediaKinds = new List<string>();
            if (multimodal.Enabled)
            {
                var media = MediaContentScanner.Scan(body);
                if (media.Count > 0)
                {
                    metrics.Scan();

                    // Separate media by kind
                    var imageParts = media.Where(p => p.Kind == MediaContentScanner.MediaKind.Image).ToList();
                    var videoParts = media.Where(p => p.Kind == MediaContentScanner.MediaKind.Video).ToList();
                    var audioParts = media.Where(p => p.Kind == MediaContentScanner.MediaKind.Audio).ToList();

                    // Build observations per message index
                    var observations = new Dictionary<int, string>();

                    // Process images (existing vision detour path)
                    if (imageParts.Count > 0)
                    {
                        var imageDataUrls = imageParts
                            .Where(p => p.Url != null && p.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            .Select(p => p.Url!)
                            .ToList();

                        bool allDataUrls = imageParts.All(p => p.Url != null && p.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase));
                        bool useCache = allDataUrls && imageDataUrls.Count > 0;
                        string? cacheKey = null;
                        if (useCache)
                        {
                            var concatenated = string.Join("|||", imageDataUrls);
                            cacheKey = ObservationCache.BuildKey(concatenated, multimodal.VisionModel);
                        }

                        string? cachedObs = null;
                        if (useCache && cacheKey != null && obsCache.TryGet(cacheKey, out cachedObs))
                        {
                            metrics.CacheHit();
                            log.LogInformation("[{RequestId}] BRIDGE cache=hit media={Count}", requestId, media.Count);
                            foreach (var img in imageParts)
                            {
                                observations[img.MessageIndex] = (observations.ContainsKey(img.MessageIndex) ? observations[img.MessageIndex] + "\n" : "") + $"[Image] {cachedObs}";
                            }
                        }
                        else
                        {
                            if (useCache)
                                log.LogInformation("[{RequestId}] BRIDGE cache=miss media={Count}", requestId, media.Count);
                            metrics.CacheMiss();

                            var swBridge = Stopwatch.StartNew();
                            var userText = UserTextExtractor.Extract(body, media);
                            var clientApiKey = ctx.Request.Headers.Authorization.ToString();
                            if (opts.VerboseRequests)
                                log.LogWarning("[{RequestId}] DETOUR vision payload: images={Count} imageDataUrlChars={Sizes} userTextLen={Len} clientKey={HasKey}",
                                    requestId, imageParts.Count, string.Join(",", imageDataUrls.Select(u => u.Length)), userText.Length, !string.IsNullOrWhiteSpace(clientApiKey));
                            var obs = await visionDetour.GetObservationAsync(userText, imageParts, ctx.RequestAborted, clientApiKey);
                            if (obs.Success)
                            {
                                if (useCache && cacheKey != null)
                                    obsCache.Set(cacheKey, obs.Text);

                                foreach (var img in imageParts)
                                {
                                    observations[img.MessageIndex] = (observations.ContainsKey(img.MessageIndex) ? observations[img.MessageIndex] + "\n" : "") + $"[Image] {obs.Text}";
                                }
                                metrics.DetourOk();
                                metrics.RewriteOk();
                                log.LogInformation("[{RequestId}] BRIDGE media={Count} obsChars={Chars} elapsedMs={ElapsedMs}",
                                    requestId, media.Count, obs.Text.Length, swBridge.ElapsedMilliseconds);
                            }
                            else
                            {
                                log.LogWarning("[{RequestId}] BRIDGE vision {Kind}", requestId, obs.ErrorKind);
                                if (obs.ErrorKind == "timeout")
                                    metrics.DetourTimeout();
                                else
                                    metrics.DetourFail();
                                if (!ctx.Response.HasStarted)
                                {
                                    ctx.Response.StatusCode = obs.ErrorKind == "timeout" ? StatusCodes.Status504GatewayTimeout
                                                                                           : StatusCodes.Status502BadGateway;
                                    var errorMsg = obs.ErrorKind == "policy" ? "Media URL not allowed by policy." : "Vision backend unavailable.";
                                    await ctx.Response.WriteAsJsonAsync(new { error = errorMsg });
                                    return Results.Empty;
                                }
                            }
                        }
                    }

                    // Process audio (new STT path)
                    if (audioParts.Count > 0)
                    {
                        var audioObservations = new List<(int MessageIndex, string Text, bool Success, string? ErrorKind)>();

                        foreach (var audioPart in audioParts)
                        {
                            if (audioPart.Url == null)
                                continue;

                            // Cache key: SHA-256(audio bytes) + model + prompt version
                            string? cacheKey = null;
                            if (audioPart.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            {
                                // Extract base64 data for cache key
                                var commaIdx = audioPart.Url.LastIndexOf(',');
                                if (commaIdx > 0)
                                {
                                    var b64 = audioPart.Url[(commaIdx + 1)..];
                                    cacheKey = ObservationCache.BuildKey(b64, multimodal.SttModel);
                                }
                            }

                            string? cachedTranscript = null;
                            bool useCache = audioPart.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && cacheKey != null;

                            if (useCache && cacheKey != null && obsCache.TryGet(cacheKey, out cachedTranscript))
                            {
                                metrics.CacheHit();
                                audioObservations.Add((audioPart.MessageIndex, cachedTranscript!, true, null));
                            }
                            else
                            {
                                if (useCache)
                                    metrics.CacheMiss();

                                if (opts.VerboseRequests)
                                    log.LogWarning("[{RequestId}] DETOUR audio payload: dataUrlChars={Chars} format={Fmt}",
                                        requestId, audioPart.Url.Length, audioPart.Url.Contains("audio/") ? audioPart.Url.Split(';')[0].Split('/')[^1] : "?");
                                var transcription = await sttDetour.TranscribeAsync(audioPart.Url, ctx.RequestAborted);

                                if (transcription.Success)
                                {
                                    if (useCache && cacheKey != null)
                                        obsCache.Set(cacheKey, transcription.Text);
                                    audioObservations.Add((audioPart.MessageIndex, transcription.Text, true, null));
                                    metrics.SttOk();
                                    metrics.RewriteOk();
                                }
                                else if (transcription.ErrorKind == "non_speech")
                                {
                                    // Non-speech audio → 501, NO forward
                                    log.LogWarning("[{RequestId}] BRIDGE audio non_speech", requestId);
                                    metrics.SttFail();
                                    if (!ctx.Response.HasStarted)
                                    {
                                        ctx.Response.StatusCode = StatusCodes.Status501NotImplemented;
                                        await ctx.Response.WriteAsJsonAsync(new { error = "non_speech_audio_not_supported" });
                                        return Results.Empty;
                                    }
                                }
                                else
                                {
                                    log.LogWarning("[{RequestId}] BRIDGE audio {Kind}", requestId, transcription.ErrorKind);
                                    if (transcription.ErrorKind == "timeout")
                                        metrics.DetourTimeout();
                                    else
                                        metrics.SttFail();
                                    if (!ctx.Response.HasStarted)
                                    {
                                        ctx.Response.StatusCode = transcription.ErrorKind == "timeout" ? StatusCodes.Status504GatewayTimeout
                                                                                                           : StatusCodes.Status502BadGateway;
                                        var errorMsg = transcription.ErrorKind == "policy" ? "Audio URL not allowed by policy." : "STT backend unavailable.";
                                        await ctx.Response.WriteAsJsonAsync(new { error = errorMsg });
                                        return Results.Empty;
                                    }
                                }
                            }
                        }

                        // Merge audio observations into the observations dict
                        foreach (var ao in audioObservations)
                        {
                            if (ao.Success)
                            {
                                observations[ao.MessageIndex] = (observations.ContainsKey(ao.MessageIndex) ? observations[ao.MessageIndex] + "\n" : "") + $"[Audio] {ao.Text}";
                            }
                        }
                    }

                    // Build X-PreRouter-Media header: comma-joined kinds in first-appearance order
                    var seenKinds = new HashSet<string>();
                    foreach (var m in media)
                    {
                        var kind = m.Kind.ToString().ToLowerInvariant();
                        if (seenKinds.Add(kind))
                            mediaKinds.Add(kind);
                    }

                    // If we have observations, rewrite the body
                    if (observations.Count > 0)
                    {
                        log.LogInformation("[{RequestId}] BRIDGE rewrite obsMsgCount={ObsCount} keys={Keys} obsHead={Head} obsTail={Tail}", requestId, observations.Count, string.Join(",", observations.Keys),
                            string.Join(" ||| ", observations.Values.Select(v => v.Length > 120 ? v[..120] : v)),
                            string.Join(" ||| ", observations.Values.Select(v => v.Length > 200 ? v[^200..] : v)));
                        forwardBody = JsonBodyRewriter.TryRewriteMedia(body, media, observations, multimodal)
                                      ?? throw new InvalidOperationException("media rewrite failed");

                        // === Verbose rewritten-body logging: exactly what the text model receives ===
                        if (opts.VerboseRewrites)
                        {
                            string pretty;
                            try
                            {
                                using var doc = JsonDocument.Parse(forwardBody);
                                pretty = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                            }
                            catch
                            {
                                pretty = forwardBody;
                            }

                            var preview = pretty.Length > 6000 ? pretty[..6000] + "\n...<truncated>" : pretty;
                            log.LogWarning("[{RequestId}] FORWARD:\n{Body}", requestId, preview);
                        }

                        // Also include "image" for the header even if we only have images (existing behavior)
                        if (mediaKinds.Count == 0)
                            mediaKinds.Add("image");
                    }
                }
            }

            // === Pass-through: body is forwarded as-is ===
            var forwarded = forwardBody ?? body;
            using var outbound = new HttpRequestMessage(HttpMethod.Post, targetUri);
            outbound.Content = new StringContent(forwarded, Encoding.UTF8, "application/json");

            CopyHeaders(ctx, outbound, backend);

            var client = httpClientFactory.CreateClient("proxy");

            try
            {
                using var upstream = await client.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

                sw.Stop();

                log.LogInformation(
                    "[{RequestId}] UPSTREAM status={StatusCode} elapsedMs={ElapsedMs} target={Target}",
                    requestId, (int)upstream.StatusCode, sw.ElapsedMilliseconds, targetUri);

                ctx.Response.StatusCode = (int)upstream.StatusCode;
                ctx.Response.Headers["X-PreRouter-Detect"] = detectionType;
                ctx.Response.Headers["X-PreRouter-Intended-Lane"] = intendedLane;
                ctx.Response.Headers["X-PreRouter-Body-Bytes"] = body.Length.ToString();
                if (forwardBody != null)
                    ctx.Response.Headers["X-PreRouter-Media"] = string.Join(",", mediaKinds);

                foreach (var h in upstream.Headers)
                    ctx.Response.Headers[h.Key] = h.Value.ToArray();
                foreach (var h in upstream.Content.Headers)
                    if (!string.Equals(h.Key, "content-type", StringComparison.OrdinalIgnoreCase))
                        ctx.Response.Headers[h.Key] = h.Value.ToArray();

                ctx.Response.Headers.Remove("transfer-encoding");
                ctx.Response.Headers.Remove("content-length");

                await using var responseStream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
                if (responseStream == null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Failed to read upstream response stream." });
                    return Results.Empty;
                }

                try
                {
                    await responseStream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                }
                catch (Exception streamEx) when (!streamEx.IsOperationCanceled())
                {
                    log.LogError(streamEx, "[{RequestId}] STREAM_ERROR elapsedMs={ElapsedMs}", requestId, sw.ElapsedMilliseconds);
                }
            }
            catch (OperationCanceledException ex) when (ctx.RequestAborted.IsCancellationRequested)
            {
                sw.Stop();
                log.LogWarning(ex, "[{RequestId}] CLIENT_CANCELLED elapsedMs={ElapsedMs}", requestId, sw.ElapsedMilliseconds);
                if (!ctx.Response.HasStarted)
                    ctx.Response.StatusCode = 499;
            }
            catch (HttpRequestException ex)
            {
                sw.Stop();
                log.LogError(ex, "[{RequestId}] UPSTREAM_HTTP_ERROR elapsedMs={ElapsedMs} target={Target}", requestId, sw.ElapsedMilliseconds, targetUri);
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Upstream HTTP error.", detail = ex.Message });
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                log.LogError(ex, "[{RequestId}] UNHANDLED_ERROR elapsedMs={ElapsedMs}", requestId, sw.ElapsedMilliseconds);
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Internal error.", detail = ex.Message });
                }
            }

            return Results.Empty;
        }
    }

    private static void CopyHeaders(HttpContext ctx, HttpRequestMessage outbound, BackendConfig backend)
    {
        var isLocal = backend.BaseUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                      backend.BaseUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ||
                      backend.BaseUrl.StartsWith("http://192.168.", StringComparison.OrdinalIgnoreCase);

        foreach (var header in ctx.Request.Headers)
        {
            var key = header.Key;

            if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;

            // Strip Authorization for local backends
            if (isLocal && string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(backend.ApiKey) &&
                string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!outbound.Headers.TryAddWithoutValidation(key, header.Value.ToArray()))
            {
                outbound.Content?.Headers.TryAddWithoutValidation(key, header.Value.ToArray());
            }
        }

        if (!string.IsNullOrWhiteSpace(backend.ApiKey))
        {
            var apiKey = backend.ApiKey;
            if (!apiKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                apiKey = $"Bearer {apiKey}";

            outbound.Headers.Remove("Authorization");
            outbound.Headers.TryAddWithoutValidation("Authorization", apiKey);
        }
    }

    private static string JoinUrl(string baseUrl, string relative)
    {
        return $"{baseUrl.TrimEnd('/')}/{relative.TrimStart('/')}";
    }
}
