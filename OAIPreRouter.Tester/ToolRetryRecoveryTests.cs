using System.Net;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using OAIPreRouter.Cli.Models;
using OAIPreRouter.Cli.Services;
using Xunit;

namespace OAIPreRouter.Cli.Tests;

public class ToolRetryRecoveryTests
{
    private const string Request = """{"model":"deepseek","messages":[{"role":"user","content":"do work"}],"tools":[{"type":"function","function":{"name":"task"}}],"temperature":0.4,"chat_template_kwargs":{"enable_thinking":true}}""";
    private const string Error = """{"error":{"type":"model_output_error","code":"unavailable_tool_call","message":"The model attempted to call a tool not offered by this request."}}""";
    private const string InvalidRaw = "<|DSML|tool_calls><|DSML|invoke name=\"task\"><|DSML|parameter name=\"x\">unfinished";
    private static readonly UnavailableToolRetryOptions Options = new() { Enabled = true };

    [Fact]
    public async Task RawToolAbortRetriesWithCorrectiveUserBanner()
    {
        var sent = new List<string>();
        var reasons = new List<string>();
        using var result = await Run((body, _) =>
        {
            sent.Add(body);
            return Reply(sent.Count == 1 ? Raw(false) : Success(false), false);
        }, false, onRetry: reasons.Add);
        Assert.Equal(2, sent.Count);
        Assert.Equal([UnavailableToolRetryService.RawToolErrorCode], reasons);
        var retry = JsonNode.Parse(sent[1])!;
        Assert.Equal(2, retry["messages"]!.AsArray().Count);
        Assert.Equal("user", retry["messages"]![1]!["role"]!.GetValue<string>());
        Assert.Contains(Options.RawToolCallBanner, retry["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal("do work", retry["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal(0.4, retry["temperature"]!.GetValue<double>());
        Assert.True(retry["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(JsonNode.Parse(Request)!["tools"]!.ToJsonString(), retry["tools"]!.ToJsonString());
        var text = await result.Content.ReadAsStringAsync();
        Assert.Contains("recovered", text);
        Assert.DoesNotContain("failed reasoning", text);
        Assert.DoesNotContain("unfinished", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableToolRetriesAndLeavesThinkingUnchanged(bool streaming)
    {
        var sent = new List<string>();
        using var result = await Run((body, _) =>
        {
            sent.Add(body);
            return Reply(sent.Count == 1 ? streaming ? "data: " + Error + "\n\ndata: [DONE]\n\n" : Error : Success(streaming), streaming,
                sent.Count == 1 && !streaming ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        }, streaming);
        Assert.Equal(2, sent.Count);
        Assert.Contains(Options.Banner, JsonNode.Parse(sent[1])!["messages"]![1]!["content"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(sent[1])!["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.DoesNotContain("unavailable_tool_call", await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StreamingReasoningReturnsBeforeUpstreamCompletesAndIsNotRetried()
    {
        var pipe = new Pipe();
        var sends = 0;
        var pending = UnavailableToolRetryService.SendAsync((_, _) =>
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(pipe.Reader.AsStream()) { Headers = { ContentType = new("text/event-stream") } }
            });
        }, Request, true, true, Options, new BridgeMetrics(), CancellationToken.None);
        await Task.Delay(10);
        Assert.False(pending.IsCompleted);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"reasoning\":\"thinking\"}}]}\n\n"));
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, sends);
        using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Contains("\"reasoning\":\"thinking\"", Encoding.UTF8.GetString(buffer, 0, read));
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task RepeatedRawAbortReturnsStructuredErrorWithoutReset()
    {
        var sends = 0;
        using var result = await Run((_, _) => { sends++; return Reply(Raw(false), false); }, false, Options with { MaxRetries = 99 });
        Assert.Equal(2, sends);
        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
        var text = await result.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"invalid_raw_tool_call\"", text);
        Assert.DoesNotContain("unfinished", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpfrontUnavailableRetryDoesNotReplayVisibleSecondAttempt(bool firstRaw)
    {
        var sends = 0;
        using var result = await Run((_, _) =>
        {
            sends++;
            var raw = sends == 1 ? firstRaw : !firstRaw;
            return Reply(raw ? Raw(true) : "data: " + Error + "\n\ndata: [DONE]\n\n", true);
        }, true);
        Assert.Equal(firstRaw ? 1 : 2, sends);
        var output = await result.Content.ReadAsStringAsync();
        Assert.Contains("failed reasoning", output);
    }

    [Fact]
    public async Task StreamingRawCallIsLeftForTheLiveCureWithoutRetry()
    {
        var sends = 0;
        var raw = "<|DSML|tool_calls><|DSML|invoke name=\"task\"></|DSML|invoke></|DSML|tool_calls>";
        using var result = await Run((_, _) => { sends++; return Reply(Chunk(raw) + Terminal, true); }, true);
        Assert.Equal(1, sends);
        Assert.Contains("\"content\"", await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NativeCallBeforeLaterRejectionIsNotReplayed()
    {
        var sends = 0;
        var mixed = "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"must_not_escape\"}]},\"finish_reason\":null}]}\n\n" +
            "data: " + Error + "\n\ndata: [DONE]\n\n";
        using var result = await Run((_, _) => { sends++; return Reply(sends == 1 ? mixed : Success(true), true); }, true);
        Assert.Equal(1, sends);
        Assert.Contains("must_not_escape", await result.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrToollessRequestIsNotRetried(bool noTools)
    {
        var sends = 0;
        var request = noTools ? "{\"messages\":[]}" : Request;
        using var result = await UnavailableToolRetryService.SendAsync((_, _) =>
        {
            sends++;
            return Task.FromResult(Reply(Error, false, HttpStatusCode.InternalServerError));
        }, request, false, true, Options with { Enabled = noTools }, new BridgeMetrics(), CancellationToken.None);
        Assert.Equal(1, sends);
        Assert.Equal(Error, await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RawAbortGateCanDisableRetryButStillReturnsError()
    {
        var sends = 0;
        using var result = await Run((_, _) => { sends++; return Reply(Raw(false), false); }, false,
            Options with { RetryRawToolCallAborts = false });
        Assert.Equal(1, sends);
        Assert.Contains("invalid_raw_tool_call", await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OtherErrorsAreNotRetriedOrCountedAsSuccess()
    {
        var sends = 0;
        var metrics = new BridgeMetrics();
        using var result = await Run((_, _) =>
        {
            sends++;
            return Reply(sends == 1 ? Error : "{\"error\":{\"code\":\"other\"}}", false, HttpStatusCode.InternalServerError);
        }, false, metrics: metrics);
        var snapshot = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(metrics.Snapshot()))!;
        Assert.Equal(2, sends);
        Assert.Equal(1, snapshot["unavailable_tool_retry_fail"]!.GetValue<int>());
        Assert.Equal(0, snapshot["unavailable_tool_retry_success"]!.GetValue<int>());
    }

    [Fact]
    public async Task TransportFailureIsNotRetried()
    {
        var sends = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => UnavailableToolRetryService.SendAsync((_, _) =>
        {
            sends++;
            throw new HttpRequestException("network down");
        }, Request, true, true, Options, new BridgeMetrics(), CancellationToken.None));
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task BufferLimitDoesNotTriggerModelRetry()
    {
        var sends = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => Run((_, _) =>
        {
            sends++;
            return Reply(new string('x', 1024), false);
        }, false, Options with { MaxResponseBytes = 32 }));
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task ZeroBudgetReturnsRawErrorWithoutReissue()
    {
        var sends = 0;
        using var result = await Run((_, _) => { sends++; return Reply(Raw(false), false); }, false,
            Options with { MaxRetries = 0 });
        Assert.Equal(1, sends);
        Assert.Contains("invalid_raw_tool_call", await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CancellationBeforeSendDoesNotGenerateAnAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sends = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UnavailableToolRetryService.SendAsync((_, _) =>
        {
            sends++;
            return Task.FromResult(Reply(Success(true), true));
        }, Request, true, true, Options, new BridgeMetrics(), cancellation.Token));
        Assert.Equal(0, sends);
    }

    private static Task<HttpResponseMessage> Run(Func<string, CancellationToken, HttpResponseMessage> send, bool streaming,
        UnavailableToolRetryOptions? opts = null, Action<string>? onRetry = null, BridgeMetrics? metrics = null) =>
        UnavailableToolRetryService.SendAsync((body, ct) => Task.FromResult(send(body, ct)), Request, streaming, true,
            opts ?? Options, metrics ?? new BridgeMetrics(), CancellationToken.None, onRetry);

    private static HttpResponseMessage Reply(string text, bool streaming, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(text, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
    };

    private static string Success(bool streaming) => streaming ? Chunk("recovered") + Terminal : Completion("recovered");
    private static string Raw(bool streaming) => streaming ?
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning\":\"failed reasoning\"}}]}\n\n" + Chunk(InvalidRaw) + Terminal : Completion(InvalidRaw);
    private static string Completion(string text) => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = text }, ["finish_reason"] = "stop" })
    }.ToJsonString();
    private static string Chunk(string text) => "data: " + new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["content"] = text }, ["finish_reason"] = null })
    }.ToJsonString() + "\n\n";
    private const string Terminal = "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
}
