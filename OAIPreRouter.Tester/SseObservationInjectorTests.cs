using Xunit;
using System.Text;
using OAIPreRouter.Cli.Services;

namespace OAIPreRouter.Cli.Tests;

public class SseObservationInjectorTests
{
    private const string Block = "*** MEDIA OBSERVATION ***\nA red circle on a white background\n*** END OBSERVATION ***";

    private static string MakeStream(string firstDelta, string terminalDelta)
    {
        return
            "data: {\"id\":\"x1\",\"object\":\"chat.completion.chunk\",\"created\":123,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"" + firstDelta + "\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"x1\",\"object\":\"chat.completion.chunk\",\"created\":123,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":" + terminalDelta + ",\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
    }

    [Fact]
    public async Task InjectAsync_InsertsBlockBeforeTerminalChunk()
    {
        var source = new MemoryStream(Encoding.UTF8.GetBytes(MakeStream("\"Hello\"", "{}")));
        var dest = new MemoryStream();

        await SseObservationInjector.InjectAsync(source, dest, Block, CancellationToken.None);

        var output = Encoding.UTF8.GetString(dest.ToArray());
        Assert.Contains("*** MEDIA OBSERVATION ***", output);
        Assert.Contains("A red circle on a white background", output);
        Assert.Contains("*** END OBSERVATION ***", output);

        // The observation chunk must arrive BEFORE the terminal (finish_reason) chunk
        var obsIdx = output.IndexOf("MEDIA OBSERVATION", StringComparison.Ordinal);
        var stopIdx = output.IndexOf("\"finish_reason\":\"stop\"", StringComparison.Ordinal);
        Assert.True(obsIdx >= 0, "observation block present");
        Assert.True(stopIdx > obsIdx, "observation block injected before terminal chunk");

        // Original content and [DONE] preserved
        Assert.Contains("\"Hello\"", output);
        Assert.Contains("data: [DONE]", output);
    }

    [Fact]
    public async Task InjectAsync_AllOriginalChunksPreserved()
    {
        var source = new MemoryStream(Encoding.UTF8.GetBytes(MakeStream("\"Hello\"", "{}")));
        var dest = new MemoryStream();

        await SseObservationInjector.InjectAsync(source, dest, Block, CancellationToken.None);

        var output = Encoding.UTF8.GetString(dest.ToArray());
        // Both original data lines survive
        Assert.Equal(3, CountOccurrences(output, "data: {"));
        Assert.Equal(1, CountOccurrences(output, "data: [DONE]"));
    }

    [Fact]
    public async Task InjectAsync_DoesNotDoubleInject_WhenMultipleTerminalChunks()
    {
        // Some backends emit finish_reason on more than one chunk; only ONE injection allowed
        var stream =
            "data: {\"id\":\"x1\",\"object\":\"chat.completion.chunk\",\"created\":123,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"a\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"id\":\"x1\",\"object\":\"chat.completion.chunk\",\"created\":123,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: {\"id\":\"x1\",\"object\":\"chat.completion.chunk\",\"created\":123,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var source = new MemoryStream(Encoding.UTF8.GetBytes(stream));
        var dest = new MemoryStream();

        await SseObservationInjector.InjectAsync(source, dest, Block, CancellationToken.None);

        var output = Encoding.UTF8.GetString(dest.ToArray());
        Assert.Equal(1, CountOccurrences(output, "*** MEDIA OBSERVATION ***"));
    }

    [Fact]
    public async Task InjectAsync_EmptyStream_NoOutput()
    {
        var source = new MemoryStream();
        var dest = new MemoryStream();

        await SseObservationInjector.InjectAsync(source, dest, Block, CancellationToken.None);

        Assert.Equal(0, dest.Length);
    }

    [Fact]
    public async Task InjectAsync_NoFinishReason_InjectBeforeDone()
    {
        // Some backends end with [DONE] but never emit a terminal finish_reason chunk —
        // the observation must still be injected (before [DONE]), not lost.
        var stream =
            "data: {\"id\":\"x1\",\"object\":\"chat.completion.chunk\",\"created\":123,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"},\"finish_reason\":null}]}\n\n" +
            "data: [DONE]\n\n";
        var source = new MemoryStream(Encoding.UTF8.GetBytes(stream));
        var dest = new MemoryStream();

        await SseObservationInjector.InjectAsync(source, dest, Block, CancellationToken.None);

        var output = Encoding.UTF8.GetString(dest.ToArray());
        Assert.Contains("*** MEDIA OBSERVATION ***", output);
        var obsIdx = output.IndexOf("MEDIA OBSERVATION", StringComparison.Ordinal);
        var doneIdx = output.IndexOf("data: [DONE]", StringComparison.Ordinal);
        Assert.True(obsIdx >= 0 && doneIdx > obsIdx, "observation injected before [DONE]");
    }

    [Fact]
    public async Task InjectAsync_TruncatedStream_NoInjection()
    {
        // Unexpected EOF (no finish_reason, no [DONE]) must NOT get an observation —
        // an aborted/failed stream must not be made to look complete.
        var stream =
            "data: {\"id\":\"x1\",\"object\":\"chat.completion.chunk\",\"created\":123,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"},\"finish_reason\":null}]}\n\n";
        var source = new MemoryStream(Encoding.UTF8.GetBytes(stream));
        var dest = new MemoryStream();

        await SseObservationInjector.InjectAsync(source, dest, Block, CancellationToken.None);

        var output = Encoding.UTF8.GetString(dest.ToArray());
        Assert.DoesNotContain("MEDIA OBSERVATION", output);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
