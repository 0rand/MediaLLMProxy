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
    private long _rehomeOk;
    private long _samplingPinned;
    private long _samplingKept;
    private long _samplingParseFail;
    private long _loopGuardChecks;
    private long _loopGuardNudges;
    private long _loopGuardErrors;
    private long _loopGuardAdvisorMs;
    private long _loopGuardWedges;
    private long _loopGuardWedgeMs;
    private long _unavailableToolRetries;
    private long _unavailableToolRetrySuccess;
    private long _unavailableToolRetryFail;

    public void Scan() => Interlocked.Increment(ref _scanCount);
    public void DetourOk() => Interlocked.Increment(ref _detourOk);
    public void DetourTimeout() => Interlocked.Increment(ref _detourTimeout);
    public void DetourFail() => Interlocked.Increment(ref _detourFail);
    public void CacheHit() => Interlocked.Increment(ref _cacheHit);
    public void CacheMiss() => Interlocked.Increment(ref _cacheMiss);
    public void RewriteOk() => Interlocked.Increment(ref _rewriteOk);
    public void SttOk() => Interlocked.Increment(ref _sttOk);
    public void SttFail() => Interlocked.Increment(ref _sttFail);
    public void RehomeOk() => Interlocked.Increment(ref _rehomeOk);
    public void SamplingPinned() => Interlocked.Increment(ref _samplingPinned);
    public void SamplingKept() => Interlocked.Increment(ref _samplingKept);
    public void SamplingParseFail() => Interlocked.Increment(ref _samplingParseFail);
    public void LoopGuardCheck() => Interlocked.Increment(ref _loopGuardChecks);
    public void LoopGuardNudge() => Interlocked.Increment(ref _loopGuardNudges);
    public void LoopGuardError() => Interlocked.Increment(ref _loopGuardErrors);
    public void LoopGuardAdvisorMs(long ms) => Interlocked.Add(ref _loopGuardAdvisorMs, ms);
    public void LoopGuardWedge() => Interlocked.Increment(ref _loopGuardWedges);
    public void LoopGuardWedgeMs(long ms) => Interlocked.Add(ref _loopGuardWedgeMs, ms);
    public void UnavailableToolRetry() => Interlocked.Increment(ref _unavailableToolRetries);
    public void UnavailableToolRetrySuccess() => Interlocked.Increment(ref _unavailableToolRetrySuccess);
    public void UnavailableToolRetryFail() => Interlocked.Increment(ref _unavailableToolRetryFail);

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
        stt_fail = Interlocked.Read(ref _sttFail),
        rehome_ok = Interlocked.Read(ref _rehomeOk),
        sampling_pinned = Interlocked.Read(ref _samplingPinned),
        sampling_kept = Interlocked.Read(ref _samplingKept),
        sampling_parse_fail = Interlocked.Read(ref _samplingParseFail),
        loop_guard_checks = Interlocked.Read(ref _loopGuardChecks),
        loop_guard_nudges = Interlocked.Read(ref _loopGuardNudges),
        loop_guard_errors = Interlocked.Read(ref _loopGuardErrors),
        loop_guard_advisor_ms = Interlocked.Read(ref _loopGuardAdvisorMs),
        loop_guard_wedges = Interlocked.Read(ref _loopGuardWedges),
        loop_guard_wedge_ms = Interlocked.Read(ref _loopGuardWedgeMs),
        unavailable_tool_retries = Interlocked.Read(ref _unavailableToolRetries),
        unavailable_tool_retry_success = Interlocked.Read(ref _unavailableToolRetrySuccess),
        unavailable_tool_retry_fail = Interlocked.Read(ref _unavailableToolRetryFail)
    };
}
