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
        StaticNudge = "take a step back",
        InjectionMarker = "[LG]: "
    };

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
        // assistant with tool_calls but no reasoning_content → gate must not fire
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"x\",\"arguments\":\"{}\"}}]}]}";
        Assert.Null(LoopGuardService.ExtractLastReasoning(body));
    }

    [Fact]
    public void Gate_BelowThreshold_DoesNotFire()
    {
        Assert.False(LoopGuardService.GateFires("short thinking", 8192));
    }

    [Fact]
    public void Gate_AboveThreshold_Fires()
    {
        Assert.True(LoopGuardService.GateFires(new string('x', 40000), 8192)); // ~10k tokens
    }

    [Fact]
    public void Gate_ThresholdZero_NeverFires()
    {
        Assert.False(LoopGuardService.GateFires(new string('x', 40000), 0));
    }

    [Fact]
    public void Gate_NullReasoning_NeverFires()
    {
        Assert.False(LoopGuardService.GateFires(null, 8192));
    }

    // ─── Evaluate (static mode) ───────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_Disabled_NoOp()
    {
        var opts = DefaultOpts() with { Enabled = false };
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":\"a\",\"reasoning_content\":\"" + new string('x', 40000) + "\"}]}";
        var (b, v) = LoopGuardService.Evaluate(body, opts);
        Assert.Null(b);
        Assert.Null(v);
    }

    [Fact]
    public void Evaluate_EmptyStaticNudge_NoOp()
    {
        var opts = DefaultOpts() with { StaticNudge = "" };
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":\"a\",\"reasoning_content\":\"" + new string('x', 40000) + "\"}]}";
        var (b, v) = LoopGuardService.Evaluate(body, opts);
        Assert.Null(b);
        Assert.Null(v);
    }

    [Fact]
    public void Evaluate_GateFires_NudgeInjected()
    {
        var opts = DefaultOpts();
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":\"a\",\"reasoning_content\":\"" + new string('x', 40000) + "\"}]}";
        var (b, v) = LoopGuardService.Evaluate(body, opts);
        Assert.NotNull(b);
        Assert.Equal("nudge", v);
        using var doc = JsonDocument.Parse(b!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("[LG]: take a step back", content);
    }

    [Fact]
    public void Evaluate_GateBelowThreshold_NoOp()
    {
        var opts = DefaultOpts();
        var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"q\"},{\"role\":\"assistant\",\"content\":\"a\",\"reasoning_content\":\"short\"}]}";
        var (b, v) = LoopGuardService.Evaluate(body, opts);
        Assert.Null(b);
        Assert.Null(v);
    }

    [Fact]
    public void Evaluate_MalformedJson_NoOp()
    {
        var opts = DefaultOpts();
        var (b, v) = LoopGuardService.Evaluate("{bad", opts);
        Assert.Null(b);
        Assert.Null(v);
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
    public void Inject_MalformedJson_ReturnsNull()
    {
        Assert.Null(JsonBodyRewriter.TryInjectLoopGuardNudge("{bad", "[LG]: ", "stop"));
    }

    [Fact]
    public void Inject_NoMessages_ReturnsNull()
    {
        Assert.Null(JsonBodyRewriter.TryInjectLoopGuardNudge("{\"model\":\"x\"}", "[LG]: ", "stop"));
    }
}
