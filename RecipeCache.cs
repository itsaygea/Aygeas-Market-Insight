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
        // Load item names and PriceMid (Ask = vendor buy price) from Item sheet
        var items = dataManager.GetExcelSheet<Item>();
        if (items == null)
        {
            log.Warning("Failed to load Item sheet for vendor prices");
            return;
        }

        var itemAskPrices = new Dictionary<uint, uint>();
        foreach (var item in items)
        {
            itemNames[item.RowId] = item.Name.ToString();
            var ask = (uint)item.PriceMid;
            if (ask > 0)
                itemAskPrices[item.RowId] = ask;
        }

        // Cross-reference with GilShopItem to identify items actually sold by NPC vendors
        var gilShopItems = dataManager.GetSubrowExcelSheet<GilShopItem>();
        if (gilShopItems == null)
        {
            log.Warning("Failed to load GilShopItem sheet — vendor prices unavailable");
            return;
        }

        foreach (var shop in gilShopItems)
        {
            for (int i = 0; i < shop.Count; i++)
            {
                var entry = shop[i];
                var itemId = entry.Item.RowId;
                if (itemId == 0) continue;

                if (itemAskPrices.TryGetValue(itemId, out var price) && price > 0)
                {
                    if (!vendorPrices.TryGetValue(itemId, out var existing) || price < existing)
                        vendorPrices[itemId] = price;
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
        public string Source = "MB";
        public List<IngredientCost>? SubCraftBreakdown;
    }

    public uint CalculateCraftCost(Recipe recipe, PriceCache priceCache, out List<IngredientCost> breakdown, bool ignoreExpiry = false)
    {
        return CalculateCraftCostInternal(recipe, priceCache, out breakdown, ignoreExpiry, [], 0);
    }

    private const int MaxSubCraftDepth = 3;

    private uint CalculateCraftCostInternal(Recipe recipe, PriceCache priceCache, out List<IngredientCost> breakdown, bool ignoreExpiry, HashSet<uint> visited, int depth)
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

            // Start with vendor price
            uint unitCost = GetVendorPrice(itemId);
            string source = unitCost > 0 ? "Vendor" : "MB";
            List<IngredientCost>? subBreakdown = null;

            // Check MB price
            var cached = ignoreExpiry ? priceCache.GetIgnoreExpiry(itemId) : priceCache.Get(itemId);
            if (cached != null)
            {
                var mbPrice = cached.NqPrice;
                if (mbPrice > 0 && (unitCost == 0 || mbPrice < unitCost))
                {
                    unitCost = mbPrice;
                    source = "MB";
                }
            }

            // Check sub-craft if item is craftable, within depth limit, and not circular
            if (depth < MaxSubCraftDepth && !visited.Contains(itemId) && itemIdToRecipes.TryGetValue(itemId, out var recipes) && recipes.Count > 0)
            {
                uint bestCraftCost = uint.MaxValue;
                List<IngredientCost> bestCraftBreakdown = [];

                foreach (var subRecipe in recipes)
                {
                    var subVisited = new HashSet<uint>(visited) { itemId };
                    var craftCost = CalculateCraftCostInternal(subRecipe, priceCache, out var subBd, ignoreExpiry, subVisited, depth + 1);
                    if (craftCost > 0 && craftCost < bestCraftCost)
                    {
                        bestCraftCost = craftCost;
                        bestCraftBreakdown = subBd;
                    }
                }

                if (bestCraftCost != uint.MaxValue && bestCraftCost < unitCost)
                {
                    unitCost = bestCraftCost;
                    source = "Craft";
                    subBreakdown = bestCraftBreakdown;
                }
            }

            if (unitCost == 0)
                unitCost = GetVendorPrice(itemId);

            var lineCost = unitCost * (uint)qty;
            total += lineCost;
            breakdown.Add(new IngredientCost(itemId, qty, unitCost, lineCost)
            {
                Source = source,
                SubCraftBreakdown = subBreakdown,
            });
        }

        return total;
    }
}
