using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OAIPreRouter.Cli.Models;
using OAIPreRouter.Cli.Services;
using Xunit;

namespace OAIPreRouter.Cli.Tests;

public class StrictToolRetryTests
{
    private const string Request = """
        {"model":"deepseek","messages":[{"role":"user","content":"do work"}],
         "tools":[{"type":"function","function":{"name":"task"}},
                  {"type":"function","function":{"name":"read_file"}}],
         "tool_choice":"auto","parallel_tool_calls":false,
         "temperature":0.4,"chat_template_kwargs":{"enable_thinking":true}}
        """;

    private const string StrictError = """
        {"error":{"type":"model_output_error","code":"unavailable_tool_call",
        "source":"tensorfold.tool_output_guard","reason":"invalid_executable_tool_output",
        "rejection_id":"abc123","category":"schema_mismatch",
        "message":"The model's executable tool output was rejected."}}
        """;

    private const string NonGuardError = """
        {"error":{"type":"model_output_error","code":"unavailable_tool_call",
        "message":"The model attempted to call a tool not offered by this request."}}
        """;

    private static readonly UnavailableToolRetryOptions Options = new()
    {
        Enabled = true,
        IncludeRequestContext = true
    };

    private static readonly string StrictErrorLine = JsonNode.Parse(StrictError)!.ToJsonString();

