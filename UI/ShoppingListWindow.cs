using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ImGuiNET;
using Lumina.Excel.Sheets;

namespace AygeaMarketInsight.UI;

public sealed class ShoppingListWindow : Window
{
    private readonly Configuration config;
    private readonly RecipeCache recipeCache;
    private readonly PriceCache priceCache;
    private readonly UniversalisClient universalisClient;
    private readonly ArtisanIpc artisanIpc;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private bool isPinned;
    private bool showConfirmClear;

    // Aggregated ingredient data
    private List<ShoppingIngredient> ingredients = [];
    private bool needsRebuild = true;

    public ShoppingListWindow(
        Configuration config,
        RecipeCache recipeCache,
        PriceCache priceCache,
        UniversalisClient universalisClient,
        ArtisanIpc artisanIpc,
        IClientState clientState,
        IFramework framework,
        IPluginLog log)
        : base("Aygea's Market Insight — Shopping List###AMIShoppingList")
    {
        this.config = config;
        this.recipeCache = recipeCache;
        this.priceCache = priceCache;
        this.universalisClient = universalisClient;
        this.artisanIpc = artisanIpc;
        this.clientState = clientState;
        this.framework = framework;
        this.log = log;

        Size = new System.Numerics.Vector2(550, 400);
        SizeCondition = ImGuiCond.FirstUseEver;

        if (config.RememberPinState)
            isPinned = config.ShoppingListItems.Count > 0;
    }

    public override void Draw()
    {
        DrawHeader();
        DrawIngredientTable();
        DrawFooter();
    }

    public override void OnOpen()
    {
        needsRebuild = true;
    }

    private void DrawHeader()
    {
        // Pin button
        var pinLabel = isPinned ? "Unpin" : "Pin";
        if (ImGui.SmallButton(pinLabel))
        {
            isPinned = !isPinned;
            if (isPinned)
            {
                Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
                var opacity = config.PinnedWindowOpacity;
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, opacity);
            }
            else
            {
                Flags = ImGuiWindowFlags.None;
                ImGui.PopStyleVar();
            }
        }

        ImGui.SameLine();
        ImGui.Text($"Shopping List ({config.ShoppingListItems.Count} recipes)");

