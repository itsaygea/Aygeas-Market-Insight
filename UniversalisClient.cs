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

                    results[itemId] = new UniversalisItemPrice
                    {
                        ItemId = itemId,
                        NqPrice = nqWorld > 0 ? nqWorld : nqDc,
                        HqPrice = hqWorld > 0 ? hqWorld : hqDc,
                        NqDcPrice = nqDc,
                        HqDcPrice = hqDc,
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
    public DateTime ExpiresAt { get; set; }
    public string Source { get; set; } = "Universalis";
}
