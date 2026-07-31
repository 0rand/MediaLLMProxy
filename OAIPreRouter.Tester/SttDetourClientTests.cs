using Xunit;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OAIPreRouter.Cli.Models;
using OAIPreRouter.Cli.Services;

namespace OAIPreRouter.Cli.Tests;

public class SttDetourClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }

    private sealed class DelayedHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken).ContinueWith(
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""text"":""transcript""}")
                },
                cancellationToken,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private SttDetourClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        var opts = Options.Create(new MultimodalOptions());
        var logger = new TestLogger<SttDetourClient>();
        return new SttDetourClient(http, opts, logger);
    }

    [Fact]
    public async Task TranscribeAsync_WavDataUrl_ReturnsSuccess()
    {
        // Arrange
        var wavDataUrl = "data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEARKwAAIhYAQACABAAZGF0YQAAAAA=";
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(@"{""text"":""hello world""}")
        });

        // Act
        var result = await client.TranscribeAsync(wavDataUrl, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("hello world", result.Text);
        Assert.Null(result.ErrorKind);
    }

    [Fact]
    public async Task TranscribeAsync_EmptyTranscript_ReturnsNonSpeech()
    {
        // Arrange
        var wavDataUrl = "data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEARKwAAIhYAQACABAAZGF0YQAAAAA=";
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(@"{""text"":""   ""}")
        });

        // Act
        var result = await client.TranscribeAsync(wavDataUrl, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("non_speech", result.ErrorKind);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_EmptyText_ReturnsNonSpeech()
    {
        // Arrange
        var wavDataUrl = "data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEARKwAAIhYAQACABAAZGF0YQAAAAA=";
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(@"{""text"":""""}")
        });

        // Act
        var result = await client.TranscribeAsync(wavDataUrl, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("non_speech", result.ErrorKind);
    }

    [Fact]
    public async Task TranscribeAsync_415_ReturnsUnsupportedCodec()
    {
        // Arrange
        var wavDataUrl = "data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEARKwAAIhYAQACABAAZGF0YQAAAAA=";
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType)
        {
            Content = new StringContent("unsupported format")
        });

        // Act
        var result = await client.TranscribeAsync(wavDataUrl, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("unsupported_codec", result.ErrorKind);
    }

    [Fact]
    public async Task TranscribeAsync_500_ReturnsUpstream()
    {
        // Arrange
        var wavDataUrl = "data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEARKwAAIhYAQACABAAZGF0YQAAAAA=";
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("server error")
        });

        // Act
        var result = await client.TranscribeAsync(wavDataUrl, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("upstream", result.ErrorKind);
    }

    [Fact]
    public async Task TranscribeAsync_MalformedJson_ReturnsParseError()
    {
        // Arrange
        var wavDataUrl = "data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEARKwAAIhYAQACABAAZGF0YQAAAAA=";
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ this is not valid json }")
        });

        // Act
        var result = await client.TranscribeAsync(wavDataUrl, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("parse", result.ErrorKind);
    }

    [Fact]
    public async Task TranscribeAsync_ImageDataUrl_ReturnsUnsupportedCodec()
    {
        // Arrange
        var imageDataUrl = "data:image/png;base64,iVBORw0KGgo=";
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var result = await client.TranscribeAsync(imageDataUrl, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("unsupported_codec", result.ErrorKind);
    }

    [Fact]
    public async Task TranscribeAsync_HttpsUrl_ReturnsPolicy()
    {
        // Arrange
        var httpsUrl = "https://example.com/audio.wav";
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var result = await client.TranscribeAsync(httpsUrl, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("policy", result.ErrorKind);
    }

    [Fact]
    public async Task TranscribeAsync_Timeout_ReturnsTimeout()
    {
        // Arrange
        var wavDataUrl = "data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEARKwAAIhYAQACABAAZGF0YQAAAAA=";
        var handler = new DelayedHandler(TimeSpan.FromSeconds(10));
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        var opts = Options.Create(new MultimodalOptions());
        var logger = new TestLogger<SttDetourClient>();
        var client = new SttDetourClient(http, opts, logger);

        // Act
        var result = await client.TranscribeAsync(wavDataUrl, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("timeout", result.ErrorKind);
    }

    [Fact]
    public async Task TranscribeAsync_Mp3DataUrl_ReturnsSuccess()
    {
        // Arrange
        var mp3DataUrl = "data:audio/mp3;base64,SUQzBAAAAAABAFRYWFhY";
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(@"{""text"":""music notes""}")
        });

        // Act
        var result = await client.TranscribeAsync(mp3DataUrl, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("music notes", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_M4aDataUrl_ReturnsSuccess()
    {
        // Arrange
        var m4aDataUrl = "data:audio/m4a;base64,AAAAIGZ0eXBNNkEg";
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(@"{""text"":""podcast episode""}")
        });

        // Act
        var result = await client.TranscribeAsync(m4aDataUrl, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("podcast episode", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_UnsupportedFormat_ReturnsUnsupportedCodec()
    {
        // Arrange
        var flacDataUrl = "data:audio/flac;base64,fLaC0wAAA";
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK));

        // Act
        var result = await client.TranscribeAsync(flacDataUrl, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("unsupported_codec", result.ErrorKind);
    }
}
