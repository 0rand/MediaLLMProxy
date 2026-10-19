using System.Text.Json;
using OAIPreRouter.Cli.Models;
using OAIPreRouter.Cli.Services;
using Xunit;

namespace OAIPreRouter.Cli.Tests;

public class UnavailableToolRetryTests
{
    private const string ErrorJson =
        """{"error":{"type":"model_output_error","code":"unavailable_tool_call","message":"The model attempted to call a tool not offered by this request."}}""";

    [Fact]
    public void IsErrorJson_RecognizesStackError()
    {
        Assert.True(UnavailableToolRetryService.IsErrorJson(ErrorJson));
        Assert.False(UnavailableToolRetryService.IsErrorJson("""{"error":{"code":"other"}}"""));
        Assert.False(UnavailableToolRetryService.IsErrorJson("not json"));
    }

    [Fact]
    public void ContainsErrorFrame_RecognizesStreamingError()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"reasoning\":\"thinking\"}}]}\n\n" +
                  "data: " + ErrorJson + "\n\n" +
                  "data: [DONE]\n\n";
        Assert.True(UnavailableToolRetryService.ContainsErrorFrame(sse));
        Assert.False(UnavailableToolRetryService.ContainsErrorFrame(
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n"));
    }

    [Fact]
    public void HasDeclaredTools_ChecksToolsArray()
    {
        Assert.True(UnavailableToolRetryService.HasDeclaredTools(
            """{"tools":[{"type":"function","function":{"name":"task"}}],"messages":[]}"""));
        Assert.False(UnavailableToolRetryService.HasDeclaredTools("""{"messages":[]}"""));
        Assert.False(UnavailableToolRetryService.HasDeclaredTools("""{"tools":[],"messages":[]}"""));
        Assert.False(UnavailableToolRetryService.HasDeclaredTools("not json"));
    }

    [Fact]
    public void BuildRetryBody_AppendsUserBannerAndPreservesConversation()
    {
        const string body =
            """{"model":"deepseek","messages":[{"role":"system","content":"sys"},{"role":"user","content":"do work"}],"tools":[{"type":"function","function":{"name":"task"}}]}""";
        var opts = new UnavailableToolRetryOptions
        {
            Enabled = true,
            Banner = "Use only the offered tools.",
            InjectionMarker = "[RETRY] "
        };

        var retry = UnavailableToolRetryService.BuildRetryBody(body, opts);

        Assert.NotNull(retry);
        using var doc = JsonDocument.Parse(retry!);
        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("do work", messages[1].GetProperty("content").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal("[RETRY] Use only the offered tools.", messages[2].GetProperty("content").GetString());
        Assert.Equal("deepseek", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public void BuildRetryBody_ReturnsNullWithoutMessages()
    {
        Assert.Null(UnavailableToolRetryService.BuildRetryBody("""{"model":"m"}""", new UnavailableToolRetryOptions()));
    }

    [Fact]
    public void TryAppendUserMessage_DoesNotMutateOriginalBody()
    {
        const string body = """{"messages":[{"role":"user","content":"hi"}]}""";
        var appended = JsonBodyRewriter.TryAppendUserMessage(body, "[M] ", "banner");
        Assert.NotNull(appended);
        Assert.Equal(body, """{"messages":[{"role":"user","content":"hi"}]}""");
        using var doc = JsonDocument.Parse(appended!);
        Assert.Equal(2, doc.RootElement.GetProperty("messages").GetArrayLength());
    }
}
