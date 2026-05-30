using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AygeaMarketInsight;

public sealed class PriceCache
{
    private readonly ConcurrentDictionary<uint, CachedPrice> cache = new();
    private readonly HashSet<uint> pendingFetches = [];
    private readonly object pendingLock = new();

    public int Generation { get; private set; }

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

    public CachedPrice? GetIgnoreExpiry(uint itemId)
    {
        return cache.TryGetValue(itemId, out var entry) ? entry : null;
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
        Generation++;

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
        Generation++;
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

    public List<CachedPrice> GetAllEntries()
    {
        var entries = new List<CachedPrice>(cache.Count);
        foreach (var kvp in cache)
            entries.Add(kvp.Value);
        return entries;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void SaveToFile(string path)
    {
        try
        {
            var entries = GetAllEntries();
            var json = JsonSerializer.Serialize(entries, JsonOpts);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best-effort save — don't crash on shutdown
        }
    }

    public int LoadFromFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<CachedPrice>>(json, JsonOpts);
            if (entries == null) return 0;

            var loaded = 0;
            foreach (var entry in entries)
            {
                if (entry.ItemId == 0) continue;
                cache.TryAdd(entry.ItemId, entry);
                loaded++;
            }
            return loaded;
        }
        catch
        {
            return 0;
        }
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
