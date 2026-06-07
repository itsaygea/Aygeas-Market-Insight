using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using AygeaMarketInsight;

namespace AygeaMarketInsight.UI;

public sealed class TooltipHook : IDisposable
{
    private readonly IGameGui gameGui;
    private readonly RecipeCache recipeCache;
    private readonly PriceCache priceCache;
    private readonly UniversalisClient universalisClient;
    private readonly Configuration config;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly InventoryScanner inventoryScanner;
    private CancellationTokenSource? hoverCts;

    private uint hoveredItemId;
    private bool needsDraw;
    private bool dataReady;

    private uint craftCost;
    private uint mbPriceRaw;
    private uint mbPriceAfterTax;
    private int profit;
    private string itemName = string.Empty;
    private bool isHq;
    private uint pinnedRecipeId;
    private List<RecipeCache.IngredientCost> breakdown = [];

    // Exposed for ItemDetailPopout
    public bool HasPinnedItem => pinnedRecipeId != 0;
    public PinnedItemData? CurrentPinnedData { get; private set; }

    public TooltipHook(
        IGameGui gameGui,
        RecipeCache recipeCache,
        PriceCache priceCache,
        UniversalisClient universalisClient,
        Configuration config,
        IObjectTable objectTable,
        IFramework framework,
        IPluginLog log,
        InventoryScanner inventoryScanner)
    {
        this.gameGui = gameGui;
        this.recipeCache = recipeCache;
        this.priceCache = priceCache;
        this.universalisClient = universalisClient;
        this.config = config;
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;
        this.inventoryScanner = inventoryScanner;

        gameGui.HoveredItemChanged += OnHoveredItemChanged;
    }

    private void OnHoveredItemChanged(object? sender, ulong itemId)
    {
        // Cancel any previous fetch for stale hovers
        hoverCts?.Cancel();
        hoverCts?.Dispose();
        hoverCts = new CancellationTokenSource();
        var token = hoverCts.Token;

        if (itemId == 0 || token.IsCancellationRequested)
        {
            hoveredItemId = 0;
            needsDraw = false;
            return;
        }

        isHq = itemId > 1_000_000;
        hoveredItemId = (uint)(itemId % 500_000);

        if (!config.EnableTooltipAugmentation || !recipeCache.HasRecipe(hoveredItemId))
        {
            needsDraw = false;
            return;
        }

        needsDraw = true;
        dataReady = false;
        FetchPricesForItem(hoveredItemId, token);
    }

