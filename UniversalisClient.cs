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
        string worldOrDc,
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
                var url = $"https://universalis.app/api/v2/{worldOrDc}/{ids}?listings=1&fields=items.minPrice,minPriceNQ,minPriceHQ";

                var response = await http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    log.Warning($"Universalis API returned {response.StatusCode}");
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("items", out var items))
                    continue;

                foreach (var prop in items.EnumerateObject())
                {
                    if (!uint.TryParse(prop.Name, out var itemId))
                        continue;

                    var obj = prop.Value;
                    uint nq = 0, hq = 0;

                    if (obj.TryGetProperty("minPriceNQ", out var nqEl) && nqEl.ValueKind != JsonValueKind.Null)
                        nq = nqEl.GetUInt32();

                    if (obj.TryGetProperty("minPriceHQ", out var hqEl) && hqEl.ValueKind != JsonValueKind.Null)
                        hq = hqEl.GetUInt32();

                    // Fallback: minPrice covers both if specific fields missing
                    if (nq == 0 && obj.TryGetProperty("minPrice", out var minEl) && minEl.ValueKind != JsonValueKind.Null)
                        nq = minEl.GetUInt32();

                    results[itemId] = new UniversalisItemPrice
                    {
                        ItemId = itemId,
                        NqPrice = nq,
                        HqPrice = hq,
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
    public DateTime ExpiresAt { get; set; }
    public string Source { get; set; } = "Universalis";
}
