using Xunit;
using OAIPreRouter.Cli.Services;

namespace OAIPreRouter.Cli.Tests;

public class UserTextExtractorTests
{
    [Fact]
    public void Extract_MediaInMessage1_TextPartExtracted()
    {
        // Arrange: media in message index 1 (0-based), with text content
        var body = @"{
            ""messages"": [
                {""role"": ""system"", ""content"": ""You are helpful.""},
                {""role"": ""user"", ""content"": ""What is in this image?""}
            ]
        }";
        var media = new List<MediaContentScanner.MediaPart>
        {
            new(MediaContentScanner.MediaKind.Image, 1, 1, "data:image/png;base64,abc")
        };

        // Act
        var result = UserTextExtractor.Extract(body, media);

        // Assert
        Assert.Equal("What is in this image?", result);
    }

    [Fact]
    public void Extract_MediaBearingMessageHasTextAndImage_TextExtracted()
    {
        // Arrange: media-bearing message has both text and image parts
        var body = @"{
            ""messages"": [
                {""role"": ""user"", ""content"": [
                    {""type"": ""text"", ""text"": ""Describe this picture.""},
                    {""type"": ""image_url"", ""image_url"": {""url"": ""data:image/png;base64,xyz""}}
                ]}
            ]
        }";
        var media = new List<MediaContentScanner.MediaPart>
        {
            new(MediaContentScanner.MediaKind.Image, 0, 1, "data:image/png;base64,xyz")
        };

        // Act
        var result = UserTextExtractor.Extract(body, media);

        // Assert
        Assert.Equal("Describe this picture.", result);
    }

    [Fact]
    public void Extract_MediaBearingMessageImageOnly_FallsBack()
    {
        // Arrange: media-bearing message is image-only (no text part)
        var body = @"{
            ""messages"": [
                {""role"": ""user"", ""content"": [
                    {""type"": ""image_url"", ""image_url"": {""url"": ""data:image/png;base64,abc""}}
                ]}
            ]
        }";
        var media = new List<MediaContentScanner.MediaPart>
        {
            new(MediaContentScanner.MediaKind.Image, 0, 0, "data:image/png;base64,abc")
        };

        // Act
        var result = UserTextExtractor.Extract(body, media);

        // Assert
        Assert.Equal("Analyze the attached media.", result);
    }

    [Fact]
    public void Extract_NoMedia_LastMessageTextExtracted()
    {
        // Arrange: no media provided, should fall back to last user message
        var body = @"{
            ""messages"": [
                {""role"": ""system"", ""content"": ""You are helpful.""},
                {""role"": ""user"", ""content"": ""Hello, how are you?""},
                {""role"": ""assistant"", ""content"": ""I am fine.""},
                {""role"": ""user"", ""content"": ""Tell me a joke.""}
            ]
        }";
        var media = new List<MediaContentScanner.MediaPart>();

        // Act
        var result = UserTextExtractor.Extract(body, media);

        // Assert
        Assert.Equal("Tell me a joke.", result);
    }

    [Fact]
    public void Extract_ContentAsString_ReturnedAsIs()
    {
        // Arrange: content is a plain string, not an array
        var body = @"{
            ""messages"": [
                {""role"": ""user"", ""content"": ""Just a string content""}
            ]
        }";
        var media = new List<MediaContentScanner.MediaPart>();

        // Act
        var result = UserTextExtractor.Extract(body, media);

        // Assert
        Assert.Equal("Just a string content", result);
    }

    [Fact]
    public void Extract_MalformedJson_ReturnsFallback()
    {
        // Arrange: invalid JSON
        var body = "{ this is not valid json {{{";
        var media = new List<MediaContentScanner.MediaPart>();

        // Act
        var result = UserTextExtractor.Extract(body, media);

        // Assert
        Assert.Equal("Analyze the attached media.", result);
    }

    [Fact]
    public void Extract_MessagesMissing_ReturnsFallback()
    {
        // Arrange: no messages key in JSON
        var body = @"{""other"": ""data""}";
        var media = new List<MediaContentScanner.MediaPart>();

        // Act
        var result = UserTextExtractor.Extract(body, media);

        // Assert
        Assert.Equal("Analyze the attached media.", result);
    }
}
