using System.Text;
using System.Text.Json.Nodes;
using OAIPreRouter.Cli.Services;
using Xunit;

namespace OAIPreRouter.Cli.Tests;

public class RawToolCallCureTests
{
    private static readonly HashSet<string> Names = ["write_file", "read_file"];
    private const string Value = "---\n  indented &lt;literal&gt;\n\n";

    private static string Block(string prefix = "\uFF5CDSML\uFF5C ", string tag = "calls") =>
        $"<{prefix}{tag}><{prefix}invoke name=\"write_file\"><{prefix}parameter name=\"content\" string=\"true\">{Value}</{prefix}parameter></{prefix}invoke></{prefix}{tag}>";

    [Theory]
    [InlineData("\uFF5CDSML\uFF5C ", "calls")]
    [InlineData("\uFF5CDSML\uFF5C", "tool_calls")]
    [InlineData("|DSML|", "tool_calls")]
    [InlineData("", "tool_calls")]
    [InlineData("DSML:", "tool_calls")]
    public void ParserPreservesFileContent(string prefix, string tag)
    {
        Assert.True(RawToolCallCure.TryParse(Block(prefix, tag), Names, out var calls, out var content));
        Assert.Equal("", content);
        Assert.Single(calls);
        Assert.Equal("write_file", calls[0]!["function"]!["name"]!.GetValue<string>());
        var args = JsonNode.Parse(calls[0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal(Value, args!["content"]!.GetValue<string>());
    }

    [Fact]
    public void ParserPreservesTypedArguments()
    {
        var block = "<tool_calls><invoke name=\"write_file\"><parameter name=\"count\" string=\"false\">3</parameter><parameter name=\"items\" string=\"false\">[true,null]</parameter></invoke></tool_calls>";
        Assert.True(RawToolCallCure.TryParse(block, Names, out var calls, out _));
        var args = JsonNode.Parse(calls[0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal(3, args!["count"]!.GetValue<int>());
        Assert.True(args["items"]![0]!.GetValue<bool>());
        Assert.Null(args["items"]![1]);
    }

    [Fact]
    public void LiteralDsmlInsideFileContentIsNotNormalized()
    {
        var text = Block().Replace(Value, "literal <|DSML|invoke name=\"example\"> code");
        Assert.True(RawToolCallCure.TryParse(text, Names, out var calls, out _));
        var args = JsonNode.Parse(calls[0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("literal <|DSML|invoke name=\"example\"> code", args!["content"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("```json\n{\"example\":\"<tool_call><name>unknown</name><parameters>{}</parameters></tool_call>\"}\n```")]
    [InlineData("```dsml\n<tool_calls><invoke name=\"write_file\"></invoke></tool_calls>\n```")]
    [InlineData("```dsml\n<tool_calls><invoke name=\"unknown\"></invoke></tool_calls>\n```")]
    [InlineData("Use `<tool_calls><invoke name=\"unknown\"></invoke></tool_calls>` as an example.")]
    [InlineData("\"<tool_calls><invoke name=\"unknown\"></invoke></tool_calls>\"")]
    [InlineData("> <tool_calls><invoke name=\"unknown\"></invoke></tool_calls>")]
    [InlineData(" ```xml\n <tool_calls><invoke name=\"unknown\"></invoke></tool_calls>\n ```")]
    [InlineData("  ```xml\n  <tool_calls><invoke name=\"unknown\"></invoke></tool_calls>\n  ```")]
    [InlineData("   ```xml\n   <tool_calls><invoke name=\"unknown\"></invoke></tool_calls>\n   ```")]
    [InlineData("   > <tool_calls><invoke name=\"unknown\"></invoke></tool_calls>")]
    [InlineData("    <tool_calls><invoke name=\"unknown\"></invoke></tool_calls>")]
    [InlineData("\t> <tool_calls><invoke name=\"unknown\"></invoke></tool_calls>")]
    [InlineData("\t<tool_calls><invoke name=\"unknown\"></invoke></tool_calls>")]
    [InlineData("`` before ``` <tool_calls><invoke name=\"unknown\"></invoke></tool_calls> ``` after ``")]
    public void LiteralExamplesAreNotCalls(string text)
    {
        Assert.False(RawToolCallCure.TryParse(text, Names, out _, out _));
        Assert.Equal(-1, RawToolCallCure.FindStart(text));
    }

    [Fact]
    public void ParsesActualCallAfterLiteralExampleAndPreservesJsonArgumentMarkers()
    {
        const string literal = "```xml\n<tool_calls><invoke name=\"unknown\"></invoke></tool_calls>\n```\n";
        var actual = "<tool_calls><invoke name=\"write_file\"><parameter name=\"content\">{\"example\":\"<tool_call>\"}</parameter></invoke></tool_calls>";
        Assert.True(RawToolCallCure.TryParse(literal + actual, Names, out var calls, out var content));
        Assert.Equal(literal, content);
        Assert.Equal("{\"example\":\"<tool_call>\"}", JsonNode.Parse(calls[0]!["function"]!["arguments"]!.GetValue<string>())!["content"]!.GetValue<string>());
    }

    [Fact]
    public void ParsesActualCallAfterIndentedFenceClose()
    {
        const string literal = " ```xml\n <tool_calls><invoke name=\"unknown\"></invoke></tool_calls>\n   ```\n";
        Assert.True(RawToolCallCure.TryParse(literal + Block(), Names, out var calls, out var content));
        Assert.Equal(literal, content);
        Assert.Single(calls);
    }

    [Fact]
    public void RequestToolsAndToolChoiceRestrictConversion()
    {
        const string request = "{\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"write_file\"}}]}";
        Assert.Contains("write_file", RawToolCallCure.AllowedNames(request));
        Assert.Empty(RawToolCallCure.AllowedNames(request.Replace("{\"tools\"", "{\"tool_choice\":\"none\",\"tools\"")));
        Assert.Empty(RawToolCallCure.AllowedNames(request.Replace("{\"tools\"", "{\"tool_choice\":{\"type\":\"function\",\"function\":{\"name\":\"other\"}},\"tools\"")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"error\":\"bad request\"}")]
    public void NonCompletionJsonIsUntouched(string json) => Assert.Equal(json, RawToolCallCure.RewriteJson(json, Names));

    [Fact]
    public void QwenAndLegacyDsmlAreSupported()
    {
        const string inner = "<name>read_file</name><parameters>{&quot;path&quot;:&quot;a.txt&quot;}</parameters>";
        foreach (var text in new[] { "<tool_call>" + inner + "</tool_call>", "<DSML>tool_calls>" + inner + "</DSML:tool_calls>" })
            Assert.True(RawToolCallCure.TryParse(text, Names, out _, out _));
    }

    [Theory]
    [InlineData("<tool_calls><invoke name=\"unknown\"></invoke></tool_calls>")]
    [InlineData("<tool_calls><invoke name=\"write_file\"><parameter name=\"x\" string=\"false\">bad</parameter></invoke></tool_calls>")]
    [InlineData("<tool_calls><invoke name=\"write_file\"><parameter name=\"x\">unfinished</invoke></tool_calls>")]
    public void InvalidCallsAreNotConverted(string text) => Assert.False(RawToolCallCure.TryParse(text, Names, out _, out _));

    [Fact]
    public void JsonPreservesProseReasoningAndUsage()
    {
        var root = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0, ["finish_reason"] = "stop",
                ["message"] = new JsonObject { ["role"] = "assistant", ["reasoning"] = "thinking", ["content"] = "before " + Block() + " after" }
            }),
            ["usage"] = new JsonObject { ["total_tokens"] = 42 }
        };
        var result = JsonNode.Parse(RawToolCallCure.RewriteJson(root.ToJsonString(), Names))!;
        Assert.Equal("before  after", result["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Equal("thinking", result["choices"]![0]!["message"]!["reasoning"]!.GetValue<string>());
        Assert.Equal("tool_calls", result["choices"]![0]!["finish_reason"]!.GetValue<string>());
        Assert.Equal(42, result["usage"]!["total_tokens"]!.GetValue<int>());
        root["choices"]![0]!["finish_reason"] = "length";
        Assert.Equal(root.ToJsonString(), RawToolCallCure.RewriteJson(root.ToJsonString(), Names));
    }

    [Fact]
    public async Task StreamingEverySplitBoundaryIncludingReasoningAndRole()
    {
        var block = Block();
        for (var split = 1; split < block.Length; split++)
        {
            var stream = Chunk(new JsonObject { ["role"] = "assistant", ["reasoning"] = "thinking", ["content"] = "before " + block[..split] }) +
                Chunk(new JsonObject { ["content"] = block[split..] }) + Chunk(new JsonObject(), "stop") + "data: [DONE]\n\n";
            var output = await Process(stream);
            var chunks = Events(output);
            Assert.Equal("before ", string.Concat(chunks.Select(c => c["choices"]![0]!["delta"]!["content"]?.GetValue<string>())));
            var call = chunks.Select(c => c["choices"]![0]!["delta"]!["tool_calls"]).OfType<JsonArray>().Single()[0]!;
            Assert.Equal(Value, JsonNode.Parse(call["function"]!["arguments"]!.GetValue<string>())!["content"]!.GetValue<string>());
            Assert.Equal(1, output.Split("data: [DONE]").Length - 1);
            Assert.Contains(chunks, c => c["choices"]![0]!["delta"]!["reasoning"]?.GetValue<string>() == "thinking");
        }
    }

    [Fact]
    public async Task MultipleCallsHaveDistinctIndicesAndUsageSurvives()
    {
        var input = Chunk(new JsonObject { ["content"] = Block() + Block() }) + Chunk(new JsonObject(), "stop") +
            "data: {\"choices\":[],\"usage\":{\"total_tokens\":42}}\n\ndata: [DONE]\n\n";
        var output = await Process(input);
        var calls = Events(output).Select(c => c["choices"]!.AsArray().FirstOrDefault()?["delta"]?["tool_calls"]).OfType<JsonArray>().Single();
        Assert.Equal(2, calls.Count);
        Assert.Equal(0, calls[0]!["index"]!.GetValue<int>());
        Assert.Equal(1, calls[1]!["index"]!.GetValue<int>());
        Assert.Contains("\"total_tokens\":42", output);
    }

    [Fact]
    public async Task NativeCallsAndPlainTextArePreserved()
    {
        var delta = new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = "native" }) };
        var output = await Process(Chunk(delta) + Chunk(new JsonObject(), "tool_calls") + "data: [DONE]\n\n");
        Assert.Equal("native", Events(output)[0]["choices"]![0]!["delta"]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        output = await Process(Chunk(new JsonObject { ["content"] = "1 <" }) + Chunk(new JsonObject { ["content"] = " 2" }, "stop") + "data: [DONE]\n\n");
        Assert.Equal("1 < 2", string.Concat(Events(output).Select(c => c["choices"]![0]!["delta"]!["content"]?.GetValue<string>())));
    }

    [Fact]
    public async Task StreamingLiteralExamplesSurviveEverySplitAndDoNotMixWithNativeCalls()
    {
        const string literal = "```json\n{\"example\":\"<tool_call><name>unknown</name><parameters>{}</parameters></tool_call>\"}\n```";
        for (var split = 1; split < literal.Length; split++)
        {
            var output = await Process(Chunk(new JsonObject { ["content"] = literal[..split] }) +
                Chunk(new JsonObject { ["content"] = literal[split..] }, "stop") + "data: [DONE]\n\n");
            Assert.Equal(literal, string.Concat(Events(output).Select(c => c["choices"]![0]!["delta"]!["content"]?.GetValue<string>())));
        }

        var combined = literal + "\n" + Block();
        for (var split = 1; split < combined.Length; split++)
        {
            var output = await Process(Chunk(new JsonObject { ["content"] = combined[..split] }) +
                Chunk(new JsonObject { ["content"] = combined[split..] }, "stop") + "data: [DONE]\n\n");
            var chunks = Events(output);
            Assert.Equal(literal + "\n", string.Concat(chunks.Select(c => c["choices"]![0]!["delta"]!["content"]?.GetValue<string>())));
            Assert.Single(chunks.Select(c => c["choices"]![0]!["delta"]!["tool_calls"]).OfType<JsonArray>());
        }

        var native = new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = "native" }) };
        var mixedOutput = await Process(Chunk(new JsonObject { ["content"] = literal }) + Chunk(native) + Chunk(new JsonObject(), "tool_calls") + "data: [DONE]\n\n");
        Assert.Equal(literal, string.Concat(Events(mixedOutput).Select(c => c["choices"]![0]!["delta"]!["content"]?.GetValue<string>())));
        Assert.Contains("\"id\":\"native\"", mixedOutput);
    }

    [Fact]
    public async Task IncompleteAndLengthLimitedCallsReturnTerminalErrorWithoutToolExecution()
    {
        foreach (var input in new[]
        {
            Chunk(new JsonObject { ["content"] = Block()[..^10] }) + Chunk(new JsonObject(), "stop"),
            Chunk(new JsonObject { ["content"] = Block() }) + Chunk(new JsonObject(), "length"),
            Chunk(new JsonObject { ["content"] = Block() }) + Chunk(new JsonObject(), "content_filter")
        })
        {
            var output = await Process(input);
            Assert.Contains("\"code\":\"invalid_raw_tool_call\"", output);
            Assert.Contains("\"source\":\"mediallmproxy.raw_tool_guard\"", output);
            Assert.DoesNotContain("\"tool_calls\"", output);
            Assert.Equal(1, output.Split("data: [DONE]").Length - 1);
        }
    }

    [Fact]
    public async Task ReasoningAndCommentsAreForwardedBeforeTheTerminalRawCallDecision()
    {
        using var destination = new MemoryStream();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = SseToolCallCure.PipeAsync(async source =>
        {
            await source.WriteAsync(Encoding.UTF8.GetBytes(": heartbeat\n\n" + Chunk(new JsonObject { ["reasoning"] = "thinking", ["reasoning_content"] = "more" })));
            await release.Task;
            await source.WriteAsync(Encoding.UTF8.GetBytes(Chunk(new JsonObject { ["content"] = Block()[..^10] }) + Chunk(new JsonObject(), "stop") + "data: [DONE]\n\n"));
        }, destination, Names, CancellationToken.None);
        await Task.Delay(10);
        var early = Encoding.UTF8.GetString(destination.ToArray());
        Assert.Contains(": heartbeat", early);
        Assert.Contains("\"reasoning\":\"thinking\"", early);
        Assert.Contains("\"reasoning_content\":\"more\"", early);
        Assert.False(processing.IsCompleted);

        release.SetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(1));
        var output = Encoding.UTF8.GetString(destination.ToArray());
        Assert.True(output.IndexOf("\"reasoning\":\"thinking\"", StringComparison.Ordinal) < output.IndexOf("\"code\":\"invalid_raw_tool_call\"", StringComparison.Ordinal));
        Assert.Contains("\"reason\":\"invalid_raw_call\"", output);
        Assert.Contains("reason: invalid_raw_call", output);
        Assert.Equal(1, output.Split("data: [DONE]").Length - 1);
    }

    [Fact]
    public async Task RawGuardReportsSafeSpecificReasons()
    {
        var invalid = await Process(Chunk(new JsonObject { ["content"] = "<tool_calls><invoke name=\"unknown\"></invoke></tool_calls>" }) + Chunk(new JsonObject(), "stop"));
        AssertReason(invalid, "invalid_raw_call");

        var nonToolFinish = await Process(Chunk(new JsonObject { ["content"] = Block() }) + Chunk(new JsonObject(), "length"));
        AssertReason(nonToolFinish, "non_tool_finish");

        var native = new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = "native" }) };
        var mixed = await Process(Chunk(new JsonObject { ["content"] = Block() }) + Chunk(native) + Chunk(new JsonObject(), "stop"));
        AssertReason(mixed, "mixed_native_raw_calls");

