using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace AygeaMarketInsight.UI;

public sealed class ShoppingListWindow : Window
{
    private readonly Configuration config;
    private readonly RecipeCache recipeCache;
    private readonly PriceCache priceCache;
    private readonly ArtisanIpc artisanIpc;
    private readonly IPluginLog log;

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
        IFramework framework,
        IPluginLog log)
        : base("Aygea's Market Insight — Shopping List###AMIShoppingList")
    {
        this.config = config;
        this.recipeCache = recipeCache;
        this.priceCache = priceCache;
        this.artisanIpc = artisanIpc;
        this.log = log;

        Size = new System.Numerics.Vector2(600, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (config.ShoppingListItems.Count == 0)
        {
            ImGui.TextDisabled("No items in shopping list.");
            ImGui.TextDisabled("Right-click a recipe in the Profit Scanner to add items.");
            return;
        }

        DrawRecipeList();
        ImGui.Separator();
        DrawIngredientTable();
        DrawFooter();
    }

    public override void OnOpen()
    {
        needsRebuild = true;
    }

    private void DrawRecipeList()
    {
        ImGui.Text("Recipes");
        ImGui.Spacing();

        // Draw each recipe entry with quantity controls and remove button
        for (int i = config.ShoppingListItems.Count - 1; i >= 0; i--)
        {
            var entry = config.ShoppingListItems[i];
            var recipe = recipeCache.GetRecipe(entry.RecipeId);
            if (recipe == null) continue;

            ImGui.PushID((int)entry.RecipeId);

            // Recipe name
            ImGui.Text(entry.RecipeName);

            ImGui.SameLine();

            // Quantity controls
            if (ImGui.SmallButton("-"))
            {
                if (entry.Quantity > 1)
                    entry.Quantity--;
                else
                {
                    config.ShoppingListItems.RemoveAt(i);
                    needsRebuild = true;
                }
                config.Save();
            }

            ImGui.SameLine();
            ImGui.Text($"{entry.Quantity}");
            ImGui.SameLine();

            if (ImGui.SmallButton("+"))
            {
                entry.Quantity++;
                config.Save();
            }

            ImGui.SameLine();

            // Craft cost for this recipe
            var craftCost = recipeCache.CalculateCraftCost(recipe.Value, priceCache, out _);
            var cached = priceCache.Get(entry.ResultItemId);
            var mbPrice = cached?.NqPrice ?? 0;
            var afterTax = (uint)(mbPrice * (1f - config.SalesTaxPercent / 100f));
            var profit = (int)(afterTax - craftCost);

            if (mbPrice > 0 && craftCost > 0)
            {
                var profitColor = profit >= 0
                    ? ImGui.ColorConvertU32ToFloat4(config.ProfitColor)
                    : ImGui.ColorConvertU32ToFloat4(config.LossColor);
                var profitText = profit >= 0 ? $"+{profit:N0}" : $"{profit:N0}";
                ImGui.TextColored(profitColor, $"Profit: {profitText} gil");
            }
            else
            {
                ImGui.TextDisabled("Profit: —");
            }

            ImGui.SameLine();

            // Remove button
            ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.6f, 0.2f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.8f, 0.3f, 0.3f, 1f));
            if (ImGui.SmallButton("Remove"))
            {
                config.ShoppingListItems.RemoveAt(i);
                config.Save();
                needsRebuild = true;
            }
            ImGui.PopStyleColor(2);

            ImGui.PopID();
        }
    }

    private void DrawIngredientTable()
    {
        if (needsRebuild)
        {
            RebuildIngredients();
            needsRebuild = false;
        }

        if (ingredients.Count == 0)
            return;

        ImGui.Text("Materials");
        ImGui.Spacing();

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable;

        if (!ImGui.BeginTable("ShoppingTable", 5, flags,
            ImGui.GetContentRegionAvail() with { Y = ImGui.GetContentRegionAvail().Y - 40 }))
            return;

        ImGui.TableSetupColumn("Item Name", ImGuiTableColumnFlags.None, 250);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.None, 50);
        ImGui.TableSetupColumn("Best Price", ImGuiTableColumnFlags.None, 110);
        ImGui.TableSetupColumn("Max Price", ImGuiTableColumnFlags.None, 110);
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
            ImGui.Text(ing.BestPrice > 0 ? $"{ing.BestPrice:N0} gil" : "—");

            ImGui.TableSetColumnIndex(3);
            ImGui.Text(ing.MaxPrice > 0 ? $"{ing.MaxPrice:N0} gil" : "—");

            ImGui.TableSetColumnIndex(4);
            ImGui.Text(ing.Source);
        }

        ImGui.EndTable();
    }

    private void DrawFooter()
    {
        // Copy List button
        if (ImGui.Button("Copy List"))
            CopyListToClipboard();

        ImGui.SameLine();

        // Clear button with confirmation
        if (!showConfirmClear)
        {
            if (ImGui.Button("Clear All"))
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
                needsRebuild = true;
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
            if (ImGui.Button("Send to Artisan"))
            {
                foreach (var entry in config.ShoppingListItems)
                    artisanIpc.CraftItem((ushort)entry.RecipeId, entry.Quantity);
            }
        }

        // Total cost
        var totalCost = ingredients.Sum(i => (long)i.BestPrice * i.Quantity);
        ImGui.TextDisabled($"Total material cost: {totalCost:N0} gil");
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

            for (int i = 0; i < 8; i++)
            {
                var amount = (int)r.AmountIngredient[i];
                var itemId = r.Ingredient[i].RowId;
                if (amount <= 0 || itemId == 0)
                    continue;

                var qty = amount * entry.Quantity;

                if (!aggregated.TryGetValue(itemId, out var existing))
                {
                    existing = new ShoppingIngredient
                    {
                        ItemId = itemId,
                        ItemName = recipeCache.GetItemName(itemId),
                        Quantity = 0,
                    };
                    aggregated[itemId] = existing;
                }

                existing.Quantity += qty;
            }
        }

        // Calculate best price for each ingredient
        foreach (var ing in aggregated.Values)
        {
            var vendorPrice = recipeCache.GetVendorPrice(ing.ItemId);
            var cached = priceCache.Get(ing.ItemId);
            var mbPrice = cached?.NqPrice ?? 0;

            // Use cheapest: vendor or MB
            if (vendorPrice > 0 && (mbPrice == 0 || vendorPrice <= mbPrice))
            {
                ing.BestPrice = vendorPrice;
                ing.Source = "Vendor";
            }
            else if (mbPrice > 0)
            {
                ing.BestPrice = mbPrice;
                ing.Source = "MB";
            }
            else if (vendorPrice > 0)
            {
                ing.BestPrice = vendorPrice;
                ing.Source = "Vendor";
            }

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
        foreach (var entry in config.ShoppingListItems)
        {
            var recipe = recipeCache.GetRecipe(entry.RecipeId);
            if (recipe == null) continue;

            var r = recipe.Value;
            var resultItemId = r.ItemResult.RowId;
            var cached = priceCache.Get(resultItemId);
            uint sellPrice = (uint)((cached?.NqPrice ?? 0) * (1f - config.SalesTaxPercent / 100f));

            var usesIngredient = false;
            int qtyNeeded = 0;
            uint otherCosts = 0;

            for (int i = 0; i < 8; i++)
            {
                var amount = (int)r.AmountIngredient[i];
                var itemId = r.Ingredient[i].RowId;
                if (amount <= 0 || itemId == 0)
                    continue;

                if (itemId == ingredient.ItemId)
                {
                    usesIngredient = true;
                    qtyNeeded += amount * entry.Quantity;
                    continue;
                }

                var cost = recipeCache.GetVendorPrice(itemId);
                var ingCached = priceCache.Get(itemId);
                if (ingCached != null && ingCached.NqPrice > 0)
                {
                    if (cost == 0 || ingCached.NqPrice < cost)
                        cost = ingCached.NqPrice;
                }

                otherCosts += cost * (uint)amount * (uint)entry.Quantity;
            }

            if (usesIngredient && sellPrice > 0)
            {
                var remaining = sellPrice > otherCosts ? sellPrice - otherCosts : 0;
                if (qtyNeeded > 0)
                {
                    var maxPerUnit = remaining / (uint)qtyNeeded;
                    if (maxPerUnit > 0 && (ingredient.MaxPrice == 0 || maxPerUnit < ingredient.MaxPrice))
                        ingredient.MaxPrice = maxPerUnit;
                }
            }
        }
    }

    private void CopyListToClipboard()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Aygea's Market Insight — Shopping List ===");
        sb.AppendLine();

        sb.AppendLine("-- Recipes --");
        foreach (var entry in config.ShoppingListItems)
            sb.AppendLine($"  {entry.RecipeName} x{entry.Quantity}");

        sb.AppendLine();
        sb.AppendLine("-- Materials --");
        foreach (var ing in ingredients)
        {
            var priceInfo = ing.Source == "Vendor"
                ? $"Vendor: {ing.BestPrice:N0} gil"
                : $"MB: {ing.BestPrice:N0} gil";

            var maxInfo = ing.MaxPrice > 0 ? $"max {ing.MaxPrice:N0} gil" : "max —";
            sb.AppendLine($"  [{ing.Quantity}x]  {ing.ItemName}  — {maxInfo}  ({priceInfo})");
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
    public uint BestPrice;
    public uint MaxPrice;
    public string Source = "?";
    public bool IsOverBudget => MaxPrice > 0 && BestPrice > MaxPrice;
}
