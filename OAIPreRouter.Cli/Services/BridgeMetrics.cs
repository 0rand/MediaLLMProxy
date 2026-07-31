namespace OAIPreRouter.Cli.Services;

using System.Threading;

/// <summary>In-memory bridge counters (no external deps). Interlocked for thread safety.</summary>
public sealed class BridgeMetrics
{
    private long _scanCount;
    private long _detourOk;
    private long _detourTimeout;
    private long _detourFail;
    private long _cacheHit;
    private long _cacheMiss;
    private long _rewriteOk;
    private long _sttOk;
    private long _sttFail;

    public void Scan() => Interlocked.Increment(ref _scanCount);
    public void DetourOk() => Interlocked.Increment(ref _detourOk);
    public void DetourTimeout() => Interlocked.Increment(ref _detourTimeout);
    public void DetourFail() => Interlocked.Increment(ref _detourFail);
    public void CacheHit() => Interlocked.Increment(ref _cacheHit);
    public void CacheMiss() => Interlocked.Increment(ref _cacheMiss);
    public void RewriteOk() => Interlocked.Increment(ref _rewriteOk);
    public void SttOk() => Interlocked.Increment(ref _sttOk);
    public void SttFail() => Interlocked.Increment(ref _sttFail);

    public object Snapshot() => new
    {
        scan_count = Interlocked.Read(ref _scanCount),
        detour_ok = Interlocked.Read(ref _detourOk),
        detour_timeout = Interlocked.Read(ref _detourTimeout),
        detour_fail = Interlocked.Read(ref _detourFail),
        cache_hit = Interlocked.Read(ref _cacheHit),
        cache_miss = Interlocked.Read(ref _cacheMiss),
        rewrite_ok = Interlocked.Read(ref _rewriteOk),
        stt_ok = Interlocked.Read(ref _sttOk),
        stt_fail = Interlocked.Read(ref _sttFail)
    };
}
