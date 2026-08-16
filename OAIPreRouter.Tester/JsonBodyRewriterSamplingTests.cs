using Xunit;
using System.Text.Json;
using OAIPreRouter.Cli.Services;

namespace OAIPreRouter.Cli.Tests;

public class JsonBodyRewriterSamplingTests
{
    [Fact]
    public void TryRewriteSampling_ReplacesExistingValue()
    {
        // Arrange — client sends temperature 0.7
        const string body = """{"model":"deepseek-v4-flash","temperature":0.7,"messages":[{"role":"user","content":"hi"}]}""";

        // Act — proxy enforces 0.2
        var rewritten = JsonBodyRewriter.TryRewriteSampling(body, "temperature", 0.2);

        // Assert
        Assert.NotNull(rewritten);
        using var doc = JsonDocument.Parse(rewritten!);
        Assert.Equal(0.2, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal("deepseek-v4-flash", doc.RootElement.GetProperty("model").GetString());
        // Other fields preserved
        Assert.Equal(1, doc.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public void TryRewriteSampling_AddsMissingField()
    {
        // Arrange — client sent no top_p
        const string body = """{"model":"deepseek-v4-flash","messages":[{"role":"user","content":"hi"}]}""";

        // Act
        var rewritten = JsonBodyRewriter.TryRewriteSampling(body, "top_p", 0.9);

        // Assert
        Assert.NotNull(rewritten);
        using var doc = JsonDocument.Parse(rewritten!);
        Assert.Equal(0.9, doc.RootElement.GetProperty("top_p").GetDouble());
    }

    [Fact]
    public void TryRewriteSampling_TwoFields_Independent()
    {
        // Arrange
        const string body = """{"model":"m","temperature":1.0,"messages":[]}""";

        // Act — apply both, order-independent
        var afterTemp = JsonBodyRewriter.TryRewriteSampling(body, "temperature", 0.4)!;
        var afterBoth = JsonBodyRewriter.TryRewriteSampling(afterTemp, "top_p", 0.95)!;

        // Assert
        using var doc = JsonDocument.Parse(afterBoth);
        Assert.Equal(0.4, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal(0.95, doc.RootElement.GetProperty("top_p").GetDouble());
    }

    [Fact]
    public void TryRewriteSampling_InvalidJson_ReturnsNull()
    {
        // Arrange
        const string body = "{ this is not valid json }";

        // Act
        var rewritten = JsonBodyRewriter.TryRewriteSampling(body, "temperature", 0.2);

        // Assert — caller keeps the original body (fail-open on parse, like the other rewriters)
        Assert.Null(rewritten);
    }
}
