using System.Text.Json;
using OAIPreRouter.Cli.Services;
using Xunit;

namespace OAIPreRouter.Cli.Tests;

public class StructuredOutputRewriteTests
{
    [Fact]
    public void NoResponseFormat_IsByteForByteUnchanged()
    {
        const string input = "{\"model\":\"qwen3.8-flash-next\",\"chat_template_kwargs\":{\"enable_thinking\":true,\"foo\":\"bar\"}}";
        Assert.Equal(input, JsonBodyRewriter.TryDisableThinkingForResponseFormat(input));
    }

    [Fact]
    public void ResponseFormat_AddsBothNoThinkKwargs_WhenKwargsAreAbsent()
    {
        const string input = "{\"model\":\"qwen3.8-flash-next\",\"response_format\":{\"type\":\"json_object\"}}";
        var result = JsonBodyRewriter.TryDisableThinkingForResponseFormat(input);
        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        var kwargs = doc.RootElement.GetProperty("chat_template_kwargs");
        Assert.False(kwargs.GetProperty("enable_thinking").GetBoolean());
        Assert.False(kwargs.GetProperty("thinking").GetBoolean());
        Assert.Equal("json_object", doc.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public void ResponseFormat_ReplacesBothNoThinkKwargs_AndPreservesOtherKwargs()
    {
        const string input = "{\"response_format\":{\"type\":\"json_schema\"},\"chat_template_kwargs\":{\"enable_thinking\":true,\"thinking\":true,\"reasoning_effort\":\"high\",\"custom\":7}}";
        var result = JsonBodyRewriter.TryDisableThinkingForResponseFormat(input);
        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        var kwargs = doc.RootElement.GetProperty("chat_template_kwargs");
        Assert.False(kwargs.GetProperty("enable_thinking").GetBoolean());
        Assert.False(kwargs.GetProperty("thinking").GetBoolean());
        Assert.Equal("high", kwargs.GetProperty("reasoning_effort").GetString());
        Assert.Equal(7, kwargs.GetProperty("custom").GetInt32());
    }

    /// <summary>
    /// Real-world control string observed in production traffic: the client
    /// sends the HF-style "thinking" toggle and a reasoning_effort. The rewrite
    /// must neutralise both no-think spellings and keep the effort knob — the
    /// deployed Qwen template gates on enable_thinking, so emitting only the
    /// caller's "thinking" key would be a silent no-op.
    /// </summary>
    [Fact]
    public void ResponseFormat_NeutralisesCallerThinkingTrue_AndKeepsReasoningEffort()
    {
        const string input = "{\"response_format\":{\"type\":\"json_object\"},\"chat_template_kwargs\":{\"thinking\":true,\"reasoning_effort\":\"medium\"}}";
        var result = JsonBodyRewriter.TryDisableThinkingForResponseFormat(input);
        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        var kwargs = doc.RootElement.GetProperty("chat_template_kwargs");
        Assert.False(kwargs.GetProperty("enable_thinking").GetBoolean());
        Assert.False(kwargs.GetProperty("thinking").GetBoolean());
        Assert.Equal("medium", kwargs.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void MalformedJson_FailsOpenWithNull()
        => Assert.Null(JsonBodyRewriter.TryDisableThinkingForResponseFormat("{bad"));
}
