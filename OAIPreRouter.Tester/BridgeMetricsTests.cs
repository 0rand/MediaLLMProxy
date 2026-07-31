using System.Text.Json;
using Xunit;
using OAIPreRouter.Cli.Services;

namespace OAIPreRouter.Cli.Tests;

public class BridgeMetricsTests
{
    private static Dictionary<string, long> GetSnapshotValues(BridgeMetrics sut)
    {
        var json = JsonSerializer.Serialize(sut.Snapshot());
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, long>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.GetInt64();
        }
        return result;
    }

    [Fact]
    public void ZeroInitialized_AllCountersAreZero()
    {
        // Arrange & Act
        var sut = new BridgeMetrics();
        var snap = GetSnapshotValues(sut);

        // Assert
        Assert.Equal(0, snap["scan_count"]);
        Assert.Equal(0, snap["detour_ok"]);
        Assert.Equal(0, snap["detour_timeout"]);
        Assert.Equal(0, snap["detour_fail"]);
        Assert.Equal(0, snap["cache_hit"]);
        Assert.Equal(0, snap["cache_miss"]);
        Assert.Equal(0, snap["rewrite_ok"]);
        Assert.Equal(0, snap["stt_ok"]);
        Assert.Equal(0, snap["stt_fail"]);
    }

    [Fact]
    public void Scan_IncrementsScanCount()
    {
        // Arrange
        var sut = new BridgeMetrics();

        // Act
        sut.Scan();

        // Assert
        var snap = GetSnapshotValues(sut);
        Assert.Equal(1, snap["scan_count"]);
    }

    [Fact]
    public void DetourOk_IncrementsDetourOk()
    {
        // Arrange
        var sut = new BridgeMetrics();

        // Act
        sut.DetourOk();

        // Assert
        var snap = GetSnapshotValues(sut);
        Assert.Equal(1, snap["detour_ok"]);
    }

    [Fact]
    public void DetourTimeout_IncrementsDetourTimeout()
    {
        // Arrange
        var sut = new BridgeMetrics();

        // Act
        sut.DetourTimeout();

        // Assert
        var snap = GetSnapshotValues(sut);
        Assert.Equal(1, snap["detour_timeout"]);
    }

    [Fact]
    public void DetourFail_IncrementsDetourFail()
    {
        // Arrange
        var sut = new BridgeMetrics();

        // Act
        sut.DetourFail();

        // Assert
        var snap = GetSnapshotValues(sut);
        Assert.Equal(1, snap["detour_fail"]);
    }

    [Fact]
    public void CacheHit_IncrementsCacheHit()
    {
        // Arrange
        var sut = new BridgeMetrics();

        // Act
        sut.CacheHit();

        // Assert
        var snap = GetSnapshotValues(sut);
        Assert.Equal(1, snap["cache_hit"]);
    }

    [Fact]
    public void CacheMiss_IncrementsCacheMiss()
    {
        // Arrange
        var sut = new BridgeMetrics();

        // Act
        sut.CacheMiss();

        // Assert
        var snap = GetSnapshotValues(sut);
        Assert.Equal(1, snap["cache_miss"]);
    }

    [Fact]
    public void RewriteOk_IncrementsRewriteOk()
    {
        // Arrange
        var sut = new BridgeMetrics();

        // Act
        sut.RewriteOk();

        // Assert
        var snap = GetSnapshotValues(sut);
        Assert.Equal(1, snap["rewrite_ok"]);
    }

    [Fact]
    public void SttOk_IncrementsSttOk()
    {
        // Arrange
        var sut = new BridgeMetrics();

        // Act
        sut.SttOk();

        // Assert
        var snap = GetSnapshotValues(sut);
        Assert.Equal(1, snap["stt_ok"]);
    }

    [Fact]
    public void SttFail_IncrementsSttFail()
    {
        // Arrange
        var sut = new BridgeMetrics();

        // Act
        sut.SttFail();

        // Assert
        var snap = GetSnapshotValues(sut);
        Assert.Equal(1, snap["stt_fail"]);
    }

    [Fact]
    public void Snapshot_ContainsAllNineKeys()
    {
        // Arrange
        var sut = new BridgeMetrics();
        sut.Scan();
        sut.DetourOk();
        sut.DetourTimeout();
        sut.DetourFail();
        sut.CacheHit();
        sut.CacheMiss();
        sut.RewriteOk();
        sut.SttOk();
        sut.SttFail();

        // Act
        var snap = GetSnapshotValues(sut);

        // Assert — all nine keys present with value 1
        Assert.Equal(1, snap["scan_count"]);
        Assert.Equal(1, snap["detour_ok"]);
        Assert.Equal(1, snap["detour_timeout"]);
        Assert.Equal(1, snap["detour_fail"]);
        Assert.Equal(1, snap["cache_hit"]);
        Assert.Equal(1, snap["cache_miss"]);
        Assert.Equal(1, snap["rewrite_ok"]);
        Assert.Equal(1, snap["stt_ok"]);
        Assert.Equal(1, snap["stt_fail"]);
    }

    [Fact]
    public void Concurrency_Interlocked_ProvidesThreadSafety()
    {
        // Arrange
        var sut = new BridgeMetrics();

        // Act
        Parallel.For(0, 100, _ => sut.Scan());

        // Assert
        var snap = GetSnapshotValues(sut);
        Assert.Equal(100, snap["scan_count"]);
    }
}
