namespace OAIPreRouter.Cli.Services;

using System.Collections.Concurrent;

/// <summary>
/// Tracks active connections per backend and enforces concurrency limits.
/// Uses a simple counter with timeout-based cleanup to prevent leaks on client disconnects.
/// </summary>
public sealed class ConnectionLimiter : IDisposable
{
    private readonly ConcurrentDictionary<string, int> _counts = new();
    private readonly ConcurrentDictionary<string, Timer?> _timers = new();
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Tries to acquire a connection slot for the given backend.
    /// Returns true if the slot was acquired, false if the concurrency limit is reached.
    /// Backends with maxConcurrent == 0 are unlimited and always return true.
    /// </summary>
    public bool TryAcquire(string backendName, int maxConcurrent)
    {
        if (maxConcurrent <= 0)
            return true;

        var current = _counts.AddOrUpdate(backendName, 1, (_, v) => v + 1);

        if (current > maxConcurrent)
        {
            // Over limit — decrement back and reject
            _counts.AddOrUpdate(backendName, 0, (_, v) => Math.Max(0, v - 1));
            _timers.TryRemove(backendName, out _);
            return false;
        }

        // Start cleanup timer if not already running
        _timers.AddOrUpdate(backendName,
            _ => new Timer(_ => TryCleanup(backendName), null, _cleanupInterval, _cleanupInterval),
            (_, existing) => existing ?? new Timer(_ => TryCleanup(backendName), null, _cleanupInterval, _cleanupInterval));

        return true;
    }

    /// <summary>
    /// Releases a connection slot for the given backend.
    /// </summary>
    public void Release(string backendName)
    {
        _counts.AddOrUpdate(backendName, 0, (_, v) => Math.Max(0, v - 1));
    }

    /// <summary>
    /// Gets the current active connection count for a backend.
    /// </summary>
    public int GetCount(string backendName) => _counts.GetOrAdd(backendName, 0);

    private void TryCleanup(string backendName)
    {
        if (_counts.TryGetValue(backendName, out var count) && count == 0)
        {
            _counts.TryRemove(backendName, out _);
            if (_timers.TryRemove(backendName, out var timer))
                timer?.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _timers)
            kvp.Value?.Dispose();
        _timers.Clear();
        _counts.Clear();
    }
}
