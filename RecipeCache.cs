using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AygeaMarketInsight;

public sealed class RecipeCache
{
    private readonly IPluginLog log;

    // itemId → all recipes that produce this item
    private readonly Dictionary<uint, List<Recipe>> itemIdToRecipes = [];

    // recipeId → recipe
    private readonly Dictionary<uint, Recipe> recipeIdToRecipe = [];

    // itemId → vendor gil cost (0 if not sold by vendor)
    private readonly Dictionary<uint, uint> vendorPrices = [];

    public RecipeCache(IDataManager dataManager, IPluginLog log)
    {
        this.log = log;

        var recipes = dataManager.GetExcelSheet<Recipe>();
        if (recipes == null)
        {
            log.Warning("Failed to load Recipe sheet");
            return;
        }

        foreach (var recipe in recipes)
        {
            if (recipe.ItemResult.RowId == 0)
                continue;

            recipeIdToRecipe[recipe.RowId] = recipe;

            if (!itemIdToRecipes.TryGetValue(recipe.ItemResult.RowId, out var list))
            {
                list = [];
                itemIdToRecipes[recipe.ItemResult.RowId] = list;
            }

            list.Add(recipe);
        }

        LoadVendorPrices(dataManager);
        log.Information($"RecipeCache initialized: {recipeIdToRecipe.Count} recipes, {vendorPrices.Count} vendor items");
    }

    private void LoadVendorPrices(IDataManager dataManager)
    {
        var gilShopItems = dataManager.GetSubrowSheet<GilShopItem>();
        if (gilShopItems == null)
        {
            log.Warning("Failed to load GilShopItem sheet");
            return;
        }

        foreach (var row in gilShopItems)
        {
            foreach (var entry in row)
            {
                if (!entry.Item.IsValid) continue;
                var item = entry.Item.Value;
                var cost = (uint)item.PriceMid;
                if (cost > 0)
                {
                    if (!vendorPrices.TryGetValue(item.RowId, out var existing) || cost < existing)
                        vendorPrices[item.RowId] = cost;
                }
            }
        }
    }

    public IReadOnlyList<Recipe> GetRecipesForItem(uint itemId)
    {
        return itemIdToRecipes.TryGetValue(itemId, out var list) ? list : [];
    }

    public bool HasRecipe(uint itemId) => itemIdToRecipes.ContainsKey(itemId);

    public Recipe? GetRecipe(uint recipeId)
    {
        return recipeIdToRecipe.TryGetValue(recipeId, out var r) ? r : null;
    }

    public IReadOnlyDictionary<uint, Recipe> GetAllRecipes() => recipeIdToRecipe;

    public uint GetVendorPrice(uint itemId)
    {
        return vendorPrices.TryGetValue(itemId, out var price) ? price : 0;
    }

    public bool IsVendorItem(uint itemId) => vendorPrices.ContainsKey(itemId);

    public struct IngredientCost(uint itemId, int quantity, uint costPerUnit, uint totalCost)
    {
        public uint ItemId = itemId;
        public int Quantity = quantity;
        public uint CostPerUnit = costPerUnit;
        public uint TotalCost = totalCost;
    }

    public uint CalculateCraftCost(Recipe recipe, PriceCache priceCache, out List<IngredientCost> breakdown)
    {
        breakdown = [];
        uint total = 0;

        for (int i = 0; i < 8; i++)
        {
            var amount = (int)recipe.AmountIngredient[i];
            var itemId = recipe.Ingredient[i].RowId;
            if (amount <= 0 || itemId == 0)
                continue;

            var qty = amount;

            // Cheapest source: vendor price or MB price
            uint unitCost = GetVendorPrice(itemId);

            var cached = priceCache.Get(itemId);
            if (cached != null)
            {
                var mbPrice = cached.NqPrice;
                if (unitCost == 0 || (mbPrice > 0 && mbPrice < unitCost))
                    unitCost = mbPrice;
            }

            if (unitCost == 0)
                unitCost = GetVendorPrice(itemId);

            var lineCost = unitCost * (uint)qty;
            total += lineCost;
            breakdown.Add(new IngredientCost(itemId, qty, unitCost, lineCost));
        }

        return total;
    }
}
