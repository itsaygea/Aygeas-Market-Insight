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

    // recipeId → pre-computed static recipe info
    private readonly Dictionary<uint, RecipeInfo> recipeInfoCache = [];

    // recipeId → cached craft cost computation
    private readonly Dictionary<uint, CachedCraftCost> craftCostCache = [];

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
        BuildRecipeInfoCache();
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

    public sealed class CachedCraftCost
    {
        public uint CraftCost;
        public List<IngredientCost> Breakdown = [];
        public int PriceGeneration;
        public bool IgnoreExpiry;
    }

    public struct RecipeInfo
    {
        public string ItemName;
        public int ItemLevel;
        public string JobName;
        public byte JobId;
    }

    private static readonly string[] CraftJobAbbr =
    [
        "CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL",
    ];

    private void BuildRecipeInfoCache()
    {
        foreach (var (recipeId, recipe) in recipeIdToRecipe)
        {
            var craftIdx = (int)recipe.CraftType.Value.RowId;
            recipeInfoCache[recipeId] = new RecipeInfo
            {
                ItemName = recipe.ItemResult.Value.Name.ToString(),
                ItemLevel = (int)recipe.ItemResult.Value.LevelItem.RowId,
                JobName = craftIdx >= 0 && craftIdx < CraftJobAbbr.Length ? CraftJobAbbr[craftIdx] : "???",
                JobId = (byte)(craftIdx + 8),
            };
        }
    }

    public RecipeInfo? GetRecipeInfo(uint recipeId)
    {
        return recipeInfoCache.TryGetValue(recipeId, out var info) ? info : null;
    }

    public uint CalculateCraftCost(Recipe recipe, PriceCache priceCache, out List<IngredientCost> breakdown, bool ignoreExpiry = false)
    {
        var gen = priceCache.Generation;
        if (craftCostCache.TryGetValue(recipe.RowId, out var cached) &&
            cached.PriceGeneration == gen && cached.IgnoreExpiry == ignoreExpiry)
        {
            breakdown = cached.Breakdown;
            return cached.CraftCost;
        }

        var visited = new HashSet<uint>();
        var cost = CalculateCraftCostInternal(recipe, priceCache, out breakdown, ignoreExpiry, visited, 0);

        craftCostCache[recipe.RowId] = new CachedCraftCost
        {
            CraftCost = cost,
            Breakdown = breakdown,
            PriceGeneration = gen,
            IgnoreExpiry = ignoreExpiry,
        };

        return cost;
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

                visited.Add(itemId);
                foreach (var subRecipe in recipes)
                {
                    var craftCost = CalculateCraftCostInternal(subRecipe, priceCache, out var subBd, ignoreExpiry, visited, depth + 1);
                    if (craftCost > 0 && craftCost < bestCraftCost)
                    {
                        bestCraftCost = craftCost;
                        bestCraftBreakdown = subBd;
                    }
                }
                visited.Remove(itemId);

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
