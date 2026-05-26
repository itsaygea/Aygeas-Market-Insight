using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace AygeaMarketInsight.UI;

public sealed class ProfitScannerWindow : Window
{
    private readonly Configuration config;
    private readonly RecipeCache recipeCache;
    private readonly PriceCache priceCache;
    private readonly UniversalisClient universalisClient;
    private readonly ArtisanIpc artisanIpc;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private List<ScannerRow> rows = [];
    private bool isLoading;
    private DateTime lastRefreshTime;
    private string worldName = string.Empty;

    // Filters
    private int minProfit;
    private int minIlvl;
    private bool hqOnly;
    private string searchQuery = string.Empty;

    // Sorting
    private ImGuiSortDirection sortDirection;
    private int sortColumn = 5; // Profit by default

    // Job toggles: CRP=8, BSM=9, ARM=10, GSM=11, LTW=12, WVR=13, ALC=14, CUL=15
    private static readonly (string Name, byte JobId)[] CraftingJobs =
    [
        ("CRP", 8), ("BSM", 9), ("ARM", 10), ("GSM", 11),
        ("LTW", 12), ("WVR", 13), ("ALC", 14), ("CUL", 15),
    ];

    private readonly HashSet<byte> enabledJobs = [8, 9, 10, 11, 12, 13, 14, 15];

    public ProfitScannerWindow(
        Configuration config,
        RecipeCache recipeCache,
        PriceCache priceCache,
        UniversalisClient universalisClient,
        ArtisanIpc artisanIpc,
        IObjectTable objectTable,
        IFramework framework,
        IPluginLog log)
        : base("Aygea's Market Insight — Profit Scanner###AMIScanner")
    {
        this.config = config;
        this.recipeCache = recipeCache;
        this.priceCache = priceCache;
        this.universalisClient = universalisClient;
        this.artisanIpc = artisanIpc;
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;

        minProfit = config.DefaultMinProfitFilter;
        minIlvl = config.DefaultMinIlvlFilter;
        hqOnly = config.HqOnlyByDefault;

        Size = new System.Numerics.Vector2(800, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        DrawControls();

        if (isLoading)
        {
            ImGui.TextDisabled("Loading prices...");
        }

        DrawTable();

        // Status bar
        var world = objectTable.LocalPlayer?.HomeWorld.Value.Name.ToString() ?? "Unknown";
        var ago = lastRefreshTime == default ? "never" : $"{(DateTime.UtcNow - lastRefreshTime).TotalMinutes:F0}m ago";
        ImGui.TextDisabled($"Last refreshed: {ago}  |  {rows.Count} recipes  |  World: {world}");
    }

    private void DrawControls()
    {
        if (ImGui.Button("Refresh"))
            RefreshPrices();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Min Profit", ref minProfit, 100);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("Min iLvl", ref minIlvl, 5);

        ImGui.SameLine();
        ImGui.Checkbox("HQ only", ref hqOnly);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        ImGui.InputText("Search", ref searchQuery, 64);

        if (config.ShowJobFilterBar)
        {
            ImGui.Spacing();
            foreach (var (name, jobId) in CraftingJobs)
            {
                var enabled = enabledJobs.Contains(jobId);
                ImGui.PushStyleColor(ImGuiCol.Button,
                    enabled ? new System.Numerics.Vector4(0.3f, 0.6f, 0.3f, 1f) : new System.Numerics.Vector4(0.3f, 0.3f, 0.3f, 1f));

                if (ImGui.SmallButton(name))
                {
                    if (enabled) enabledJobs.Remove(jobId);
                    else enabledJobs.Add(jobId);
                }

                ImGui.PopStyleColor();
                ImGui.SameLine();
            }

            ImGui.NewLine();
        }
    }

    private void DrawTable()
    {
        var filteredRows = GetFilteredRows();

        var flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV |
                    ImGuiTableFlags.Resizable;

        if (!ImGui.BeginTable("ScannerTable", 7, flags,
            ImGui.GetContentRegionAvail() with { Y = ImGui.GetContentRegionAvail().Y - 30 }))
            return;

        ImGui.TableSetupColumn("Item Name", ImGuiTableColumnFlags.DefaultSort);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.DefaultSort, 50);
        ImGui.TableSetupColumn("iLvl", ImGuiTableColumnFlags.DefaultSort, 50);
        ImGui.TableSetupColumn("Craft Cost", ImGuiTableColumnFlags.DefaultSort, 90);
        ImGui.TableSetupColumn("MB Sell Price", ImGuiTableColumnFlags.DefaultSort, 100);
        ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.DefaultSort, 90);
        ImGui.TableSetupColumn("Margin %", ImGuiTableColumnFlags.DefaultSort, 70);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var sorts = ImGui.TableGetSortSpecs();
        if (sorts.SpecsDirty && sorts.SpecsCount > 0)
        {
            var spec = sorts.Specs[0];
            sortColumn = spec.ColumnIndex;
            sortDirection = spec.SortDirection;
            sorts.SpecsDirty = false;
        }

