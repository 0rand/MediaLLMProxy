using Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OAIPreRouter.Cli.Models;
using OAIPreRouter.Cli.Services;

namespace OAIPreRouter.Cli.Tests;

public class VisionDetourUrlPolicyTests
{
    [Fact]
    public void IsUrlAllowed_DataUrl_AllowDataUrlsTrue_ReturnsTrue()
    {
        var policy = new MediaUrlPolicy { AllowDataUrls = true, AllowHttpsHosts = new() };
        Assert.True(VisionDetourClient.IsUrlAllowed("data:image/png;base64,abc", policy));
    }

    [Fact]
    public void IsUrlAllowed_DataUrl_AllowDataUrlsFalse_ReturnsFalse()
    {
        var policy = new MediaUrlPolicy { AllowDataUrls = false, AllowHttpsHosts = new() };
        Assert.False(VisionDetourClient.IsUrlAllowed("data:image/png;base64,abc", policy));
    }

    [Fact]
    public void IsUrlAllowed_HttpUrl_ReturnsFalse()
    {
        var policy = new MediaUrlPolicy { AllowDataUrls = true, AllowHttpsHosts = new() };
        Assert.False(VisionDetourClient.IsUrlAllowed("http://192.168.1.8/img.png", policy));
    }

    [Fact]
    public void IsUrlAllowed_HttpsUrl_AllowedHost_ReturnsTrue()
    {
        var policy = new MediaUrlPolicy { AllowDataUrls = true, AllowHttpsHosts = new() { "example.com" } };
        Assert.True(VisionDetourClient.IsUrlAllowed("https://example.com/img.png", policy));
    }

    [Fact]
    public void IsUrlAllowed_HttpsUrl_DisallowedHost_ReturnsFalse()
    {
        var policy = new MediaUrlPolicy { AllowDataUrls = true, AllowHttpsHosts = new() { "example.com" } };
        Assert.False(VisionDetourClient.IsUrlAllowed("https://evil.com/img.png", policy));
    }

    [Fact]
    public void IsUrlAllowed_HttpsUrl_PortStripped_ReturnsTrue()
    {
        var policy = new MediaUrlPolicy { AllowDataUrls = true, AllowHttpsHosts = new() { "example.com" } };
        Assert.True(VisionDetourClient.IsUrlAllowed("https://example.com:8443/img.png", policy));
    }

    [Fact]
    public void IsUrlAllowed_NullUrl_ReturnsFalse()
    {
        var policy = new MediaUrlPolicy { AllowDataUrls = true, AllowHttpsHosts = new() { "example.com" } };
        Assert.False(VisionDetourClient.IsUrlAllowed(null!, policy));
    }

    [Fact]
    public void IsUrlAllowed_EmptyUrl_ReturnsFalse()
    {
        var policy = new MediaUrlPolicy { AllowDataUrls = true, AllowHttpsHosts = new() { "example.com" } };
        Assert.False(VisionDetourClient.IsUrlAllowed("", policy));
    }

    [Fact]
    public void IsUrlAllowed_WhitespaceUrl_ReturnsFalse()
    {
        var policy = new MediaUrlPolicy { AllowDataUrls = true, AllowHttpsHosts = new() { "example.com" } };
        Assert.False(VisionDetourClient.IsUrlAllowed("   ", policy));
    }

    [Fact]
    public void IsUrlAllowed_FtpUrl_ReturnsFalse()
    {
        var policy = new MediaUrlPolicy { AllowDataUrls = true, AllowHttpsHosts = new() { "example.com" } };
        Assert.False(VisionDetourClient.IsUrlAllowed("ftp://example.com/file", policy));
    }

    [Fact]
    public void IsUrlAllowed_CaseInsensitiveHost_ReturnsTrue()
    {
        var policy = new MediaUrlPolicy { AllowDataUrls = true, AllowHttpsHosts = new() { "example.com" } };
        Assert.True(VisionDetourClient.IsUrlAllowed("https://EXAMPLE.COM/img.png", policy));
    }

    [Fact]
    public void IsUrlAllowed_HttpsUrl_TrailingDot_ReturnsTrue()
    {
        var policy = new MediaUrlPolicy { AllowDataUrls = true, AllowHttpsHosts = new() { "example.com" } };
        Assert.True(VisionDetourClient.IsUrlAllowed("https://example.com./img.png", policy));
    }

    [Fact]
    public async Task GetObservationAsync_RejectedHttpsUrl_ReturnsPolicyError()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("Handler should not be called for policy-rejected URLs"));
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        var opts = Options.Create(new MultimodalOptions
        {
            VisionBackend = new BackendConfig { BaseUrl = "http://localhost:8000" },
            VisionModel = "test-model",
            MaxObservationTokens = 64,
            TimeoutSeconds = 2,
            UrlPolicy = new MediaUrlPolicy { AllowDataUrls = true, AllowHttpsHosts = new() { "allowed.com" } }
        });
        var logger = new TestLogger<VisionDetourClient>();
        var client = new VisionDetourClient(http, opts, logger);
        var parts = new List<MediaContentScanner.MediaPart>
        {
            new(MediaContentScanner.MediaKind.Image, 0, 0, "https://evil.com/img.png")
        };

        var obs = await client.GetObservationAsync("What is this?", parts, CancellationToken.None);

        Assert.False(obs.Success);
        Assert.Equal("policy", obs.ErrorKind);
        Assert.Equal("", obs.Text);
    }

    [Fact]
    public async Task GetObservationAsync_AcceptedDataUrl_ProceedsToHandler()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(@"{""choices"":[{""message"":{""content"":""a red ball""}}]}")
        });
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        var opts = Options.Create(new MultimodalOptions
        {
            VisionBackend = new BackendConfig { BaseUrl = "http://localhost:8000" },
            VisionModel = "test-model",
            MaxObservationTokens = 64,
            TimeoutSeconds = 2,
            UrlPolicy = new MediaUrlPolicy { AllowDataUrls = true, AllowHttpsHosts = new() }
        });
        var logger = new TestLogger<VisionDetourClient>();
        var client = new VisionDetourClient(http, opts, logger);
        var parts = new List<MediaContentScanner.MediaPart>
        {
            new(MediaContentScanner.MediaKind.Image, 0, 0, "data:image/png;base64,abc123")
        };

        var obs = await client.GetObservationAsync("What color?", parts, CancellationToken.None);

        Assert.True(obs.Success);
        Assert.Equal("a red ball", obs.Text);
        Assert.Null(obs.ErrorKind);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }
}
