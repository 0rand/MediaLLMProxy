using Xunit;
using System.Text.Json;
using OAIPreRouter.Cli.Services;

namespace OAIPreRouter.Cli.Tests;

public class JsonBodyRewriterObservationAppendTests
{
    private const string Block = "*** MEDIA OBSERVATION ***\nred circle\n*** END OBSERVATION ***";

    [Fact]
    public void Append_AppendsBlockToMessageContent()
    {
        var json = "{\"id\":\"x\",\"object\":\"chat.completion\",\"created\":123,\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"It's a circle.\"},\"finish_reason\":\"stop\"}],\"usage\":{\"total_tokens\":10}}";

        var result = JsonBodyRewriter.TryAppendObservationToJson(json, Block);

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        Assert.Contains("It's a circle.", content);
        Assert.Contains("*** MEDIA OBSERVATION ***", content);
        Assert.Contains("red circle", content);
        Assert.Contains("*** END OBSERVATION ***", content);
        // Other fields preserved
        Assert.Equal("x", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public void Append_EmptyContent_BlockStillAppended()
    {
        var json = "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":\"length\"}]}";

        var result = JsonBodyRewriter.TryAppendObservationToJson(json, Block);

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        Assert.Contains("*** MEDIA OBSERVATION ***", content);
        Assert.Contains("red circle", content);
    }

    [Fact]
    public void Append_NoChoices_ReturnsNull()
    {
        Assert.Null(JsonBodyRewriter.TryAppendObservationToJson("{\"error\":\"boom\"}", Block));
        Assert.Null(JsonBodyRewriter.TryAppendObservationToJson("{invalid", Block));
    }

    [Fact]
    public void Append_OnlyFirstChoiceModified()
    {
        var json = "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"first\"}},{\"index\":1,\"message\":{\"role\":\"assistant\",\"content\":\"second\"}}]}";

        var result = JsonBodyRewriter.TryAppendObservationToJson(json, Block);

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result);
        var choices = doc.RootElement.GetProperty("choices");
        Assert.Contains("*** MEDIA OBSERVATION ***", choices[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("second", choices[1].GetProperty("message").GetProperty("content").GetString());
    }
}
