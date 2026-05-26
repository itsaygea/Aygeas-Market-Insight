using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace AygeaMarketInsight.UI;

public sealed class TooltipHook : IDisposable
{
    private readonly IGameGui gameGui;
    private readonly RecipeCache recipeCache;
    private readonly PriceCache priceCache;
    private readonly UniversalisClient universalisClient;
    private readonly Configuration config;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private uint hoveredItemId;
    private bool needsDraw;
    private bool dataReady;

    private uint craftCost;
    private uint mbPrice;
    private int profit;
    private string itemName = string.Empty;
    private bool isHq;
    private List<RecipeCache.IngredientCost> breakdown = [];

    public TooltipHook(
        IGameGui gameGui,
        RecipeCache recipeCache,
        PriceCache priceCache,
        UniversalisClient universalisClient,
        Configuration config,
        IClientState clientState,
        IFramework framework,
        IPluginLog log)
    {
        this.gameGui = gameGui;
        this.recipeCache = recipeCache;
        this.priceCache = priceCache;
        this.universalisClient = universalisClient;
        this.config = config;
        this.clientState = clientState;
        this.framework = framework;
        this.log = log;

        gameGui.HoveredItemChanged += OnHoveredItemChanged;
    }

    private void OnHoveredItemChanged(ulong itemId)
    {
        if (itemId == 0)
        {
            hoveredItemId = 0;
            needsDraw = false;
            return;
        }

        // HQ items: value > 1,000,000 means HQ, subtract to get base
        isHq = itemId > 1_000_000;
        hoveredItemId = (uint)(itemId % 500_000);

        if (!config.EnableTooltipAugmentation || !recipeCache.HasRecipe(hoveredItemId))
        {
            needsDraw = false;
            return;
        }

        needsDraw = true;
        dataReady = false;

        // Kick off async price fetch for this item + its ingredients
        FetchPricesForItem(hoveredItemId);
    }

    private void FetchPricesForItem(uint itemId)
    {
        var recipes = recipeCache.GetRecipesForItem(itemId);
        if (recipes.Count == 0) return;

        var missingIds = new HashSet<uint> { itemId };

        foreach (var recipe in recipes)
        {
            foreach (var ing in recipe.Ingredients())
            {
                if (ing.Amount > 0 && ing.Item.RowId != 0)
                    missingIds.Add(ing.Item.RowId);
            }
        }

        // Filter to only items not cached and not already pending
        var toFetch = missingIds
            .Where(id => priceCache.Get(id) == null && !priceCache.IsPending(id))
            .ToList();

        if (toFetch.Count == 0)
        {
            // All data available — compute immediately
            ComputeTooltipData(itemId);
            return;
        }

        foreach (var id in toFetch)
            priceCache.MarkPending(id);

        var world = clientState.LocalPlayer?.HomeWorld.Value.Name.ToString() ?? "";
        var ttl = config.UniversalisCacheTtlMinutes;

        _ = Task.Run(async () =>
        {
            try
            {
                var results = await universalisClient.FetchPrices(world, toFetch, ttl);
                foreach (var kvp in results)
                {
                    var p = kvp.Value;
                    priceCache.Set(kvp.Key, p.NqPrice, p.HqPrice, p.Source,
                        TimeSpan.FromMinutes(ttl));
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Tooltip price fetch failed");
            }

            framework.RunOnFrameworkThread(() =>
            {
                if (hoveredItemId == itemId && needsDraw)
                    ComputeTooltipData(itemId);
            });
        });
    }

    private void ComputeTooltipData(uint itemId)
    {
        var recipes = recipeCache.GetRecipesForItem(itemId);
        if (recipes.Count == 0) return;

        // Find cheapest recipe to craft
        uint cheapestCost = uint.MaxValue;
        List<RecipeCache.IngredientCost> bestBreakdown = [];

        foreach (var recipe in recipes)
        {
            var cost = recipeCache.CalculateCraftCost(recipe, priceCache, out var bd);
            if (cost < cheapestCost)
            {
                cheapestCost = cost;
                bestBreakdown = bd;
            }
        }

        craftCost = cheapestCost == uint.MaxValue ? 0 : cheapestCost;
        breakdown = bestBreakdown;

        // Get MB price for the result item
        var cached = priceCache.Get(itemId);
        mbPrice = isHq ? cached?.HqPrice ?? 0 : cached?.NqPrice ?? 0;

        profit = (int)(mbPrice - craftCost);
        dataReady = true;
    }

    public void Draw()
    {
        if (!needsDraw || hoveredItemId == 0 || !config.EnableTooltipAugmentation)
            return;

        if (!dataReady)
        {
            if (config.ShowFetchingPlaceholder)
            {
                using var tooltip = ImRaii.Tooltip();
                if (tooltip)
                {
                    ImGui.TextColored(new System.Numerics.Vector4(0.6f, 0.6f, 0.6f, 1f),
                        "Aygea's Market Insight — Fetching prices...");
                }
            }
            return;
        }

        using (var tooltip = ImRaii.Tooltip())
        {
            if (!tooltip) return;

            ImGui.Separator();
            ImGui.Text("Aygea's Market Insight");
            ImGui.Separator();

            if (config.ShowCraftCostInTooltips)
                ImGui.Text($"Craft cost:   {craftCost:N0} gil");

            if (config.ShowMbPriceInTooltips)
                ImGui.Text($"MB price:     {mbPrice:N0} gil");

            if (config.ShowCraftCostInTooltips && config.ShowMbPriceInTooltips)
            {
                var profitText = profit >= 0
                    ? $"Craft saves {profit:N0} gil"
                    : $"Craft costs {Math.Abs(profit):N0} gil more";

                if (config.ColorProfitLossText)
                {
                    var color = profit >= 0
                        ? ImGui.ColorConvertU32ToFloat4(config.ProfitColor)
                        : ImGui.ColorConvertU32ToFloat4(config.LossColor);
                    ImGui.TextColored(color, $">> {profitText}");
                }
                else
                {
                    ImGui.Text($">> {profitText}");
                }
            }

            // Ingredient breakdown (collapsible)
            if (breakdown.Count > 0 && ImGui.TreeNode("Ingredient Breakdown"))
            {
                foreach (var ing in breakdown)
                {
                    var vendorPrice = recipeCache.GetVendorPrice(ing.ItemId);
                    var source = vendorPrice > 0 && vendorPrice <= ing.CostPerUnit ? "Vendor" : "MB";
                    ImGui.Text($"  {ing.Quantity}x — {ing.CostPerUnit:N0} gil each ({source})");
                }

                ImGui.TreePop();
            }
        }
    }

    public void Dispose()
    {
        gameGui.HoveredItemChanged -= OnHoveredItemChanged;
    }
}
