using System.Text;
using OAIPreRouter.Cli.Models;
using OAIPreRouter.Cli.Services;
using Xunit;

namespace OAIPreRouter.Cli.Tests;

public class SseWedgeProcessorTests
{
    [Fact]
    public async Task ProcessAsync_PreservesLfCrLfCommentsMultilineDataAndSplitUtf8()
    {
        var input =
            ": initial comment\r\n\r\n" +
            "data: {\"id\":\"x\",\"created\":1,\r\n" +
            "data: \"model\":\"m\",\"choices\":[{\"delta\":{\"reasoning_content\":\"é\"},\"finish_reason\":null}]}\r\n\r\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"total_tokens\":3}}\r\n\r\n" +
            "data: [DONE]\r\n\r\n";
        var judgeCalls = 0;
        await using var source = new ChunkedReadStream(Encoding.UTF8.GetBytes(input), 1, 2, 3);
        await using var destination = new MemoryStream();

        await SseWedgeProcessor.ProcessAsync(source, destination, Options(9999),
            _ => { judgeCalls++; return Task.FromResult<(string?, string?)>((null, null)); },
            (_, _, _) => Task.FromResult<Stream?>(null), null, CancellationToken.None);

        Assert.Equal(input, Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal(0, judgeCalls);
    }

    [Fact]
    public async Task ProcessAsync_FinishDisablesJudgeAndDrainsUsageAndDone()
    {
        var input = FinishEvent("stop") + UsageEvent() + DoneEvent();
        var judgeCalls = 0;
        await using var destination = new MemoryStream();

        await SseWedgeProcessor.ProcessAsync(StreamFor(input), destination, Options(1),
            _ => { judgeCalls++; return Task.FromResult<(string?, string?)>(("nudge", null)); },
            (_, _, _) => Task.FromResult<Stream?>(null), null, CancellationToken.None);

        Assert.Equal(input, Text(destination));
        Assert.Equal(0, judgeCalls);
    }

    [Fact]
    public async Task ProcessAsync_DoneWithoutFinish_IsForwardedWithoutInventingFinish()
    {
        var input = ReasoningEvent("short") + DoneEvent();
        await using var destination = new MemoryStream();

        await SseWedgeProcessor.ProcessAsync(StreamFor(input), destination, Options(9999),
            _ => Task.FromResult<(string?, string?)>((null, null)),
            (_, _, _) => Task.FromResult<Stream?>(null), null, CancellationToken.None);

        Assert.Equal(input, Text(destination));
        Assert.DoesNotContain("finish_reason\":\"stop", Text(destination));
    }

    [Fact]
    public async Task ProcessAsync_TruncatedEof_IsForwardedWithoutFabricatedTerminalEvent()
    {
        var input = ReasoningEvent("still working");
        await using var destination = new MemoryStream();

        await SseWedgeProcessor.ProcessAsync(StreamFor(input), destination, Options(9999),
            _ => Task.FromResult<(string?, string?)>((null, null)),
            (_, _, _) => Task.FromResult<Stream?>(null), null, CancellationToken.None);

        var output = Text(destination);
        Assert.Equal(input, output);
        Assert.DoesNotContain("[DONE]", output);
        Assert.DoesNotContain("finish_reason\":\"stop", output);
    }

    [Fact]
    public async Task ProcessAsync_UninspectableEvent_DisablesWedgeButPreservesRawStream()
    {
        var reasoning = new string('x', 70 * 1024);
        var input = ReasoningEvent(reasoning) + FinishEvent("stop") + DoneEvent();
        var judgeCalls = 0;
        await using var destination = new MemoryStream();

        await SseWedgeProcessor.ProcessAsync(StreamFor(input), destination, Options(1),
            _ => { judgeCalls++; return Task.FromResult<(string?, string?)>(("nudge", null)); },
            (_, _, _) => Task.FromResult<Stream?>(null), null, CancellationToken.None);

        Assert.Equal(input, Text(destination));
        Assert.Equal(0, judgeCalls);
    }

    [Fact]
    public async Task ProcessAsync_RoleThenErrorAndDone_PreservesErrorWithoutFinish()
    {
        var input =
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"error\":{\"message\":\"upstream rejected\"}}\n\n" +
            DoneEvent();
        await using var destination = new MemoryStream();

        await SseWedgeProcessor.ProcessAsync(StreamFor(input), destination, Options(9999),
            _ => Task.FromResult<(string?, string?)>((null, null)),
            (_, _, _) => Task.FromResult<Stream?>(null), null, CancellationToken.None);

        Assert.Equal(input, Text(destination));
        Assert.DoesNotContain("finish_reason\":\"stop", Text(destination));
    }

    [Fact]
    public async Task ProcessAsync_DownstreamWriteFailure_IsPropagated()
    {
        await using var destination = new ThrowingWriteStream();

        await Assert.ThrowsAsync<IOException>(() => SseWedgeProcessor.ProcessAsync(
            StreamFor(ReasoningEvent("abcdef")), destination, Options(9999),
            _ => Task.FromResult<(string?, string?)>((null, null)),
            (_, _, _) => Task.FromResult<Stream?>(null), null, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessAsync_WedgeReplacement_PreservesReplacementTerminalFramingAndDisposesIt()
    {
        var attempt1 = ReasoningEvent("abcdefgh");
        var replacement = "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":null}]}\r\n\r\n" +
                          FinishEvent("stop") + UsageEvent() + DoneEvent();
        var replacementStream = new TrackingStream(Encoding.UTF8.GetBytes(replacement));
        await using var destination = new MemoryStream();
        var aborted = false;

        await SseWedgeProcessor.ProcessAsync(StreamFor(attempt1), destination, Options(1),
            _ => Task.FromResult<(string?, string?)>(("answer directly", "summary")),
            (_, _, _) => Task.FromResult<Stream?>(replacementStream),
            () => aborted = true, CancellationToken.None);

        var output = Text(destination);
        Assert.StartsWith(attempt1, output);
        Assert.Contains("LOOP GUARD WEDGE", output);
        Assert.EndsWith(replacement, output);
        Assert.True(aborted);
        Assert.True(replacementStream.Disposed);
    }

    [Fact]
    public async Task ProcessAsync_JudgeOrReplacementFailure_KeepsOriginalAttempt()
    {
        var input = ReasoningEvent("abcdefgh") + FinishEvent("stop") + DoneEvent();
        await using var destinationAfterJudgeFailure = new MemoryStream();
        await SseWedgeProcessor.ProcessAsync(StreamFor(input), destinationAfterJudgeFailure, Options(1),
            _ => throw new InvalidOperationException("judge failed"),
            (_, _, _) => Task.FromResult<Stream?>(null), null, CancellationToken.None);
        Assert.Equal(input, Text(destinationAfterJudgeFailure));

        await using var destinationAfterReplacementFailure = new MemoryStream();
        await SseWedgeProcessor.ProcessAsync(StreamFor(input), destinationAfterReplacementFailure, Options(1),
            _ => Task.FromResult<(string?, string?)>(("nudge", null)),
            (_, _, _) => throw new IOException("replacement failed"), null, CancellationToken.None);
        Assert.Equal(input, Text(destinationAfterReplacementFailure));
    }

    [Fact]
    public async Task ProcessAsync_CancellationIsPropagated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await using var destination = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SseWedgeProcessor.ProcessAsync(
            new ChunkedReadStream(Encoding.UTF8.GetBytes(ReasoningEvent("abcdef")), 1), destination, Options(1),
            _ => Task.FromResult<(string?, string?)>((null, null)),
            (_, _, _) => Task.FromResult<Stream?>(null), null, cts.Token));
    }

    private static LoopGuardOptions Options(int threshold) => new()
    {
        Enabled = true,
        WedgeEnabled = true,
        WedgeReasoningTokens = threshold,
        MaxReasoningChars = 16,
        WedgeBanner = "[LOOP GUARD WEDGE]"
    };

    private static Stream StreamFor(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));
    private static string Text(MemoryStream stream) => Encoding.UTF8.GetString(stream.ToArray());
    private static string ReasoningEvent(string reasoning) =>
        "data: {\"id\":\"x\",\"created\":1,\"model\":\"m\",\"choices\":[{\"delta\":{\"reasoning_content\":\"" + reasoning + "\"},\"finish_reason\":null}]}\n\n";
    private static string FinishEvent(string reason) =>
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"" + reason + "\"}]}\n\n";
    private static string UsageEvent() => "data: {\"choices\":[],\"usage\":{\"total_tokens\":3}}\n\n";
    private static string DoneEvent() => "data: [DONE]\n\n";

    private sealed class ChunkedReadStream : MemoryStream
    {
        private readonly Queue<int> _chunks;

        public ChunkedReadStream(byte[] bytes, params int[] chunks) : base(bytes)
        {
            _chunks = new Queue<int>(chunks);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = _chunks.Count == 0 ? buffer.Length : _chunks.Dequeue();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, next)], cancellationToken);
        }
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("client disconnected");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("client disconnected"));
    }
}
