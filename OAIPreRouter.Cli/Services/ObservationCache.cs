namespace OAIPreRouter.Cli.Services;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OAIPreRouter.Cli.Models;

/// <summary>
/// Thread-safe observation cache keyed on SHA-256(media bytes + model + prompt version + request text).
/// TTL eviction on read; capacity cap (oldest-style eviction by insertion order is fine).
/// </summary>
public sealed class ObservationCache
{
    public const string PromptVersion = "prompt-v1";

    private sealed record CacheEntry(string Observation, DateTimeOffset CreatedAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly MultimodalOptions _opts;

    public ObservationCache(IOptions<MultimodalOptions> opts)
    {
        _opts = opts.Value;
    }

    public static string BuildKey(string dataUrl, string model) => BuildKey(dataUrl, model, string.Empty);

    /// <summary>
    /// Builds a key for a specific media interpretation request. The request text is part of the
    /// identity: identical image bytes can legitimately have different OCR, layout, or locate
    /// answers.
    /// </summary>
    public static string BuildKey(string dataUrl, string model, string requestText)
    {
        var material = string.Join('\0', dataUrl, model, PromptVersion, requestText);
        var bytes = Encoding.UTF8.GetBytes(material);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return hash;
    }

    public bool TryGet(string key, out string? observation)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (DateTimeOffset.UtcNow - entry.CreatedAt <= TimeSpan.FromHours(_opts.CacheTtlHours))
            {
                observation = entry.Observation;
                return true;
            }
            _entries.TryRemove(key, out _); // expired
        }
        observation = null;
        return false;
    }

    public void Set(string key, string observation)
    {
        if (_entries.Count >= _opts.CacheCapacity)
        {
            // capacity cap: evict the oldest entry
            KeyValuePair<string, CacheEntry>? oldest = null;
            foreach (var kv in _entries)
            {
                if (oldest == null || kv.Value.CreatedAt < oldest.Value.Value.CreatedAt)
                    oldest = kv;
            }
            if (oldest != null)
                _entries.TryRemove(oldest.Value.Key, out _);
        }
        _entries[key] = new CacheEntry(observation, DateTimeOffset.UtcNow);
    }

    public int Count => _entries.Count;
}