        var incomplete = await Process(Chunk(new JsonObject { ["content"] = Block()[..^10] }) + "data: [DONE]\n\n");
        AssertReason(incomplete, "incomplete_raw_call");
    }

    [Fact]
    public async Task RawGuardReportsBufferLimitWithoutPayload()
    {
        var output = await Process(Chunk(new JsonObject { ["content"] = "<tool_calls>" + new string('x', 4 * 1024 * 1024) }));
        AssertReason(output, "buffer_limit");
        Assert.DoesNotContain(new string('x', 100), output);
    }

    [Fact]
    public async Task UpstreamErrorFrameIsPreservedOverBufferedRawCandidate()
    {
        const string unavailable = "{\"error\":{\"type\":\"model_output_error\",\"code\":\"unavailable_tool_call\"}}";
        var output = await Process(Chunk(new JsonObject { ["content"] = Block()[..^10] }) + "data: " + unavailable + "\n\ndata: [DONE]\n\n");
        Assert.Contains("\"code\":\"unavailable_tool_call\"", output);
        Assert.DoesNotContain("invalid_raw_tool_call", output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingFinishAfterReasoningReturnsExplicitErrorWithoutPendingContent(bool done)
    {
        const string pending = "```xml\n<tool_calls><invoke name=\"unknown\"></invoke></tool_calls>\n```";
        var input = Chunk(new JsonObject { ["reasoning"] = "thinking" }) + Chunk(new JsonObject { ["content"] = pending });
        if (done) input += "data: [DONE]\n\n";
        var output = await Process(input);
        Assert.Contains("\"reasoning\":\"thinking\"", output);
        Assert.Contains("\"code\":\"upstream_stream_incomplete\"", output);
        Assert.Contains("\"source\":\"mediallmproxy.raw_tool_guard\"", output);
        Assert.Contains("\"reason\":\"missing_finish\"", output);
        Assert.Contains("upstream stream ended before completion", output);
        Assert.DoesNotContain(pending, output);
        Assert.Equal(1, output.Split("data: [DONE]").Length - 1);
    }

    [Fact]
    public async Task ObservationInjectionComposesThroughPipe()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Chunk(new JsonObject { ["content"] = Block() }) + Chunk(new JsonObject(), "stop") + "data: [DONE]\n\n"));
        using var dest = new MemoryStream();
        await SseToolCallCure.PipeAsync(d => SseObservationInjector.InjectAsync(source, d, "OBSERVATION", CancellationToken.None), dest, Names, CancellationToken.None);
        var output = Encoding.UTF8.GetString(dest.ToArray());
        Assert.Contains("OBSERVATION", output);
        Assert.Contains("\"finish_reason\":\"tool_calls\"", output);
    }

    [Fact]
    public async Task WedgeOutputComposesThroughPipe()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Chunk(new JsonObject { ["content"] = Block() }) + Chunk(new JsonObject(), "stop")));
        using var dest = new MemoryStream();
        await SseToolCallCure.PipeAsync(d => SseWedgeProcessor.ProcessAsync(source, d,
            new OAIPreRouter.Cli.Models.LoopGuardOptions(),
            _ => Task.FromResult<(string?, string?)>((null, null)),
            (_, _, _) => Task.FromResult<Stream?>(null), null, CancellationToken.None), dest, Names, CancellationToken.None);
        Assert.Contains("\"finish_reason\":\"tool_calls\"", Encoding.UTF8.GetString(dest.ToArray()));
    }

    [Fact]
    public async Task InvalidPipeOutputTerminatesWithoutDeadlockingProducer()
    {
        using var dest = new MemoryStream();
        var error = await Record.ExceptionAsync(() => SseToolCallCure.PipeAsync(async d =>
        {
            await d.WriteAsync(Encoding.UTF8.GetBytes(Chunk(new JsonObject { ["content"] = Block()[..^10] }) + Chunk(new JsonObject(), "stop")));
            await d.FlushAsync();
            for (var i = 0; i < 100; i++) await d.WriteAsync(new byte[65536]);
        }, dest, Names, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(error);
        Assert.Contains("\"code\":\"invalid_raw_tool_call\"", Encoding.UTF8.GetString(dest.ToArray()));
    }

    private static string Chunk(JsonObject delta, string? finish = null) => "data: " + new JsonObject
    {
        ["id"] = "test", ["model"] = "deepseek", ["created"] = 123,
        ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish })
    }.ToJsonString() + "\n\n";

    private static async Task<string> Process(string input)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var dest = new MemoryStream();
        await SseToolCallCure.ProcessAsync(source, dest, Names, CancellationToken.None);
        return Encoding.UTF8.GetString(dest.ToArray());
    }

    private static List<JsonNode> Events(string output) => output.Split('\n').Where(l => l.StartsWith("data: {")).Select(l => JsonNode.Parse(l[6..])!).ToList();

    private static void AssertReason(string output, string reason)
    {
        Assert.Contains("\"code\":\"invalid_raw_tool_call\"", output);
        Assert.Contains("\"source\":\"mediallmproxy.raw_tool_guard\"", output);
        Assert.Contains("\"reason\":\"" + reason + "\"", output);
        Assert.Contains("reason: " + reason, output);
    }
}
