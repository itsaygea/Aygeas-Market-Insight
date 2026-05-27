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

    // itemId → item name
    private readonly Dictionary<uint, string> itemNames = [];

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
        var items = dataManager.GetExcelSheet<Item>();
        if (items == null)
        {
            log.Warning("Failed to load Item sheet for vendor prices");
            return;
        }

        foreach (var item in items)
        {
            itemNames[item.RowId] = item.Name.ToString();

            // PriceLow = buy-from-vendor price (0 if not sold by any vendor)
            var cost = (uint)item.PriceLow;
            if (cost > 0)
            {
                if (!vendorPrices.TryGetValue(item.RowId, out var existing) || cost < existing)
                    vendorPrices[item.RowId] = cost;
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

    public string GetItemName(uint itemId)
    {
        return itemNames.TryGetValue(itemId, out var name) ? name : $"Item #{itemId}";
    }

    private static readonly string[] CraftJobNames =
    [
        "Carpenter", "Blacksmith", "Armorer", "Goldsmith",
        "Leatherworker", "Weaver", "Alchemist", "Culinarian",
    ];

    public (int Level, string CraftType, bool IsExpert) GetRecipeDifficulty(uint recipeId)
    {
        if (!recipeIdToRecipe.TryGetValue(recipeId, out var recipe))
            return (0, "???", false);

        var rlv = recipe.RecipeLevelTable.Value;
        int level = rlv.ClassJobLevel;
        var idx = (int)recipe.CraftType.Value.RowId;
        var craftType = idx >= 0 && idx < CraftJobNames.Length ? CraftJobNames[idx] : "???";
        bool isExpert = recipe.IsExpert;

        return (level, craftType, isExpert);
    }

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
