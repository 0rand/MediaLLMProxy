using Xunit;
using System.Text.Json;
using OAIPreRouter.Cli.Services;
using OAIPreRouter.Cli.Models;

namespace OAIPreRouter.Cli.Tests;

public class LoopGuardTests
{
    private static LoopGuardOptions DefaultOpts() => new()
    {
        Enabled = true,
        ReasoningTokenThreshold = 8192,
        StaticNudge = "static fallback nudge",
        InjectionMarker = "[LG]: ",
        AdvisorBackend = new BackendConfig { BaseUrl = "" }, // advisor off by default in tests
        AdvisorModel = "qwen25-3b"
    };

    private static string BigReasoningBody() =>
        "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":\"a\",\"reasoning_content\":\"" + new string('x', 40000) + "\"}]}";

    private static string SmallReasoningBody() =>
        "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":\"a\",\"reasoning_content\":\"short\"}]}";

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int Calls;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static HttpClient ClientWith(FakeHandler handler) => new(handler);

    // ─── Gate / extraction ────────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_LastAssistantReasoning_Returned()
    {
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":\"a\",\"reasoning_content\":\"long thinking\"}]}";
        Assert.Equal("long thinking", LoopGuardService.ExtractLastReasoning(body));
    }

    [Fact]
    public void Extract_NoReasoning_ReturnsNull()
    {
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":\"a\"}]}";
        Assert.Null(LoopGuardService.ExtractLastReasoning(body));
    }

    [Fact]
    public void Extract_LastAssistantIsToolCall_NoReasoning_ReturnsNull()
    {
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"x\",\"arguments\":\"{}\"}}]}]}";
        Assert.Null(LoopGuardService.ExtractLastReasoning(body));
    }

    [Fact]
    public void Gate_BelowThreshold_DoesNotFire() => Assert.False(LoopGuardService.GateFires("short thinking", 8192));

    [Fact]
    public void Gate_AboveThreshold_Fires() => Assert.True(LoopGuardService.GateFires(new string('x', 40000), 8192));

    [Fact]
    public void Gate_ThresholdZero_NeverFires() => Assert.False(LoopGuardService.GateFires(new string('x', 40000), 0));

    [Fact]
    public void Gate_NullReasoning_NeverFires() => Assert.False(LoopGuardService.GateFires(null, 8192));

    // ─── Verdict parsing ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("NUDGE: stop looping", "stop looping")]
    [InlineData("nudge: take a step back", "take a step back")]
    [InlineData("NUDGE:", null)]
    [InlineData("NO_LOOP", null)]
    [InlineData("", null)]
    [InlineData("some random text", null)]
    [InlineData(null, null)]
    public void ParseVerdict(string? raw, string? expected)
    {
        Assert.Equal(expected, LoopGuardService.ParseVerdict(raw));
    }

