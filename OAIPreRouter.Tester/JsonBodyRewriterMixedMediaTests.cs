using System.Text.Json;
using OAIPreRouter.Cli.Models;
using OAIPreRouter.Cli.Services;
using Xunit;

namespace OAIPreRouter.Tester;

public class JsonBodyRewriterMixedMediaTests
{
    private static MultimodalOptions Options() => new();

    [Fact]
    public void RewriteModel_ReplacesRequestedModel()
    {
        var body = "{\"model\":\"client-chosen-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"temperature\":0.9}";

        var result = JsonBodyRewriter.TryRewriteModel(body, "mlx-community--Laguna-S-2.1-oQ4e-fast");

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        Assert.Equal("mlx-community--Laguna-S-2.1-oQ4e-fast", doc.RootElement.GetProperty("model").GetString());
        // other fields untouched
        Assert.Equal(0.9, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal("hi", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void RewriteModel_MissingModelField_AddsIt()
    {
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

        var result = JsonBodyRewriter.TryRewriteModel(body, "main-model");

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        Assert.Equal("main-model", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void Mixed_ImageAndAudio_StripsBoth_And_InjectsCombinedObservation()
    {
        var body = "{\"model\":\"deepseek-v4-flash\",\"messages\":[{\"role\":\"user\",\"content\":[" +
                   "{\"type\":\"text\",\"text\":\"Describe\"}," +
                   "{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,AAA=\"}}," +
                   "{\"type\":\"input_audio\",\"input_audio\":{\"data\":\"SGVsbG8=\",\"format\":\"wav\"}}" +
                   "]}]}";

        var media = MediaContentScanner.Scan(body);
        Assert.Equal(2, media.Count);

        var observations = new Dictionary<int, string>
        {
            [0] = "[Image] solid red\n[Audio] Hello world."
        };

        var result = JsonBodyRewriter.TryRewriteMedia(body, media, observations, Options());

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        var messages = doc.RootElement.GetProperty("messages");
        // policy system message is inserted at index 0; the user message follows
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        var userMsg = messages.EnumerateArray().First(m => m.GetProperty("role").GetString() == "user");
        var content = userMsg.GetProperty("content");

        // no media parts survive
        foreach (var part in content.EnumerateArray())
        {
            var type = part.GetProperty("type").GetString();
            Assert.NotEqual("image_url", type);
            Assert.NotEqual("input_audio", type);
        }

        var allText = string.Join("\n", content.EnumerateArray()
            .Where(p => p.GetProperty("type").GetString() == "text")
            .Select(p => p.GetProperty("text").GetString()));

        Assert.Contains("[Image] solid red", allText);
        Assert.Contains("[Audio] Hello world.", allText);
    }
}