        foreach (var row in filteredRows)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Text(row.ItemName);
            ImGui.TableSetColumnIndex(1);
            ImGui.Text(row.JobName);
            ImGui.TableSetColumnIndex(2);
            ImGui.Text($"{row.ItemLevel}");
            ImGui.TableSetColumnIndex(3);
            ImGui.Text($"{row.CraftCost:N0}");
            ImGui.TableSetColumnIndex(4);
            ImGui.Text($"{row.MbPrice:N0}");
            ImGui.TableSetColumnIndex(5);

            if (row.Profit >= 0)
                ImGui.TextColored(new System.Numerics.Vector4(0f, 0.8f, 0f, 1f), $"{row.Profit:N0}");
            else
                ImGui.TextColored(new System.Numerics.Vector4(0.8f, 0f, 0f, 1f), $"{row.Profit:N0}");

            ImGui.TableSetColumnIndex(6);
            ImGui.Text($"{row.Margin:P0}");

            // Right-click context menu
            if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
                ImGui.OpenPopup($"RowMenu_{row.RecipeId}");

            if (ImGui.BeginPopup($"RowMenu_{row.RecipeId}"))
            {
                if (ImGui.MenuItem("Add to Shopping List"))
                    AddToShoppingList(row);

                if (artisanIpc.Available)
                {
                    if (ImGui.MenuItem("Add to Artisan List"))
                        artisanIpc.CraftItem((ushort)row.RecipeId, 1);
                }

                ImGui.EndPopup();
            }
        }

        ImGui.EndTable();
    }

    private IEnumerable<ScannerRow> GetFilteredRows()
    {
        var query = rows.AsEnumerable();

        if (minProfit > 0)
            query = query.Where(r => r.Profit >= minProfit);

        if (minIlvl > 0)
            query = query.Where(r => r.ItemLevel >= minIlvl);

        if (hqOnly)
            query = query.Where(r => r.HqPrice > 0);

        if (!string.IsNullOrEmpty(searchQuery))
        {
            var sq = searchQuery.ToLowerInvariant();
            query = query.Where(r => r.ItemName.ToLowerInvariant().Contains(sq));
        }

        query = query.Where(r => enabledJobs.Contains(r.JobId));

        // Apply sorting
        query = sortDirection == ImGuiSortDirection.Ascending
            ? sortColumn switch
            {
                0 => query.OrderBy(r => r.ItemName),
                1 => query.OrderBy(r => r.JobName),
                2 => query.OrderBy(r => r.ItemLevel),
                3 => query.OrderBy(r => r.CraftCost),
                4 => query.OrderBy(r => r.MbPrice),
                5 => query.OrderBy(r => r.Profit),
                6 => query.OrderBy(r => r.Margin),
                _ => query,
            }
            : sortColumn switch
            {
                0 => query.OrderByDescending(r => r.ItemName),
                1 => query.OrderByDescending(r => r.JobName),
                2 => query.OrderByDescending(r => r.ItemLevel),
                3 => query.OrderByDescending(r => r.CraftCost),
                4 => query.OrderByDescending(r => r.MbPrice),
                5 => query.OrderByDescending(r => r.Profit),
                6 => query.OrderByDescending(r => r.Margin),
                _ => query,
            };

        return query;
    }

    private void RefreshPrices()
    {
        if (isLoading) return;

        worldName = objectTable.LocalPlayer?.HomeWorld.Value.Name.ToString() ?? "";
        if (string.IsNullOrEmpty(worldName)) return;

        isLoading = true;
        priceCache.RemoveBySource("Universalis", TimeSpan.FromMinutes(5));

        // Collect all item IDs we need prices for
        var allItemIds = new HashSet<uint>();
        foreach (var recipe in recipeCache.GetAllRecipes().Values)
        {
            allItemIds.Add(recipe.ItemResult.RowId);
            for (int i = 0; i < 8; i++)
            {
                var amount = (int)recipe.AmountIngredient[i];
                var itemId = recipe.Ingredient[i].RowId;
                if (amount > 0 && itemId != 0)
                    allItemIds.Add(itemId);
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var ttl = config.UniversalisCacheTtlMinutes;
                var results = await universalisClient.FetchPrices(worldName, allItemIds, ttl);

                framework.RunOnFrameworkThread(() =>
                {
                    foreach (var kvp in results)
                    {
                        var p = kvp.Value;
                        priceCache.Set(kvp.Key, p.NqPrice, p.HqPrice, p.Source,
                            TimeSpan.FromMinutes(ttl));
                    }

                    BuildRows();
                    lastRefreshTime = DateTime.UtcNow;
                    isLoading = false;
                });
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Scanner refresh failed");
                framework.RunOnFrameworkThread(() => isLoading = false);
            }
        });
    }

    private void BuildRows()
    {
        rows.Clear();
        foreach (var (recipeId, recipe) in recipeCache.GetAllRecipes())
        {
            var resultItemId = recipe.ItemResult.RowId;

            var craftCost = recipeCache.CalculateCraftCost(recipe, priceCache, out _);
            if (craftCost == 0) continue;

            var cached = priceCache.Get(resultItemId);
            var mbPrice = cached?.NqPrice ?? 0;
            var hqPrice = cached?.HqPrice ?? 0;
            var displayPrice = hqOnly ? hqPrice : mbPrice;

            if (displayPrice == 0) continue;

            var profit = (int)(displayPrice - craftCost);
            var margin = displayPrice > 0 ? (float)profit / displayPrice : 0f;

            var itemName = recipe.ItemResult.Value.Name.ToString();
            var itemLevel = recipe.ItemResult.Value.LevelItem.RowId;
            var craftType = recipe.CraftType.Value;
            var jobName = craftType.RowId switch
            {
                0 => "CRP", 1 => "BSM", 2 => "ARM", 3 => "GSM",
                4 => "LTW", 5 => "WVR", 6 => "ALC", 7 => "CUL",
                _ => "???",
            };

            // Map craft type index to job ID (CRP=8, BSM=9, ...)
            var jobId = (byte)(craftType.RowId + 8);

            rows.Add(new ScannerRow
            {
                RecipeId = recipeId,
                ResultItemId = resultItemId,
                ItemName = itemName,
                JobName = jobName,
                JobId = jobId,
                ItemLevel = (int)itemLevel,
                CraftCost = craftCost,
                MbPrice = displayPrice,
                HqPrice = hqPrice,
                Profit = profit,
                Margin = margin,
            });
        }
    }

    private void AddToShoppingList(ScannerRow row)
    {
        config.ShoppingListItems.Add(new ShoppingListEntry
        {
            RecipeId = row.RecipeId,
            Quantity = 1,
            RecipeName = row.ItemName,
            ResultItemId = row.ResultItemId,
        });
        config.Save();
    }
}

internal sealed class ScannerRow
{
    public uint RecipeId;
    public uint ResultItemId;
    public string ItemName = string.Empty;
    public string JobName = string.Empty;
    public byte JobId;
    public int ItemLevel;
    public uint CraftCost;
    public uint MbPrice;
    public uint HqPrice;
    public int Profit;
    public float Margin;
}
