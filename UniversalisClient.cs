using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace AygeaMarketInsight;

public sealed class UniversalisClient : IDisposable
{
    private readonly IPluginLog log;
    private readonly HttpClient http;
    private readonly SemaphoreSlim concurrencyLimit = new(8);

    public UniversalisClient(IPluginLog log)
    {
        this.log = log;

        var version = typeof(UniversalisClient).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        http = new HttpClient
        {
            DefaultRequestHeaders =
            {
                { "User-Agent", $"AygeaMarketInsight/{version}" },
            },
        };
    }

    public async Task<Dictionary<uint, UniversalisItemPrice>> FetchPrices(
        uint worldId,
        IEnumerable<uint> itemIds,
        int ttlMinutes,
        Action<int, int>? onProgress = null)
    {
        var results = new Dictionary<uint, UniversalisItemPrice>();
        var batchList = itemIds.Distinct().Chunk(100).ToArray();
        var totalBatches = batchList.Length;

        for (int batchIdx = 0; batchIdx < totalBatches; batchIdx++)
        {
            try
            {
                await concurrencyLimit.WaitAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var ids = string.Join(",", batchList[batchIdx]);
                var url = $"https://universalis.app/api/v2/aggregated/{worldId}/{ids}";

                var response = await http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    log.Warning($"Universalis aggregated API returned {response.StatusCode} for {url}: {(body.Length > 200 ? body[..200] : body)}");
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("results", out var resultsArr))
                    continue;

                foreach (var item in resultsArr.EnumerateArray())
                {
                    if (!item.TryGetProperty("itemId", out var idEl)) continue;
                    var itemId = idEl.GetUInt32();

                    uint nqWorld = 0, hqWorld = 0, nqDc = 0, hqDc = 0;
                    float nqVel = 0, hqVel = 0;

                    if (item.TryGetProperty("nq", out var nq) &&
                        nq.TryGetProperty("minListing", out var nqListing))
                    {
                        nqWorld = GetPrice(nqListing, "world");
                        nqDc = GetPrice(nqListing, "dc");
                    }

                    if (item.TryGetProperty("hq", out var hq) &&
                        hq.TryGetProperty("minListing", out var hqListing))
                    {
                        hqWorld = GetPrice(hqListing, "world");
                        hqDc = GetPrice(hqListing, "dc");
                    }

                    // Parse sale velocity
                    if (item.TryGetProperty("nq", out var nqObj))
                        nqVel = GetVelocity(nqObj);
                    if (item.TryGetProperty("hq", out var hqObj))
                        hqVel = GetVelocity(hqObj);

                    results[itemId] = new UniversalisItemPrice
                    {
                        ItemId = itemId,
                        NqPrice = nqWorld > 0 ? nqWorld : nqDc,
                        HqPrice = hqWorld > 0 ? hqWorld : hqDc,
                        NqDcPrice = nqDc,
                        HqDcPrice = hqDc,
                        NqSaleVelocity = nqVel,
                        HqSaleVelocity = hqVel,
                        Source = "Universalis",
                        ExpiresAt = DateTime.UtcNow + TimeSpan.FromMinutes(ttlMinutes),
                    };
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Universalis fetch failed");
            }
            finally
            {
                concurrencyLimit.Release();
                onProgress?.Invoke(batchIdx + 1, totalBatches);
            }
        }

        return results;
    }

    private static uint GetPrice(JsonElement listing, string scope)
    {
        if (!listing.TryGetProperty(scope, out var el)) return 0;
        if (!el.TryGetProperty("price", out var p)) return 0;
        return p.ValueKind == JsonValueKind.Null ? 0 : p.GetUInt32();
    }

    private static float GetVelocity(JsonElement quality)
    {
        if (!quality.TryGetProperty("dailySaleVelocity", out var vel)) return 0;
        if (!vel.TryGetProperty("world", out var world)) return 0;
        if (!world.TryGetProperty("quantity", out var q)) return 0;
        return q.ValueKind == JsonValueKind.Null ? 0 : q.GetSingle();
    }

    public async Task<Dictionary<uint, (uint Price, string World)>> FetchDcBestSellPrices(
        string dcName,
        IEnumerable<uint> itemIds)
    {
        var results = new Dictionary<uint, (uint, string)>();
        var batchList = itemIds.Distinct().Chunk(100).ToArray();

        foreach (var batch in batchList)
        {
            try
            {
                await concurrencyLimit.WaitAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var ids = string.Join(",", batch);
                var url = $"https://universalis.app/api/v2/{dcName}/{ids}?listings=50";

                var response = await http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("items", out var itemsObj)) continue;

                foreach (var prop in itemsObj.EnumerateObject())
                {
                    if (!uint.TryParse(prop.Name, out var itemId)) continue;
                    var itemData = prop.Value;

                    if (!itemData.TryGetProperty("listings", out var listings)) continue;

                    // Find the cheapest listing per world, then pick the world with the highest floor price
                    var worldPrices = new Dictionary<string, uint>();
                    foreach (var listing in listings.EnumerateArray())
                    {
                        var price = listing.TryGetProperty("pricePerUnit", out var p) ? p.GetUInt32() : 0;
                        var world = listing.TryGetProperty("worldName", out var w) ? w.GetString() ?? "" : "";

                        if (price == 0 || string.IsNullOrEmpty(world)) continue;
                        if (!worldPrices.ContainsKey(world) || price < worldPrices[world])
                            worldPrices[world] = price;
                    }

                    if (worldPrices.Count == 0) continue;

                    var best = worldPrices.MaxBy(kvp => kvp.Value);
                    results[itemId] = (best.Value, best.Key);
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Universalis DC best-sell fetch failed");
            }
            finally
            {
                concurrencyLimit.Release();
            }
        }

        return results;
    }

    public void Dispose()
    {
        http.Dispose();
        concurrencyLimit.Dispose();
    }
}

public sealed class UniversalisItemPrice
{
    public uint ItemId { get; set; }
    public uint NqPrice { get; set; }
    public uint HqPrice { get; set; }
    public uint NqDcPrice { get; set; }
    public uint HqDcPrice { get; set; }
    public float NqSaleVelocity { get; set; }
    public float HqSaleVelocity { get; set; }
    public uint MaxDcPrice { get; set; }
    public string MaxDcPriceWorld { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string Source { get; set; } = "Universalis";
}
