using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AygeaMarketInsight;

public sealed class PriceCache
{
    private readonly ConcurrentDictionary<uint, CachedPrice> cache = new();
    private readonly HashSet<uint> pendingFetches = [];
    private readonly object pendingLock = new();

    public CachedPrice? Get(uint itemId)
    {
        if (cache.TryGetValue(itemId, out var entry))
        {
            if (entry.ExpiresAt > DateTime.UtcNow)
                return entry;

            cache.TryRemove(itemId, out _);
        }

        return null;
    }

    public void Set(uint itemId, uint nqPrice, uint hqPrice, string source, TimeSpan ttl)
    {
        cache[itemId] = new CachedPrice
        {
            ItemId = itemId,
            NqPrice = nqPrice,
            HqPrice = hqPrice,
            Source = source,
            ExpiresAt = DateTime.UtcNow + ttl,
        };

        lock (pendingLock)
        {
            pendingFetches.Remove(itemId);
        }
    }

    public void SetRange(IEnumerable<CachedPrice> entries)
    {
        foreach (var entry in entries)
        {
            cache[entry.ItemId] = entry;
            lock (pendingLock)
            {
                pendingFetches.Remove(entry.ItemId);
            }
        }
    }

    public void Remove(uint itemId)
    {
        cache.TryRemove(itemId, out _);
    }

    public void Clear() => cache.Clear();

    public void MarkPending(uint itemId)
    {
        lock (pendingLock)
        {
            pendingFetches.Add(itemId);
        }
    }

    public bool IsPending(uint itemId)
    {
        lock (pendingLock)
        {
            return pendingFetches.Contains(itemId);
        }
    }

    public void ClearExpired()
    {
        foreach (var kvp in cache)
        {
            if (kvp.Value.ExpiresAt <= DateTime.UtcNow)
                cache.TryRemove(kvp.Key, out _);
        }
    }

    public int RemoveBySource(string source, TimeSpan keepNewerThan)
    {
        var cutoff = DateTime.UtcNow - keepNewerThan;
        var count = 0;

        foreach (var kvp in cache)
        {
            if (kvp.Value.Source == source && kvp.Value.ExpiresAt < cutoff)
            {
                cache.TryRemove(kvp.Key, out _);
                count++;
            }
        }

        return count;
    }
}

public sealed class CachedPrice
{
    public uint ItemId { get; set; }
    public uint NqPrice { get; set; }
    public uint HqPrice { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string Source { get; set; } = string.Empty;
}
