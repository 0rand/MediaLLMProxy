namespace OAIPreRouter.Cli.Services;

/// <summary>A response body that retains and disposes its owning HTTP response.</summary>
public sealed class HttpResponseOwnedStream : Stream
{
    private readonly Stream inner;
    private HttpResponseMessage? owner;

    private HttpResponseOwnedStream(HttpResponseMessage response, Stream body)
    {
        owner = response;
        inner = body;
    }

    /// <summary>Transfer response ownership, including failures while opening its body.</summary>
    public static async Task<Stream> OpenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStreamAsync(ct);
            ct.ThrowIfCancellationRequested();
            return new HttpResponseOwnedStream(response, body);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // HttpResponseMessage owns HttpContent, which owns the body stream. Dispose
        // through that chain once rather than disposing the inner stream twice.
        if (disposing)
            Interlocked.Exchange(ref owner, null)?.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
