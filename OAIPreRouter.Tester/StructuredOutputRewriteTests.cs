using System.Text.Json;
using OAIPreRouter.Cli.Services;
using Xunit;

namespace OAIPreRouter.Cli.Tests;

public class StructuredOutputRewriteTests
{
    [Fact]
    public void NoResponseFormat_IsByteForByteUnchanged()
    {
        const string input = "{\"model\":\"mimo-2.6-flash\",\"chat_template_kwargs\":{\"enable_thinking\":true,\"foo\":\"bar\"}}";
        Assert.Equal(input, JsonBodyRewriter.TryDisableThinkingForResponseFormat(input));
    }

    [Fact]
    public void ResponseFormat_AddsEnableThinkingFalse_WhenKwargsAreAbsent()
    {
        const string input = "{\"model\":\"mimo-2.6-flash\",\"response_format\":{\"type\":\"json_object\"}}";
        var result = JsonBodyRewriter.TryDisableThinkingForResponseFormat(input);
        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        Assert.False(doc.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("json_object", doc.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public void ResponseFormat_ReplacesOnlyEnableThinking_AndPreservesOtherKwargs()
    {
        const string input = "{\"response_format\":{\"type\":\"json_schema\"},\"chat_template_kwargs\":{\"enable_thinking\":true,\"reasoning_effort\":\"high\",\"custom\":7}}";
        var result = JsonBodyRewriter.TryDisableThinkingForResponseFormat(input);
        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        var kwargs = doc.RootElement.GetProperty("chat_template_kwargs");
        Assert.False(kwargs.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("high", kwargs.GetProperty("reasoning_effort").GetString());
        Assert.Equal(7, kwargs.GetProperty("custom").GetInt32());
    }

    [Fact]
    public void MalformedJson_FailsOpenWithNull()
        => Assert.Null(JsonBodyRewriter.TryDisableThinkingForResponseFormat("{bad"));
}
