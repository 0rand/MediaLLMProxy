using Xunit;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OAIPreRouter.Cli.Models;
using OAIPreRouter.Cli.Services;

namespace OAIPreRouter.Cli.Tests;

public class VisionDetourClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }

    private VisionDetourClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        var opts = Options.Create(new MultimodalOptions
        {
            VisionBackend = new BackendConfig { BaseUrl = "http://localhost:8000" },
            VisionModel = "Qwen3.6-35B-A3B-MLX-VL-oQ8",
            MaxObservationTokens = 64,
            TimeoutSeconds = 2
        });
        var logger = new TestLogger<VisionDetourClient>();
        return new VisionDetourClient(http, opts, logger);
    }

    [Fact]
    public void BuildPayload_ContainsImageUrlPartWithDataUrl()
    {
        // Arrange
        var opts = new MultimodalOptions();
        var parts = new List<MediaContentScanner.MediaPart>
        {
            new(MediaContentScanner.MediaKind.Image, 0, 0, "data:image/png;base64,abc123")
        };

        // Act
        var payload = VisionDetourClient.BuildPayload(opts, "What is this?", parts);
        var doc = JsonDocument.Parse(payload);

        // Assert
        var userMsg = doc.RootElement.GetProperty("messages")[1];
        var contentArr = userMsg.GetProperty("content");
        var imagePart = contentArr.EnumerateArray().First(p => p.GetProperty("type").GetString() == "image_url");
        Assert.Equal("data:image/png;base64,abc123", imagePart.GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void BuildPayload_IncludesTerseSystemMessage()
    {
        // Arrange
        var opts = new MultimodalOptions();
        var parts = Array.Empty<MediaContentScanner.MediaPart>();

        // Act
        var payload = VisionDetourClient.BuildPayload(opts, "test", parts);
        var doc = JsonDocument.Parse(payload);

        // Assert
        var sysMsg = doc.RootElement.GetProperty("messages")[0];
        Assert.Equal("system", sysMsg.GetProperty("role").GetString());
        Assert.Equal(VisionDetourClient.TerseObserverSystemPrompt, sysMsg.GetProperty("content").GetString());
    }

    [Fact]
    public void BuildPayload_TextOnlyParts_NoImageUrlInPayload()
    {
        // Arrange
        var opts = new MultimodalOptions();
        var parts = new List<MediaContentScanner.MediaPart>
        {
            new(MediaContentScanner.MediaKind.None, 0, 0, null)
        };

        // Act
        var payload = VisionDetourClient.BuildPayload(opts, "Hello", parts);
        var doc = JsonDocument.Parse(payload);

        // Assert
        var userMsg = doc.RootElement.GetProperty("messages")[1];
        var contentArr = userMsg.GetProperty("content");
        var hasImageUrl = contentArr.EnumerateArray().Any(p => p.GetProperty("type").GetString() == "image_url");
        Assert.False(hasImageUrl);
    }

    [Fact]
    public void BuildPayload_EmptyUserText_TextPartOmitted()
    {
        // Arrange
        var opts = new MultimodalOptions();
        var parts = Array.Empty<MediaContentScanner.MediaPart>();

        // Act
        var payload = VisionDetourClient.BuildPayload(opts, "", parts);
        var doc = JsonDocument.Parse(payload);

        // Assert
        var userMsg = doc.RootElement.GetProperty("messages")[1];
        var contentArr = userMsg.GetProperty("content");
        var textParts = contentArr.EnumerateArray().Count(p => p.GetProperty("type").GetString() == "text");
        Assert.Equal(0, textParts);
    }

    [Fact]
    public async Task GetObservation_StripsWhitespace()
    {
        // Arrange
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(@"{""choices"":[{""message"":{""content"":""  red  ""}}]}")
        });
        var parts = Array.Empty<MediaContentScanner.MediaPart>();

        // Act
        var obs = await client.GetObservationAsync("What color?", parts, CancellationToken.None);

        // Assert
        Assert.True(obs.Success);
        Assert.Equal("red", obs.Text);
        Assert.Null(obs.ErrorKind);
    }

    [Fact]
    public async Task GetObservation_Upstream500_ReturnsUpstreamError()
    {
        // Arrange
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("server error")
        });
        var parts = Array.Empty<MediaContentScanner.MediaPart>();

        // Act
        var obs = await client.GetObservationAsync("What color?", parts, CancellationToken.None);

        // Assert
        Assert.False(obs.Success);
        Assert.Equal("upstream", obs.ErrorKind);
        Assert.Equal("", obs.Text);
    }

    [Fact]
    public async Task GetObservation_MalformedResponseJson_ReturnsParseError()
    {
        // Arrange
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ this is not valid json }")
        });
        var parts = Array.Empty<MediaContentScanner.MediaPart>();

        // Act
        var obs = await client.GetObservationAsync("What color?", parts, CancellationToken.None);

        // Assert
        Assert.False(obs.Success);
        Assert.Equal("parse", obs.ErrorKind);
        Assert.Equal("", obs.Text);
    }

    [Fact]
    public async Task GetObservation_Timeout_ReturnsTimeoutError()
    {
        // Arrange
        var client = CreateClient(_ =>
        {
            // Simulate a delay longer than the TimeoutSeconds (2s) by using a delayed response
            // Since FakeHandler is synchronous, we simulate timeout by returning a response
            // that would require waiting. Instead, we test by using a very short timeout.
            // The handler returns immediately, but we need to simulate the timeout behavior.
            // We'll use a handler that delays via Task.Delay
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""choices"":[{""message"":{""content"":""done""}}]}")
            };
        });

        // Override with a client that has a very short timeout to trigger timeout
        var handler = new DelayedHandler(TimeSpan.FromSeconds(10));
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        var opts = Options.Create(new MultimodalOptions
        {
            VisionBackend = new BackendConfig { BaseUrl = "http://localhost:8000" },
            VisionModel = "Qwen3.6-35B-A3B-MLX-VL-oQ8",
            MaxObservationTokens = 64,
            TimeoutSeconds = 1  // 1 second internal timeout
        });
        var logger = new TestLogger<VisionDetourClient>();
        var shortTimeoutClient = new VisionDetourClient(http, opts, logger);

        // Act
        var obs = await shortTimeoutClient.GetObservationAsync("What color?", Array.Empty<MediaContentScanner.MediaPart>(), CancellationToken.None);

        // Assert
        Assert.False(obs.Success);
        Assert.Equal("timeout", obs.ErrorKind);
        Assert.Equal("", obs.Text);
    }

    [Fact]
    public async Task GetObservation_ClientApiKey_SetsAuthorizationHeader()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        var client = CreateClient(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""choices"":[{""message"":{""content"":""red""}}]}")
            };
        });

        // Act
        var obs = await client.GetObservationAsync("What color?", Array.Empty<MediaContentScanner.MediaPart>(), CancellationToken.None, "sk-client");

        // Assert
        Assert.True(obs.Success);
        Assert.NotNull(captured);
        Assert.Equal("Bearer sk-client", captured!.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task GetObservation_ConfigApiKey_FallbackWhenNoClientKey()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""choices"":[{""message"":{""content"":""red""}}]}")
            };
        });
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        var opts = Options.Create(new MultimodalOptions
        {
            VisionBackend = new BackendConfig { BaseUrl = "http://localhost:8000", ApiKey = "cfg-key" },
            VisionModel = "Qwen3.6-35B-A3B-MLX-VL-oQ8",
            MaxObservationTokens = 64,
            TimeoutSeconds = 2
        });
        var logger = new TestLogger<VisionDetourClient>();
        var client = new VisionDetourClient(http, opts, logger);

        // Act (no client key)
        var obs = await client.GetObservationAsync("What color?", Array.Empty<MediaContentScanner.MediaPart>(), CancellationToken.None);

        // Assert
        Assert.True(obs.Success);
        Assert.NotNull(captured);
        Assert.Equal("Bearer cfg-key", captured!.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task GetObservation_NoKeys_NoAuthorizationHeader()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        var client = CreateClient(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""choices"":[{""message"":{""content"":""red""}}]}")
            };
        });

        // Act
        var obs = await client.GetObservationAsync("What color?", Array.Empty<MediaContentScanner.MediaPart>(), CancellationToken.None);

        // Assert
        Assert.True(obs.Success);
        Assert.NotNull(captured);
        Assert.Null(captured!.Headers.Authorization);
    }

    private sealed class DelayedHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken).ContinueWith(
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""choices"":[{""message"":{""content"":""done""}}]}")
                },
                cancellationToken,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }
}

public class TestLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}