    // ─── Advisor call ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CallAdvisor_ReturnsContent()
    {
        var handler = new FakeHandler(_ => JsonResponse("{\"choices\":[{\"message\":{\"content\":\"NUDGE: stop\"}}]}"));
        var text = await LoopGuardService.CallAdvisorAsync(ClientWith(handler), "http://x", "m", "prompt", 200, CancellationToken.None);
        Assert.Equal("NUDGE: stop", text);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallAdvisor_HttpError_ReturnsNull()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway));
        var text = await LoopGuardService.CallAdvisorAsync(ClientWith(handler), "http://x", "m", "prompt", 200, CancellationToken.None);
        Assert.Null(text);
    }

    // ─── EvaluateAsync: static-only mode ──────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_Disabled_NoOp()
    {
        var opts = DefaultOpts() with { Enabled = false };
        var (b, v, ms) = await LoopGuardService.EvaluateAsync(BigReasoningBody(), opts, null, null, CancellationToken.None);
        Assert.Null(b); Assert.Null(v); Assert.Equal(0, ms);
    }

    [Fact]
    public async Task Evaluate_StaticOnly_GateFires_InjectsStatic()
    {
        var opts = DefaultOpts(); // advisor off (BaseUrl "")
        var (b, v, ms) = await LoopGuardService.EvaluateAsync(BigReasoningBody(), opts, null, null, CancellationToken.None);
        Assert.NotNull(b); Assert.Equal("nudge", v); Assert.Equal(0, ms);
        using var doc = JsonDocument.Parse(b!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("[LG]: static fallback nudge", content);
    }

    [Fact]
    public async Task Evaluate_StaticOnly_GateBelow_NoOp()
    {
        var opts = DefaultOpts();
        var (b, v, _) = await LoopGuardService.EvaluateAsync(SmallReasoningBody(), opts, null, null, CancellationToken.None);
        Assert.Null(b); Assert.Null(v);
    }

    [Fact]
    public async Task Evaluate_MalformedJson_NoOp()
    {
        var opts = DefaultOpts();
        var (b, v, _) = await LoopGuardService.EvaluateAsync("{bad", opts, null, null, CancellationToken.None);
        Assert.Null(b); Assert.Null(v);
    }

    // ─── EvaluateAsync: advisor mode ──────────────────────────────────────────────────────

    [Fact]
    public async Task Evaluate_AdvisorNudge_Injected()
    {
        var handler = new FakeHandler(_ => JsonResponse("{\"choices\":[{\"message\":{\"content\":\"NUDGE: focus on the entrypoint\"}}]}"));
        var opts = DefaultOpts() with { AdvisorBackend = new BackendConfig { BaseUrl = "http://judge:8008" } };
        var (b, v, ms) = await LoopGuardService.EvaluateAsync(BigReasoningBody(), opts, null, ClientWith(handler), CancellationToken.None);
        Assert.NotNull(b); Assert.Equal("nudge", v); Assert.True(ms >= 0);
        using var doc = JsonDocument.Parse(b!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("focus on the entrypoint", content);
        Assert.DoesNotContain("static fallback nudge", content);
    }

    [Fact]
    public async Task Evaluate_AdvisorNoLoop_NoInjection()
    {
        var handler = new FakeHandler(_ => JsonResponse("{\"choices\":[{\"message\":{\"content\":\"NO_LOOP\"}}]}"));
        var opts = DefaultOpts() with { AdvisorBackend = new BackendConfig { BaseUrl = "http://judge:8008" } };
        var (b, v, _) = await LoopGuardService.EvaluateAsync(BigReasoningBody(), opts, null, ClientWith(handler), CancellationToken.None);
        Assert.Null(b); Assert.Equal("no_loop", v);
    }

    [Fact]
    public async Task Evaluate_AdvisorDead_StaticFallback()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway));
        var opts = DefaultOpts() with { AdvisorBackend = new BackendConfig { BaseUrl = "http://judge:8008" }, AdvisorFallbackToStatic = true };
        var (b, v, _) = await LoopGuardService.EvaluateAsync(BigReasoningBody(), opts, null, ClientWith(handler), CancellationToken.None);
        Assert.NotNull(b); Assert.Equal("nudge", v);
        using var doc = JsonDocument.Parse(b!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("static fallback nudge", content);
    }

    [Fact]
    public async Task Evaluate_AdvisorDead_NoStatic_FailOpen()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway));
        var opts = DefaultOpts() with
        {
            AdvisorBackend = new BackendConfig { BaseUrl = "http://judge:8008" },
            StaticNudge = "",
            AdvisorFallbackToStatic = true
        };
        var (b, v, _) = await LoopGuardService.EvaluateAsync(BigReasoningBody(), opts, null, ClientWith(handler), CancellationToken.None);
        Assert.Null(b); Assert.Null(v);
    }

    [Fact]
    public async Task Evaluate_AdvisorVerdictCached_SecondCallNoHttp()
    {
        var handler = new FakeHandler(_ => JsonResponse("{\"choices\":[{\"message\":{\"content\":\"NUDGE: cached nudge\"}}]}"));
        var opts = DefaultOpts() with { AdvisorBackend = new BackendConfig { BaseUrl = "http://judge:8008" } };
        var cache = new ObservationCache(Microsoft.Extensions.Options.Options.Create(new MultimodalOptions()));
        var client = ClientWith(handler);

        var (b1, v1, _) = await LoopGuardService.EvaluateAsync(BigReasoningBody(), opts, cache, client, CancellationToken.None);
        var (b2, v2, _) = await LoopGuardService.EvaluateAsync(BigReasoningBody(), opts, cache, client, CancellationToken.None);

        Assert.Equal("nudge", v1);
        Assert.Equal("nudge", v2);
        Assert.Equal(1, handler.Calls); // second call served from cache
    }

    // ─── Injection shapes ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Inject_StringContent_Appended()
    {
        var input = "{\"messages\":[{\"role\":\"user\",\"content\":\"describe this\"},{\"role\":\"assistant\",\"content\":\"ok\"}]}";
        var result = JsonBodyRewriter.TryInjectLoopGuardNudge(input, "[LG]: ", "stop");
        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        var msgs = doc.RootElement.GetProperty("messages");
        Assert.Equal("describe this\n\n[LG]: stop", msgs[0].GetProperty("content").GetString());
        Assert.Equal("ok", msgs[1].GetProperty("content").GetString());
    }

    [Fact]
    public void Inject_ArrayContent_AppendsTextPart()
    {
        var input = "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}]}";
        var result = JsonBodyRewriter.TryInjectLoopGuardNudge(input, "[LG]: ", "stop");
        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("[LG]: stop", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public void Inject_NoUserMessage_InsertsUserAfterLast()
    {
        var input = "{\"messages\":[{\"role\":\"system\",\"content\":\"sys\"},{\"role\":\"tool\",\"tool_call_id\":\"c1\",\"content\":\"out\"}]}";
        var result = JsonBodyRewriter.TryInjectLoopGuardNudge(input, "[LG]: ", "stop");
        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result!);
        var msgs = doc.RootElement.GetProperty("messages");
        Assert.Equal(3, msgs.GetArrayLength());
        Assert.Equal("user", msgs[2].GetProperty("role").GetString());
        Assert.Equal("[LG]: stop", msgs[2].GetProperty("content").GetString());
    }

    [Fact]
    public void Inject_MalformedJson_ReturnsNull() => Assert.Null(JsonBodyRewriter.TryInjectLoopGuardNudge("{bad", "[LG]: ", "stop"));

    [Fact]
    public void Inject_NoMessages_ReturnsNull() => Assert.Null(JsonBodyRewriter.TryInjectLoopGuardNudge("{\"model\":\"x\"}", "[LG]: ", "stop"));
}