    private void FetchPricesForItem(uint itemId, CancellationToken token = default)
    {
        var recipes = recipeCache.GetRecipesForItem(itemId);
        if (recipes.Count == 0) return;

        var missingIds = new HashSet<uint> { itemId };

        foreach (var recipe in recipes)
        {
            for (int i = 0; i < 8; i++)
            {
                var amount = (int)recipe.AmountIngredient[i];
                var ingItemId = recipe.Ingredient[i].RowId;
                if (amount > 0 && ingItemId != 0)
                    missingIds.Add(ingItemId);
            }
        }

        var toFetch = missingIds
            .Where(id => priceCache.Get(id) == null && !priceCache.IsPending(id))
            .ToList();

        if (toFetch.Count == 0)
        {
            ComputeTooltipData(itemId);
            return;
        }

        foreach (var id in toFetch)
            priceCache.MarkPending(id);

        var worldId = config.HomeWorldId > 0 ? config.HomeWorldId : (objectTable.LocalPlayer?.HomeWorld.RowId ?? 0);
        if (worldId == 0) return;
        var ttl = config.UniversalisCacheTtlMinutes;

#pragma warning disable CS4014
        _ = Task.Run(async () =>
        {
            if (token.IsCancellationRequested) return;

            try
            {
                var results = await universalisClient.FetchPrices(worldId, toFetch, ttl);
                token.ThrowIfCancellationRequested();

                foreach (var kvp in results)
                {
                    var p = kvp.Value;
                    priceCache.Set(kvp.Key, p.NqPrice, p.HqPrice, p.Source,
                        TimeSpan.FromMinutes(ttl));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    log.Warning(ex, "Tooltip price fetch failed");
                foreach (var id in toFetch)
                    priceCache.Set(id, 0, 0, "failed", TimeSpan.FromMinutes(1));
            }

            if (token.IsCancellationRequested) return;

            framework.RunOnFrameworkThread(() =>
            {
                if (!token.IsCancellationRequested && hoveredItemId == itemId && needsDraw)
                    ComputeTooltipData(itemId);
            });
        }, token);
#pragma warning restore CS4014
    }

    private void ComputeTooltipData(uint itemId)
    {
        var recipes = recipeCache.GetRecipesForItem(itemId);
        if (recipes.Count == 0) return;

        uint cheapestCost = uint.MaxValue;
        List<RecipeCache.IngredientCost> bestBreakdown = [];
        uint bestRecipeId = 0;

        foreach (var recipe in recipes)
        {
            var cost = recipeCache.CalculateCraftCost(recipe, priceCache, out var bd);
            if (cost < cheapestCost)
            {
                cheapestCost = cost;
                bestBreakdown = bd;
                bestRecipeId = recipe.RowId;
            }
        }

        craftCost = cheapestCost == uint.MaxValue ? 0 : cheapestCost;
        breakdown = bestBreakdown;
        pinnedRecipeId = bestRecipeId;

        var cached = priceCache.Get(itemId);
        mbPriceRaw = isHq ? cached?.HqPrice ?? 0 : cached?.NqPrice ?? 0;
        mbPriceAfterTax = (uint)(mbPriceRaw * (1f - config.SalesTaxPercent / 100f));

        profit = (int)(mbPriceAfterTax - craftCost);
        dataReady = true;

        itemName = recipeCache.GetRecipesForItem(itemId).FirstOrDefault().ItemResult.Value.Name.ToString();

        // Update pinned data for popout
        CurrentPinnedData = new PinnedItemData
        {
            ItemId = itemId,
            ItemName = itemName,
            RecipeId = bestRecipeId,
            CraftCost = craftCost,
            MbPriceRaw = cached?.NqPrice ?? 0,
            HqSnapshot = cached?.HqPrice ?? 0,
            MbPriceAfterTax = mbPriceAfterTax,
            Profit = profit,
            IsHq = isHq,
            Breakdown = new List<RecipeCache.IngredientCost>(breakdown),
        };
    }

    public void Draw()
    {
        if (!needsDraw || hoveredItemId == 0 || !config.EnableTooltipAugmentation)
            return;

        if (!dataReady)
        {
            if (config.ShowFetchingPlaceholder)
            {
                using var fetchingTooltip = ImRaii.Tooltip();
                ImGui.TextColored(new System.Numerics.Vector4(0.6f, 0.6f, 0.6f, 1f),
                    "Aygea's Market Insight — Fetching prices...");
            }
            return;
        }

        using var tooltip = ImRaii.Tooltip();

        ImGui.Separator();
        ImGui.Text("Aygea's Market Insight");
        ImGui.Separator();

        // Show inventory count if scanning is enabled
        if (config.EnableInventoryScanning)
        {
            uint have = inventoryScanner.GetItemQuantity(hoveredItemId);
            ImGui.Text($"You have:     {have:N0}");
            if (have > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(in inventory)");
            }
            ImGui.Separator();
        }

        // Check if we should show expanded tooltip (modifier key held)
        bool showExpanded = false;
        var io = ImGui.GetIO();
        switch (config.TooltipPopoutModifierKey)
        {
            case 0: showExpanded = true; break; // No modifier required - always show
            case 1: showExpanded = io.KeyCtrl; break; // Ctrl
            case 2: showExpanded = io.KeyShift; break; // Shift
            case 3: showExpanded = io.KeyAlt; break; // Alt
        }

        if (showExpanded)
        {
            // Expanded view with detailed breakdown
            if (config.ShowCraftCostInTooltips)
                ImGui.Text($"Craft cost:   {craftCost:N0} gil");

            if (config.ShowMbPriceInTooltips)
            {
                ImGui.Text($"MB price:     {mbPriceRaw:N0} gil");
                if (config.SalesTaxPercent > 0)
                    ImGui.TextDisabled($"  After tax:  {mbPriceAfterTax:N0} gil ({config.SalesTaxPercent:F0}%)");
            }

            if (config.ShowCraftCostInTooltips && config.ShowMbPriceInTooltips)
            {
                var profitText = profit >= 0
                    ? $"Profit: {profit:N0} gil"
                    : $"Loss: {Math.Abs(profit):N0} gil";

                if (config.ColorProfitLossText)
                {
                    var color = profit >= 0
                        ? ImGui.ColorConvertU32ToFloat4(config.ProfitColor)
                        : ImGui.ColorConvertU32ToFloat4(config.LossColor);
                    ImGui.TextColored(color, profitText);
                }
                else
                {
                    ImGui.Text(profitText);
                }
            }

            ImGui.Separator();

            // Show ingredient breakdown
            if (breakdown != null && breakdown.Count > 0)
            {
                ImGui.Text("Ingredients:");
                foreach (var ing in breakdown)
                {
                    string sourceText = ing.Source switch
                    {
                        "Vendor" => "[Vendor]",
                        "MB" => "[Market]",
                        "Craft" => "[Craft]",
                        _ => $"[{ing.Source}]"
                    };

                    ImGui.BulletText($"{sourceText} {recipeCache.GetItemName(ing.ItemId)} x{ing.Quantity} = {ing.TotalCost:N0} gil");
                    
                    // Show sub-craft breakdown if available
                    if (ing.SubCraftBreakdown != null && ing.SubCraftBreakdown.Count > 0)
                    {
                        foreach (var sub in ing.SubCraftBreakdown)
                        {
                            string subSourceText = sub.Source switch
                            {
                                "Vendor" => "  ↳ [Vendor]",
                                "MB" => "  ↳ [Market]",
                                "Craft" => "  ↳ [Craft]",
                                _ => $"  ↳ [{sub.Source}]"
                            };
                            ImGui.Text($"{subSourceText} {recipeCache.GetItemName(sub.ItemId)} x{sub.Quantity} = {sub.TotalCost:N0} gil");
                        }
                    }
                }
            }
        }
        else
        {
            // Compact view (original behavior)
            if (config.ShowCraftCostInTooltips)
                ImGui.Text($"Craft cost:   {craftCost:N0} gil");

            if (config.ShowMbPriceInTooltips)
            {
                ImGui.Text($"MB price:     {mbPriceRaw:N0} gil");
                if (config.SalesTaxPercent > 0)
                    ImGui.TextDisabled($"  After tax:  {mbPriceAfterTax:N0} gil ({config.SalesTaxPercent:F0}%)");
            }

            if (config.ShowCraftCostInTooltips && config.ShowMbPriceInTooltips)
            {
                var profitText = profit >= 0
                    ? $"Profit: {profit:N0} gil"
                    : $"Loss: {Math.Abs(profit):N0} gil";

                if (config.ColorProfitLossText)
                {
                    var color = profit >= 0
                        ? ImGui.ColorConvertU32ToFloat4(config.ProfitColor)
                        : ImGui.ColorConvertU32ToFloat4(config.LossColor);
                    ImGui.TextColored(color, profitText);
                }
                else
                {
                    ImGui.Text(profitText);
                }
            }
        }

        var keyLabel = config.TooltipPopoutModifierKey switch
        {
            0 => "Hover to pin details",
            2 => "Hold Shift to pin details",
            3 => "Hold Alt to pin details",
            _ => "Hold Ctrl to pin details",
        };
        if (config.EnableTooltipPopout)
            ImGui.TextDisabled(keyLabel);
    }

    public bool CheckPinRequest()
    {
        if (!dataReady || hoveredItemId == 0) return false;
        if (!config.EnableTooltipPopout) return false;

        var io = ImGui.GetIO();
        return config.TooltipPopoutModifierKey switch
        {
            0 => true, // No modifier required
            2 => io.KeyShift,
            3 => io.KeyAlt,
            _ => io.KeyCtrl, // Default: Ctrl
        };
    }

    public void Dispose()
    {
        hoverCts?.Cancel();
        hoverCts?.Dispose();
        gameGui.HoveredItemChanged -= OnHoveredItemChanged;
    }
}

public sealed class PinnedItemData
{
    public uint ItemId;
    public string ItemName = string.Empty;
    public uint RecipeId;
    public uint CraftCost;
    public uint MbPriceRaw;
    public uint HqSnapshot;
    public uint MbPriceAfterTax;
    public int Profit;
    public bool IsHq;
    public List<RecipeCache.IngredientCost> Breakdown = [];
}
