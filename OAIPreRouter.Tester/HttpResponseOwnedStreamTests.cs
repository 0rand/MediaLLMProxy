namespace OAIPreRouter.Cli.Tests;

using System.Net;
using OAIPreRouter.Cli.Services;
using Xunit;

public class HttpResponseOwnedStreamTests
{
    [Fact]
    public async Task DisposingStreamDisposesOwningResponseOnce()
    {
        var response = new HttpResponseMessage();
        var content = new TrackingContent(new MemoryStream([1, 2, 3]));
        response.Content = content;

        var stream = await HttpResponseOwnedStream.OpenAsync(response, CancellationToken.None);
        var first = new byte[3];
        Assert.Equal(3, await stream.ReadAsync(first));
        Assert.Equal([1, 2, 3], first);

        stream.Dispose();
        stream.Dispose();
        await stream.DisposeAsync();

        Assert.Equal(1, content.DisposeCount);
    }

    [Fact]
    public async Task DisposingStreamDisposesOwningResponseWithReplacementForwardFailure()
    {
        var response = new HttpResponseMessage();
        var content = new TrackingContent(new MemoryStream([1, 2, 3]));
        response.Content = content;
        var stream = await HttpResponseOwnedStream.OpenAsync(response, CancellationToken.None);

        await using (stream)
        {
            var destination = new ThrowingWriteStream();
            await Assert.ThrowsAsync<IOException>(() => stream.CopyToAsync(destination));
        }

        Assert.Equal(1, content.DisposeCount);
    }

    private sealed class TrackingContent(Stream body) : HttpContent
    {
        public int DisposeCount { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => body.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Increment(ref _disposeCount) == 1) DisposeCount = 1;
            base.Dispose(disposing);
        }
        private int _disposeCount;
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("downstream failed");
    }
}
