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
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly System.Action<System.Action<string>?, System.Action?> refreshAll;

    private List<ScannerRow> rows = [];

    public System.Action? OnAddToShoppingList { get; set; }
    public System.Action<PinnedItemData>? OnOpenItemDetail { get; set; }
    public System.Action? OnRefreshComplete { get; set; }
    private bool isLoading;
    private string loadingStatus = string.Empty;
    private DateTime lastRefreshTime;
    private uint worldId;

    // Filters
    private int minProfit;
    private int minIlvl;
    private bool hqOnly;
    private string searchQuery = string.Empty;

    // Filter cache
    private List<ScannerRow>? cachedFilteredRows;
    private bool filtersDirty = true;

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
        IDataManager dataManager,
        IFramework framework,
        IPluginLog log,
        System.Action<System.Action<string>?, System.Action?> refreshAll)
        : base("Aygea's Market Insight — Profit Scanner###AMIScanner")
    {
        this.config = config;
        this.recipeCache = recipeCache;
        this.priceCache = priceCache;
        this.universalisClient = universalisClient;
        this.artisanIpc = artisanIpc;
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.framework = framework;
        this.log = log;
        this.refreshAll = refreshAll;

        minProfit = config.DefaultMinProfitFilter;
        minIlvl = config.DefaultMinIlvlFilter;
        hqOnly = config.HqOnlyByDefault;

        Size = new System.Numerics.Vector2(800, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        // Build rows from cached prices immediately for instant display
        if (rows.Count == 0 && !isLoading)
            BuildRows();

        // Auto-refresh if stale (older than cache TTL) or never fetched
        if (!isLoading && (lastRefreshTime == default ||
            (DateTime.UtcNow - lastRefreshTime).TotalMinutes > config.UniversalisCacheTtlMinutes))
            RefreshPrices();
    }

    public override void Draw()
    {
        DrawControls();

        if (isLoading)
        {
            if (rows.Count > 0)
                ImGui.TextDisabled($"Updating... {loadingStatus}  (showing data from {lastRefreshTime:HH:mm})");
            else
                ImGui.TextDisabled($"Loading prices... {loadingStatus}");
        }

        DrawTable();

        // Status bar
        var world = config.HomeWorldId > 0 ? config.HomeWorldName : (objectTable.LocalPlayer?.HomeWorld.Value.Name.ToString() ?? "Unknown");
        var ago = lastRefreshTime == default ? "never" : $"{(DateTime.UtcNow - lastRefreshTime).TotalMinutes:F0}m ago";
        ImGui.TextDisabled($"Last refreshed: {ago}  |  {rows.Count} recipes  |  World: {world}");
    }

    private void DrawControls()
    {
        var oldMinProfit = minProfit;
        var oldMinIlvl = minIlvl;
        var oldHqOnly = hqOnly;
        var oldSearchQuery = searchQuery;

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
                    filtersDirty = true;
                }

                ImGui.PopStyleColor();
                ImGui.SameLine();
            }

            ImGui.NewLine();
        }

        if (minProfit != oldMinProfit || minIlvl != oldMinIlvl || hqOnly != oldHqOnly || searchQuery != oldSearchQuery)
            filtersDirty = true;
    }

    private void DrawTable()
    {
        if (filtersDirty)
        {
            cachedFilteredRows = GetFilteredRows().ToList();
            filtersDirty = false;
        }

        var filteredRows = cachedFilteredRows ?? [];

        var flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;

        if (!ImGui.BeginTable("ScannerTable", 8, flags,
            ImGui.GetContentRegionAvail() with { Y = ImGui.GetContentRegionAvail().Y - 30 }))
            return;

        ImGui.TableSetupColumn("Item Name", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.DefaultSort, 50);
        ImGui.TableSetupColumn("iLvl", ImGuiTableColumnFlags.DefaultSort, 50);
        ImGui.TableSetupColumn("Craft Cost", ImGuiTableColumnFlags.DefaultSort, 90);
        ImGui.TableSetupColumn("MB Price (after tax)", ImGuiTableColumnFlags.DefaultSort, 110);
        ImGui.TableSetupColumn("Profit", ImGuiTableColumnFlags.DefaultSort, 90);
        ImGui.TableSetupColumn("Margin %", ImGuiTableColumnFlags.DefaultSort, 70);
        ImGui.TableSetupColumn("##add", ImGuiTableColumnFlags.NoSort, 30);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var sorts = ImGui.TableGetSortSpecs();
        if (sorts.SpecsDirty && sorts.SpecsCount > 0)
        {
            var spec = sorts.Specs[0];
            sortColumn = spec.ColumnIndex;
            sortDirection = spec.SortDirection;
            sorts.SpecsDirty = false;
            filtersDirty = true;
        }

        foreach (var row in filteredRows)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Text(row.ItemName);
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                OpenItemDetail(row);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Double-click to view details");
            ImGui.TableSetColumnIndex(1);
            ImGui.Text(row.JobName);
            ImGui.TableSetColumnIndex(2);
            ImGui.Text($"{row.ItemLevel}");
            ImGui.TableSetColumnIndex(3);
            ImGui.Text($"{row.CraftCost:N0}");
            ImGui.TableSetColumnIndex(4);
            ImGui.Text($"{row.MbPrice:N0}");
            if (row.MaxDcPrice > 0 && row.MaxDcPriceWorld.Length > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip($"Best sell: {row.MaxDcPrice:N0} on {row.MaxDcPriceWorld}");
            ImGui.TableSetColumnIndex(5);

            if (row.Profit >= 0)
                ImGui.TextColored(new System.Numerics.Vector4(0f, 0.8f, 0f, 1f), $"{row.Profit:N0}");
            else
                ImGui.TextColored(new System.Numerics.Vector4(0.8f, 0f, 0f, 1f), $"{row.Profit:N0}");

            ImGui.TableSetColumnIndex(6);
            ImGui.Text($"{row.Margin:P0}");

            // "+" button column
            ImGui.TableSetColumnIndex(7);
            if (ImGui.SmallButton($"+##add_{row.RecipeId}"))
                AddToShoppingList(row);

            // Right-click context menu (works on any cell in the row)
            if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
                ImGui.OpenPopup($"RowMenu_{row.RecipeId}");

            if (ImGui.BeginPopup($"RowMenu_{row.RecipeId}"))
            {
                if (ImGui.MenuItem("View Details"))
                    OpenItemDetail(row);

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
            query = query.Where(r => r.LowerItemName.Contains(sq));
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

        worldId = config.HomeWorldId > 0 ? config.HomeWorldId : (objectTable.LocalPlayer?.HomeWorld.RowId ?? 0);
        if (worldId == 0) return;

        isLoading = true;
        loadingStatus = "collecting items...";

        refreshAll(
            status => loadingStatus = status ?? string.Empty,
            () =>
            {
                // Also fetch DC best sell prices for result items
                var dcName = GetDcName();
                if (!string.IsNullOrEmpty(dcName))
                {
                    var resultItemIds = new HashSet<uint>();
                    foreach (var recipe in recipeCache.GetAllRecipes().Values)
                        resultItemIds.Add(recipe.ItemResult.RowId);

#pragma warning disable CS4014
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var dcResults = await universalisClient.FetchDcBestSellPrices(dcName, resultItemIds);
                            framework.RunOnFrameworkThread(() =>
                            {
                                foreach (var kvp in dcResults)
                                    priceCache.UpdateDcBestSell(kvp.Key, kvp.Value.Price, kvp.Value.World);
                                BuildRows();
                            });
                        }
                        catch (Exception ex)
                        {
                            log.Warning(ex, "DC best sell fetch failed");
                        }
                    });
#pragma warning restore CS4014
                }

                BuildRows();
                lastRefreshTime = DateTime.UtcNow;
                isLoading = false;
                loadingStatus = string.Empty;
                OnRefreshComplete?.Invoke();
            });
    }

    private void BuildRows()
    {
        rows.Clear();
        foreach (var (recipeId, recipe) in recipeCache.GetAllRecipes())
        {
            var resultItemId = recipe.ItemResult.RowId;

            var craftCost = recipeCache.CalculateCraftCost(recipe, priceCache, out _, ignoreExpiry: true);
            if (craftCost == 0) continue;

            var cached = priceCache.GetIgnoreExpiry(resultItemId);
            var mbPrice = cached?.NqPrice ?? 0;
            var hqPrice = cached?.HqPrice ?? 0;
            var maxDcPrice = cached?.MaxDcPrice ?? 0;
            var maxDcPriceWorld = cached?.MaxDcPriceWorld ?? "";
            var displayPrice = hqOnly ? hqPrice : mbPrice;

            if (displayPrice == 0) continue;

            var afterTax = (uint)(displayPrice * (1f - config.SalesTaxPercent / 100f));
            var profit = (int)(afterTax - craftCost);
            var margin = afterTax > 0 ? (float)profit / afterTax : 0f;

            var info = recipeCache.GetRecipeInfo(recipeId);
            if (info == null) continue;

            rows.Add(new ScannerRow
            {
                RecipeId = recipeId,
                ResultItemId = resultItemId,
                ItemName = info.Value.ItemName,
                LowerItemName = info.Value.ItemName.ToLowerInvariant(),
                JobName = info.Value.JobName,
                JobId = info.Value.JobId,
                ItemLevel = info.Value.ItemLevel,
                CraftCost = craftCost,
                MbPrice = displayPrice,
                HqPrice = hqPrice,
                MaxDcPrice = maxDcPrice,
                MaxDcPriceWorld = maxDcPriceWorld,
                Profit = profit,
                Margin = margin,
            });
        }

        filtersDirty = true;
    }

    private string? GetDcName()
    {
        var wid = config.HomeWorldId > 0 ? config.HomeWorldId : (objectTable.LocalPlayer?.HomeWorld.RowId ?? 0);
        if (wid == 0) return null;

        var worlds = dataManager.GetExcelSheet<World>();
        if (worlds == null) return null;

        foreach (var w in worlds)
        {
            if (w.RowId == wid)
                return w.DataCenter.Value.Name.ToString();
        }

        return null;
    }

    private void AddToShoppingList(ScannerRow row)
    {
        var existing = config.ShoppingListItems.FirstOrDefault(e => e.RecipeId == row.RecipeId);
        if (existing != null)
            existing.Quantity++;
        else
        {
            config.ShoppingListItems.Add(new ShoppingListEntry
            {
                RecipeId = row.RecipeId,
                Quantity = 1,
                RecipeName = row.ItemName,
                ResultItemId = row.ResultItemId,
            });
        }
        config.Save();
        OnAddToShoppingList?.Invoke();
    }

    private void OpenItemDetail(ScannerRow row)
    {
        var recipe = recipeCache.GetRecipe(row.RecipeId);
        if (recipe == null) return;

        var r = recipe.Value;
        var craftCost = recipeCache.CalculateCraftCost(r, priceCache, out var breakdown);
        var cached = priceCache.Get(row.ResultItemId);
        var mbPrice = cached?.NqPrice ?? 0;
        var hqPrice = cached?.HqPrice ?? 0;
        var afterTax = (uint)(row.MbPrice * (1f - config.SalesTaxPercent / 100f));

        OnOpenItemDetail?.Invoke(new PinnedItemData
        {
            ItemId = row.ResultItemId,
            ItemName = row.ItemName,
            RecipeId = row.RecipeId,
            CraftCost = craftCost,
            MbPriceRaw = mbPrice,
            HqSnapshot = hqPrice,
            MbPriceAfterTax = afterTax,
            Profit = (int)(afterTax - craftCost),
            IsHq = hqPrice > 0 && (mbPrice == 0 || hqPrice > mbPrice),
            Breakdown = breakdown,
        });
    }
}

internal sealed class ScannerRow
{
    public uint RecipeId;
    public uint ResultItemId;
    public string ItemName = string.Empty;
    public string LowerItemName = string.Empty;
    public string JobName = string.Empty;
    public byte JobId;
    public int ItemLevel;
    public uint CraftCost;
    public uint MbPrice;
    public uint HqPrice;
    public uint MaxDcPrice;
    public string MaxDcPriceWorld = string.Empty;
    public int Profit;
    public float Margin;
}