    [Fact]
    public async Task StrictGuardRejectionRetriesOnceWithContext()
    {
        var sent = new List<string>();
        var reasons = new List<string>();
        var metrics = new BridgeMetrics();
        using var result = await Send((body, _) =>
        {
            sent.Add(body);
            return sent.Count == 1
                ? Reply(StrictError, false, HttpStatusCode.InternalServerError)
                : Reply(Completion("recovered"), false);
        }, metrics: metrics, onRetry: reasons.Add);

        Assert.Equal(2, sent.Count);
        Assert.Single(reasons);
        Assert.Contains("strict_guard_rejection", reasons[0]);
        Assert.Contains("abc123", reasons[0]);

        var retry = JsonNode.Parse(sent[1])!;
        var messages = retry["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        var banner = messages[1]!["content"]!.GetValue<string>();
        Assert.Contains(Options.StrictGuardBannerIntro, banner);
        Assert.Contains("did not match the offered function schema", banner);
        Assert.Contains("`task`", banner);
        Assert.Contains("`read_file`", banner);
        Assert.Contains("Emit exactly one tool call", banner);
        Assert.Contains(Options.InjectionMarker, banner);
        Assert.DoesNotContain("abc123", banner);
        Assert.Equal(0.4, retry["temperature"]!.GetValue<double>());
        Assert.True(retry["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(JsonNode.Parse(Request)!["tools"]!.ToJsonString(), retry["tools"]!.ToJsonString());
        Assert.Contains("recovered", await result.Content.ReadAsStringAsync());
        Assert.Equal("1", result.Headers.GetValues("X-PreRouter-Tool-Retries").Single());

        var snapshot = Snapshot(metrics);
        Assert.Equal(1, snapshot["unavailable_tool_retries"]!.GetValue<int>());
        Assert.Equal(1, snapshot["unavailable_tool_retry_success"]!.GetValue<int>());
        Assert.Equal(0, snapshot["unavailable_tool_retry_fail"]!.GetValue<int>());
    }

    [Fact]
    public async Task StrictGuardRejectionTwiceReturnsSecondError()
    {
        var sent = 0;
        var metrics = new BridgeMetrics();
        using var result = await Send((_, _) =>
        {
            sent++;
            var error = StrictError.Replace("abc123", sent == 1 ? "first" : "second");
            return Reply(error, false, HttpStatusCode.InternalServerError, shouldRetry: "false");
        }, metrics: metrics);

        Assert.Equal(2, sent);
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        var text = await result.Content.ReadAsStringAsync();
        Assert.Contains("second", text);
        Assert.DoesNotContain("recovered", text);
        Assert.Equal("1", result.Headers.GetValues("X-PreRouter-Tool-Retries").Single());
        Assert.Equal("unavailable_tool_call", result.Headers.GetValues("X-PreRouter-Tool-Rejected").Single());
        Assert.Equal("false", result.Headers.GetValues("x-should-retry").Single());

        var snapshot = Snapshot(metrics);
        Assert.Equal(1, snapshot["unavailable_tool_retry_fail"]!.GetValue<int>());
        Assert.Equal(1, snapshot["tool_retry_fail_by_category"]!["schema_mismatch"]!.GetValue<int>());
    }

    [Fact]
    public async Task NonGuardUnavailableToolErrorIsNotRetried()
    {
        var sent = 0;
        var metrics = new BridgeMetrics();
        using var result = await Send((_, _) =>
        {
            sent++;
            return Reply(NonGuardError, false, HttpStatusCode.InternalServerError);
        }, metrics: metrics);

        Assert.Equal(1, sent);
        Assert.Equal(NonGuardError, await result.Content.ReadAsStringAsync());
        var snapshot = Snapshot(metrics);
        Assert.Equal(1, snapshot["tool_retry_skipped_not_guard"]!.GetValue<int>());
        Assert.Equal(0, snapshot["unavailable_tool_retries"]!.GetValue<int>());
    }

    [Fact]
    public async Task StrictStreamingRejectionBeforeVisibleFrameRetries()
    {
        var first = ": heartbeat\n\ndata: " + StrictErrorLine + "\n\ndata: [DONE]\n\n";
        var sent = 0;
        using var result = await Send((_, _) =>
        {
            sent++;
            return sent == 1
                ? Reply(first, true)
                : Reply(Chunk("recovered") + Terminal, true);
        }, streaming: true);

        Assert.Equal(2, sent);
        var text = await result.Content.ReadAsStringAsync();
        Assert.Contains("recovered", text);
        Assert.DoesNotContain("heartbeat", text);
        Assert.Equal("1", result.Headers.GetValues("X-PreRouter-Tool-Retries").Single());
    }

    [Fact]
    public async Task StrictStreamingReasoningIsForwardedWithoutRetry()
    {
        var first = ChunkReasoning("thinking") + "data: " + StrictErrorLine + "\n\ndata: [DONE]\n\n";
        var sent = 0;
        using var result = await Send((_, _) =>
        {
            sent++;
            return Reply(first, true);
        }, streaming: true);

        Assert.Equal(1, sent);
        var text = await result.Content.ReadAsStringAsync();
        Assert.Contains("thinking", text);
        Assert.Contains("unavailable_tool_call", text);
    }

    [Fact]
    public async Task StrictContractDoesNotCureRawContent()
    {
        const string raw = """<tool_call>{"name":"task","arguments":{}}</tool_call>""";
        var sent = 0;
        using var result = await Send((_, _) =>
        {
            sent++;
            return Reply(Completion(raw), false);
        }, cureRawTools: true);

        Assert.Equal(1, sent);
        var json = JsonNode.Parse(await result.Content.ReadAsStringAsync())!;
        Assert.Equal(raw, json["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Null(json["choices"]![0]!["message"]!["tool_calls"]);
    }

    [Fact]
    public async Task StrictBannerHonorsToolChoiceNone()
    {
        const string request = """
            {"messages":[{"role":"user","content":"hi"}],
             "tools":[{"type":"function","function":{"name":"task"}}],"tool_choice":"none"}
            """;
        var sent = new List<string>();
        using var result = await Send((body, _) =>
        {
            sent.Add(body);
            return sent.Count == 1
                ? Reply(StrictError, false, HttpStatusCode.InternalServerError)
                : Reply(Completion("ok"), false);
        }, request: request);

        Assert.Equal(2, sent.Count);
        var banner = JsonNode.Parse(sent[1])!["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("Do not call any tool", banner);
        Assert.Contains("Do not emit any tool call", banner);
        Assert.DoesNotContain("Emit a complete tool call", banner);
    }

    [Fact]
    public async Task StrictBannerHonorsForcedTool()
    {
        const string request = """
            {"messages":[{"role":"user","content":"hi"}],
             "tools":[{"type":"function","function":{"name":"task"}},
                      {"type":"function","function":{"name":"read_file"}}],
             "tool_choice":{"type":"function","function":{"name":"task"}}}
            """;
        var sent = new List<string>();
        using var result = await Send((body, _) =>
        {
            sent.Add(body);
            return sent.Count == 1
                ? Reply(StrictError, false, HttpStatusCode.InternalServerError)
                : Reply(Completion("ok"), false);
        }, request: request);

        Assert.Equal(2, sent.Count);
        var banner = JsonNode.Parse(sent[1])!["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("Call exactly the function `task`", banner);
        Assert.DoesNotContain("read_file", banner);
    }

    [Fact]
    public async Task StrictStreamingForwardsContentBeforeUpstreamCompletes()
    {
        var pipe = new Pipe();
        var sends = 0;
        var pending = UnavailableToolRetryService.SendAsync((_, _) =>
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(pipe.Reader.AsStream())
                {
                    Headers = { ContentType = new("text/event-stream") }
                }
            });
        }, Request, true, false, Options, new BridgeMetrics(), CancellationToken.None,
            contract: ToolOutputContract.StrictNative);

        await Task.Delay(10);
        Assert.False(pending.IsCompleted);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(Chunk("first")));
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, sends);
        using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[512];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Contains("first", Encoding.UTF8.GetString(buffer, 0, read));
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task StrictZeroBudgetDoesNotReissue()
    {
        var sent = 0;
        using var result = await Send((_, _) =>
        {
            sent++;
            return Reply(StrictError, false, HttpStatusCode.InternalServerError);
        }, opts: Options with { MaxRetries = 0 });

        Assert.Equal(1, sent);
        Assert.Contains("unavailable_tool_call", await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StrictTransportFailureIsNotRetried()
    {
        var sends = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => UnavailableToolRetryService.SendAsync((_, _) =>
        {
            sends++;
            throw new HttpRequestException("network down");
        }, Request, true, false, Options, new BridgeMetrics(), CancellationToken.None,
            contract: ToolOutputContract.StrictNative));
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task StrictCancellationBeforeSendDoesNotGenerateAnAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sends = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UnavailableToolRetryService.SendAsync((_, _) =>
        {
            sends++;
            return Task.FromResult(Reply(Completion("ok"), false));
        }, Request, false, false, Options, new BridgeMetrics(), cancellation.Token,
            contract: ToolOutputContract.StrictNative));
        Assert.Equal(0, sends);
    }

    private static Task<HttpResponseMessage> Send(
        Func<string, CancellationToken, HttpResponseMessage> send,
        bool streaming = false,
        UnavailableToolRetryOptions? opts = null,
        BridgeMetrics? metrics = null,
        Action<string>? onRetry = null,
        string? request = null,
        bool cureRawTools = false) =>
        UnavailableToolRetryService.SendAsync(
            (body, token) => Task.FromResult(send(body, token)),
            request ?? Request,
            streaming,
            cureRawTools,
            opts ?? Options,
            metrics ?? new BridgeMetrics(),
            CancellationToken.None,
            onRetry,
            ToolOutputContract.StrictNative);

    private static HttpResponseMessage Reply(
        string text, bool streaming, HttpStatusCode status = HttpStatusCode.OK, string? shouldRetry = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(text, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
        };
        if (shouldRetry != null)
            response.Headers.TryAddWithoutValidation("x-should-retry", shouldRetry);
        return response;
    }

    private static JsonNode Snapshot(BridgeMetrics metrics) =>
        JsonNode.Parse(JsonSerializer.Serialize(metrics.Snapshot()))!;

    private static string Completion(string text) => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject
        {
            ["message"] = new JsonObject { ["content"] = text },
            ["finish_reason"] = "stop"
        })
    }.ToJsonString();

    private static string Chunk(string text) => "data: " + new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["delta"] = new JsonObject { ["content"] = text },
            ["finish_reason"] = null
        })
    }.ToJsonString() + "\n\n";

    private static string ChunkReasoning(string text) => "data: " + new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["delta"] = new JsonObject { ["reasoning_content"] = text },
            ["finish_reason"] = null
        })
    }.ToJsonString() + "\n\n";

    private const string Terminal =
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
}
