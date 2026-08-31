using Xunit;
using OAIPreRouter.Cli.Services;

namespace OAIPreRouter.Cli.Tests;

public class MediaContentScannerTests
{
    [Fact]
    public void Scan_EmptyBody_ReturnsEmpty()
    {
        // Act
        var result = MediaContentScanner.Scan("");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_TextOnlyContent_ReturnsEmpty()
    {
        // Arrange
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"Hello world\"}]}";

        // Act
        var result = MediaContentScanner.Scan(body);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_SingleImage_ReturnsOnePartWithCorrectIndicesAndUrl()
    {
        // Arrange
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"https://example.com/img.png\"}}]}]}";

        // Act
        var result = MediaContentScanner.Scan(body);

        // Assert
        Assert.Single(result);
        var part = result[0];
        Assert.Equal(MediaContentScanner.MediaKind.Image, part.Kind);
        Assert.Equal(0, part.MessageIndex);
        Assert.Equal(0, part.PartIndex);
        Assert.Equal("https://example.com/img.png", part.Url);
    }

    [Fact]
    public void Scan_InputAudioWithDataField_ConstructsDataUrl()
    {
        // OpenAI-standard shape: bytes live in input_audio.data (base64), format in input_audio.format
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"},{\"type\":\"input_audio\",\"input_audio\":{\"data\":\"SGVsbG8=\",\"format\":\"wav\"}}]}]}";

        var result = MediaContentScanner.Scan(body);

        Assert.Single(result);
        var part = result[0];
        Assert.Equal(MediaContentScanner.MediaKind.Audio, part.Kind);
        Assert.Equal(1, part.PartIndex);
        Assert.Equal("data:audio/wav;base64,SGVsbG8=", part.Url);
    }

    [Fact]
    public void Scan_InputAudioMissingFormat_UrlNull()
    {
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_audio\",\"input_audio\":{\"data\":\"SGVsbG8=\"}}]}]}";

        var result = MediaContentScanner.Scan(body);

        Assert.Single(result);
        Assert.Null(result[0].Url);
    }

    [Fact]
    public void Scan_MixedImageThenAudio_ReturnsBothWithUrls()
    {
        // Exact e2e mixed shape: image part BEFORE audio part in same content array
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Describe\"},{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,AAA=\"}},{\"type\":\"input_audio\",\"input_audio\":{\"data\":\"SGVsbG8=\",\"format\":\"wav\"}}]}]}";

        var result = MediaContentScanner.Scan(body);

        Assert.Equal(2, result.Count);
        Assert.Equal(MediaContentScanner.MediaKind.Image, result[0].Kind);
        Assert.Equal("data:image/png;base64,AAA=", result[0].Url);
        Assert.Equal(MediaContentScanner.MediaKind.Audio, result[1].Kind);
        Assert.Equal("data:audio/wav;base64,SGVsbG8=", result[1].Url);
    }

    [Fact]
    public void Scan_ImageAndAudio_ReturnsBothInOrder()
    {
        // Arrange
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"https://example.com/img.png\"}},{\"type\":\"input_audio\",\"url\":\"data:audio/mp3;base64,abc123\"}]}]}";

        // Act
        var result = MediaContentScanner.Scan(body);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal(MediaContentScanner.MediaKind.Image, result[0].Kind);
        Assert.Equal(0, result[0].MessageIndex);
        Assert.Equal(0, result[0].PartIndex);
        Assert.Equal("https://example.com/img.png", result[0].Url);

        Assert.Equal(MediaContentScanner.MediaKind.Audio, result[1].Kind);
        Assert.Equal(0, result[1].MessageIndex);
        Assert.Equal(1, result[1].PartIndex);
        Assert.Equal("data:audio/mp3;base64,abc123", result[1].Url);
    }

    [Fact]
    public void Scan_ContentAsStringNotArray_ReturnsEmpty()
    {
        // Arrange
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"Just a string\"}]}";

        // Act
        var result = MediaContentScanner.Scan(body);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_MalformedJson_ReturnsEmpty()
    {
        // Arrange
        var body = "{invalid json {{{";

        // Act
        var result = MediaContentScanner.Scan(body);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_MessagesNotArray_ReturnsEmpty()
    {
        // Arrange
        var body = "{\"messages\":{\"role\":\"user\",\"content\":\"not an array\"}}";

        // Act
        var result = MediaContentScanner.Scan(body);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_NonStringType_SkippedButValidSubsequentPartFound()
    {
        // Arrange
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":123},{\"type\":\"image_url\",\"image_url\":{\"url\":\"https://example.com/img.png\"}}]}]}";

        // Act
        var result = MediaContentScanner.Scan(body);

        // Assert
        Assert.Single(result);
        Assert.Equal(MediaContentScanner.MediaKind.Image, result[0].Kind);
        Assert.Equal(1, result[0].PartIndex);
    }

    [Fact]
    public void Scan_VideoUrlPart_ReturnsVideoKind()
    {
        // Arrange
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"video_url\",\"url\":\"https://example.com/video.mp4\"}]}]}";

        // Act
        var result = MediaContentScanner.Scan(body);

        // Assert
        Assert.Single(result);
        var part = result[0];
        Assert.Equal(MediaContentScanner.MediaKind.Video, part.Kind);
        Assert.Equal(0, part.MessageIndex);
        Assert.Equal(0, part.PartIndex);
        Assert.Equal("https://example.com/video.mp4", part.Url);
    }

    [Fact]
    public void Scan_ImageUrlWithNonStringUrl_ReturnsPartWithNullUrl()
    {
        // Arrange
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":12345}}]}]}";

        // Act
        var result = MediaContentScanner.Scan(body);

        // Assert
        Assert.Single(result);
        var part = result[0];
        Assert.Equal(MediaContentScanner.MediaKind.Image, part.Kind);
        Assert.Null(part.Url);
    }

    [Fact]
    public void GetMessageCount_NormalBody_ReturnsCount()
    {
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"a\"},{\"role\":\"assistant\",\"content\":\"b\"},{\"role\":\"user\",\"content\":\"c\"}]}";
        Assert.Equal(3, MediaContentScanner.GetMessageCount(body));
    }

    [Fact]
    public void GetMessageCount_NoMessages_ReturnsZero()
    {
        Assert.Equal(0, MediaContentScanner.GetMessageCount("{\"model\":\"x\"}"));
        Assert.Equal(0, MediaContentScanner.GetMessageCount("{invalid"));
    }

    [Fact]
    public void LastTurnMedia_MediaInLastMessage_ReturnedAsCurrent()
    {
        // image in message 1 (the last message) → current turn
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"first\"},{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,AAA=\"}}]}]}";
        var media = MediaContentScanner.Scan(body);

        var current = MediaContentScanner.LastTurnMedia(body, media);

        Assert.Single(current);
        Assert.Equal(1, current[0].MessageIndex);
    }

    [Fact]
    public void LastTurnMedia_MediaOnlyInHistory_ReturnsEmpty()
    {
        // image in message 0, last message is a text-only follow-up → history, NOT current
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,AAA=\"}}]},{\"role\":\"assistant\",\"content\":\"it's a cat\"},{\"role\":\"user\",\"content\":\"and the colors?\"}]}";
        var media = MediaContentScanner.Scan(body);

        var current = MediaContentScanner.LastTurnMedia(body, media);

        Assert.Empty(current);
    }

    [Fact]
    public void LastTurnMedia_MixedHistoryAndCurrent_OnlyLastMessageReturned()
    {
        // image in message 0 (history) AND message 2 (current turn) → only message 2 detoured
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,AAA=\"}}]},{\"role\":\"assistant\",\"content\":\"ok\"},{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,BBB=\"}}]}]}";
        var media = MediaContentScanner.Scan(body);

        var current = MediaContentScanner.LastTurnMedia(body, media);

        Assert.Single(current);
        Assert.Equal(2, current[0].MessageIndex);
        Assert.Contains("BBB", current[0].Url);
    }

    [Fact]
    public void LastTurnMedia_NoMessages_ReturnsEmpty()
    {
        var media = new List<MediaContentScanner.MediaPart>
        {
            new(MediaContentScanner.MediaKind.Image, 0, 0, "data:image/png;base64,AAA=")
        };
        Assert.Empty(MediaContentScanner.LastTurnMedia("{\"model\":\"x\"}", media));
    }

    [Fact]
    public void ToolMedia_OnlyToolRoleMessages_Returned()
    {
        // image in a tool message + image in a user message → only the tool-message part returned
        var body = "{\"messages\":[" +
                   "{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,AAA=\"}}]}," +
                   "{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"vision_analyze\",\"arguments\":\"{}\"}}]}," +
                   "{\"role\":\"tool\",\"tool_call_id\":\"c1\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"},{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,BBB=\"}}]}]}";
        var media = MediaContentScanner.Scan(body);

        var toolMedia = MediaContentScanner.ToolMedia(body, media);

        Assert.Single(toolMedia);
        Assert.Equal(2, toolMedia[0].MessageIndex);
        Assert.Equal(1, toolMedia[0].PartIndex);
        Assert.Contains("BBB", toolMedia[0].Url);
    }

    [Fact]
    public void ToolMedia_NoToolMessages_ReturnsEmpty()
    {
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,AAA=\"}}]}]}";
        var media = MediaContentScanner.Scan(body);

        Assert.Empty(MediaContentScanner.ToolMedia(body, media));
    }
}
