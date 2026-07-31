using Xunit;
using OAIPreRouter.Cli.Services;
using OAIPreRouter.Cli.Models;
using Microsoft.Extensions.Options;

namespace OAIPreRouter.Cli.Tests;

public class ObservationCacheTests
{
    private static ObservationCache CreateCache(int ttlHours = 24, int capacity = 512)
    {
        var opts = Options.Create(new MultimodalOptions { CacheTtlHours = ttlHours, CacheCapacity = capacity });
        return new ObservationCache(opts);
    }

    [Fact]
    public void Hit_ReturnsCachedObservation()
    {
        var cache = CreateCache();
        var key = ObservationCache.BuildKey("data:image/png;base64,abc", "gpt-4o");
        cache.Set(key, "A cat sitting on a mat");
        Assert.True(cache.TryGet(key, out var obs));
        Assert.Equal("A cat sitting on a mat", obs);
    }

    [Fact]
    public void Miss_UnknownKey_ReturnsFalse()
    {
        var cache = CreateCache();
        var key = ObservationCache.BuildKey("data:image/png;base64,nonexistent", "gpt-4o");
        Assert.False(cache.TryGet(key, out _));
    }

    [Fact]
    public void TtlExpiry_EvictedEntry_ReturnsFalse()
    {
        var cache = CreateCache(ttlHours: 0); // 0 hours = already expired
        var key = ObservationCache.BuildKey("data:image/png;base64,abc", "gpt-4o");
        cache.Set(key, "A cat sitting on a mat");
        // With TTL=0, TimeSpan.FromHours(0) = TimeSpan.Zero
        // Need to ensure time has passed so the difference is non-zero
        System.Threading.Thread.Sleep(10);
        Assert.False(cache.TryGet(key, out _));
    }

    [Fact]
    public void CapacityEviction_EvictsOldest()
    {
        var cache = CreateCache(capacity: 2);
        var key1 = ObservationCache.BuildKey("data:image/png;base64,aaa", "gpt-4o");
        var key2 = ObservationCache.BuildKey("data:image/png;base64,bbb", "gpt-4o");
        var key3 = ObservationCache.BuildKey("data:image/png;base64,ccc", "gpt-4o");

        cache.Set(key1, "first");
        cache.Set(key2, "second");
        cache.Set(key3, "third");

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet(key1, out _)); // evicted
        Assert.True(cache.TryGet(key2, out var o2));
        Assert.Equal("second", o2);
        Assert.True(cache.TryGet(key3, out var o3));
        Assert.Equal("third", o3);
    }

    [Fact]
    public void KeyStability_SameInputs_YieldSameKey()
    {
        var dataUrl = "data:image/png;base64,abc123";
        var model = "gpt-4o";
        var key1 = ObservationCache.BuildKey(dataUrl, model);
        var key2 = ObservationCache.BuildKey(dataUrl, model);
        Assert.Equal(key1, key2);

        var key3 = ObservationCache.BuildKey("data:image/png;base64,different", model);
        Assert.NotEqual(key1, key3);

        var key4 = ObservationCache.BuildKey(dataUrl, "claude-sonnet-4-20250514");
        Assert.NotEqual(key1, key4);
    }

    [Fact]
    public void Concurrency_ParallelSets_AllRetrievable()
    {
        var cache = CreateCache(capacity: 1000);
        var keys = new string[100];
        for (int i = 0; i < 100; i++)
        {
            keys[i] = ObservationCache.BuildKey($"data:image/png;base64,{i}", "gpt-4o");
        }

        Parallel.For(0, 100, i => cache.Set(keys[i], $"observation-{i}"));

        Assert.Equal(100, cache.Count);
        Parallel.For(0, 100, i =>
        {
            Assert.True(cache.TryGet(keys[i], out var obs));
            Assert.Equal($"observation-{i}", obs);
        });
    }
}