        if (needsRebuild)
        {
            RebuildIngredients();
            needsRebuild = false;
        }
    }

    private void DrawIngredientTable()
    {
        if (ingredients.Count == 0)
        {
            ImGui.TextDisabled("No items in shopping list.");
            ImGui.TextDisabled("Right-click a recipe in the Profit Scanner to add items.");
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable;

        if (!ImGui.BeginTable("ShoppingTable", 5, flags))
            return;

        ImGui.TableSetupColumn("Item Name");
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.None, 50);
        ImGui.TableSetupColumn("Current Price", ImGuiTableColumnFlags.None, 100);
        ImGui.TableSetupColumn("Max Price", ImGuiTableColumnFlags.None, 100);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.None, 70);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var ing in ingredients)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            if (config.HighlightOverBudgetIngredients && ing.IsOverBudget)
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.2f, 0.2f, 1f), ing.ItemName);
            else
                ImGui.Text(ing.ItemName);

            ImGui.TableSetColumnIndex(1);
            ImGui.Text($"{ing.Quantity}");

            ImGui.TableSetColumnIndex(2);
            ImGui.Text(ing.CurrentPrice > 0 ? $"{ing.CurrentPrice:N0} gil" : "?");

            ImGui.TableSetColumnIndex(3);
            ImGui.Text(ing.MaxPrice > 0 ? $"{ing.MaxPrice:N0} gil" : "—");

            ImGui.TableSetColumnIndex(4);
            ImGui.Text(ing.Source);
        }

        ImGui.EndTable();
    }

    private void DrawFooter()
    {
        ImGui.Separator();

        // Copy List button
        if (ImGui.Button("Copy List"))
            CopyListToClipboard();

        ImGui.SameLine();

        // Clear button with confirmation
        if (!showConfirmClear)
        {
            if (ImGui.Button("Clear"))
                showConfirmClear = true;
        }
        else
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f), "Clear all?");
            ImGui.SameLine();
            if (ImGui.SmallButton("Yes"))
            {
                config.ShoppingListItems.Clear();
                config.Save();
                ingredients.Clear();
                showConfirmClear = false;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("No"))
                showConfirmClear = false;
        }

        // Artisan button (only if available)
        if (artisanIpc.Available)
        {
            ImGui.SameLine();
            if (ImGui.Button("Add to Artisan"))
            {
                foreach (var entry in config.ShoppingListItems)
                    artisanIpc.CraftItem((ushort)entry.RecipeId, entry.Quantity);
            }
        }
    }

    private void RebuildIngredients()
    {
        ingredients.Clear();
        var aggregated = new Dictionary<uint, ShoppingIngredient>();

        foreach (var entry in config.ShoppingListItems)
        {
            var recipe = recipeCache.GetRecipe(entry.RecipeId);
            if (recipe == null) continue;

            var r = recipe.Value;
            var resultItemId = r.ItemResult.RowId;

            // Get sell price for the result item
            var cached = priceCache.Get(resultItemId);
            uint sellPrice = cached?.NqPrice ?? 0;

            foreach (var ing in r.Ingredients())
            {
                if (ing.Amount <= 0 || ing.Item.RowId == 0)
                    continue;

                var itemId = ing.Item.RowId;
                var qty = ing.Amount * entry.Quantity;

                if (!aggregated.TryGetValue(itemId, out var existing))
                {
                    existing = new ShoppingIngredient
                    {
                        ItemId = itemId,
                        ItemName = ing.Item.Value.Name.ToDalamudString().ToString(),
                        Quantity = 0,
                    };
                    aggregated[itemId] = existing;
                }

                existing.Quantity += qty;
            }
        }

        // Calculate max price and current price for each ingredient
        foreach (var ing in aggregated.Values)
        {
            // Current price
            var cached = priceCache.Get(ing.ItemId);
            var vendorPrice = recipeCache.GetVendorPrice(ing.ItemId);

            if (cached != null && cached.NqPrice > 0)
            {
                ing.CurrentPrice = cached.NqPrice;
                ing.Source = (vendorPrice > 0 && vendorPrice <= cached.NqPrice) ? "Vendor" : "MB";
            }
            else if (vendorPrice > 0)
            {
                ing.CurrentPrice = vendorPrice;
                ing.Source = "Vendor";
            }

            // Max price calculation per ingredient:
            // maxPrice_i = (sellPrice - sumOfOtherIngredientCosts) / qty_i
            CalculateMaxPrice(ing);
        }

        // Sort: over-budget first, then by source, then alphabetical
        ingredients = aggregated.Values
            .OrderByDescending(i => i.IsOverBudget)
            .ThenBy(i => i.Source)
            .ThenBy(i => i.ItemName)
            .ToList();
    }

    private void CalculateMaxPrice(ShoppingIngredient ingredient)
    {
        // Calculate total cost of all OTHER ingredients across all recipes
        uint otherCosts = 0;

        foreach (var entry in config.ShoppingListItems)
        {
            var recipe = recipeCache.GetRecipe(entry.RecipeId);
            if (recipe == null) continue;

            var r = recipe.Value;
            var resultItemId = r.ItemResult.RowId;
            var cached = priceCache.Get(resultItemId);
            uint sellPrice = cached?.NqPrice ?? 0;

            foreach (var ing in r.Ingredients())
            {
                if (ing.Amount <= 0 || ing.Item.RowId == 0)
                    continue;

                if (ing.Item.RowId == ingredient.ItemId)
                    continue;

                var cost = recipeCache.GetVendorPrice(ing.Item.RowId);
                var ingCached = priceCache.Get(ing.Item.RowId);
                if (ingCached != null && ingCached.NqPrice > 0)
                {
                    if (cost == 0 || ingCached.NqPrice < cost)
                        cost = ingCached.NqPrice;
                }

                otherCosts += cost * (uint)ing.Amount * (uint)entry.Quantity;
            }

            // Only compute max price from recipes that actually use this ingredient
            var usesIngredient = r.Ingredients()
                .Any(i => i.Amount > 0 && i.Item.RowId == ingredient.ItemId);

            if (usesIngredient && sellPrice > 0)
            {
                var totalOther = otherCosts;
                var remaining = sellPrice > totalOther ? sellPrice - totalOther : 0;

                // How many of this ingredient are needed across this recipe
                var qtyNeeded = r.Ingredients()
                    .Where(i => i.Item.RowId == ingredient.ItemId)
                    .Sum(i => i.Amount * entry.Quantity);

                if (qtyNeeded > 0)
                {
                    var maxPerUnit = remaining / (uint)qtyNeeded;
                    if (maxPerUnit > 0 && (ingredient.MaxPrice == 0 || maxPerUnit < ingredient.MaxPrice))
                        ingredient.MaxPrice = maxPerUnit;
                }
            }

            otherCosts = 0; // Reset per recipe
        }
    }

    private void CopyListToClipboard()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Aygea's Market Insight — Shopping List ===");

        foreach (var ing in ingredients)
        {
            var priceInfo = ing.Source == "Vendor"
                ? $"Vendor: {ing.CurrentPrice:N0} gil"
                : $"MB: {ing.CurrentPrice:N0} gil";

            var maxInfo = ing.MaxPrice > 0 ? $"max {ing.MaxPrice:N0} gil" : "max —";
            sb.AppendLine($"[{ing.Quantity}x]  {ing.ItemName}  — {maxInfo}  ({priceInfo})");
        }

        sb.AppendLine("==============================================");
        ImGui.SetClipboardText(sb.ToString());
    }
}

internal sealed class ShoppingIngredient
{
    public uint ItemId;
    public string ItemName = string.Empty;
    public int Quantity;
    public uint CurrentPrice;
    public uint MaxPrice;
    public string Source = "?";
    public bool IsOverBudget => MaxPrice > 0 && CurrentPrice > MaxPrice;
}
