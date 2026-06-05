using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Interface.ImGuiNotification;
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
    private readonly INotificationManager notificationManager;
    private readonly IPluginLog log;
    private readonly System.Action<HashSet<uint>, System.Action<string>?, System.Action?> refreshAll;

    private bool showConfirmClear;
    private bool isLoading;
    private string loadingStatus = string.Empty;

    public ShoppingListWindow(
        Configuration config,
        RecipeCache recipeCache,
        PriceCache priceCache,
        UniversalisClient universalisClient,
        ArtisanIpc artisanIpc,
        INotificationManager notificationManager,
        IFramework framework,
        IPluginLog log,
        System.Action<HashSet<uint>, System.Action<string>?, System.Action?> refreshAll)
        : base("Aygea's Market Insight — Shopping List###AMIShoppingList")
    {
        this.config = config;
        this.recipeCache = recipeCache;
        this.priceCache = priceCache;
        this.artisanIpc = artisanIpc;
        this.notificationManager = notificationManager;
        this.log = log;
        this.refreshAll = refreshAll;

        Size = new System.Numerics.Vector2(600, 550);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        // Auto-fetch prices if shopping list has items with missing prices
        if (config.ShoppingListItems.Count > 0 && !isLoading)
        {
            var needsFetch = false;
            foreach (var entry in config.ShoppingListItems)
            {
                if (priceCache.Get(entry.ResultItemId) == null)
                {
                    needsFetch = true;
                    break;
                }
            }

            if (needsFetch)
                RefreshPrices();
        }
    }

    private void RefreshPrices()
    {
        if (isLoading) return;

        isLoading = true;
        loadingStatus = "collecting items...";

        // Collect only shopping list items + their ingredients
        var ids = new HashSet<uint>();
        foreach (var entry in config.ShoppingListItems)
        {
            ids.Add(entry.ResultItemId);
            var recipe = recipeCache.GetRecipe(entry.RecipeId);
            if (recipe != null)
            {
                var r = recipe.Value;
                for (int i = 0; i < 8; i++)
                {
                    var amount = (int)r.AmountIngredient[i];
                    var itemId = r.Ingredient[i].RowId;
                    if (amount > 0 && itemId != 0)
                        ids.Add(itemId);
                }
            }
        }

        if (ids.Count == 0)
        {
            isLoading = false;
            loadingStatus = string.Empty;
            return;
        }

        refreshAll(ids,
            status => loadingStatus = status ?? string.Empty,
            () =>
            {
                isLoading = false;
                loadingStatus = string.Empty;
            });
    }

    public override void Draw()
    {
        if (config.ShoppingListItems.Count == 0)
        {
            ImGui.TextDisabled("No items in shopping list.");
            ImGui.TextDisabled("Right-click a recipe in the Profit Scanner to add items.");
            return;
        }

        if (isLoading)
        {
            ImGui.TextDisabled($"Refreshing prices... {loadingStatus}");
            ImGui.SameLine();
        }

        if (ImGui.SmallButton("Refresh Prices##SL"))
            RefreshPrices();

        DrawMarginControl();
        ImGui.Separator();

        // Draw each recipe as a collapsible section
        for (int i = config.ShoppingListItems.Count - 1; i >= 0; i--)
            DrawRecipeSection(i);

        DrawFooter();
    }

    private void DrawMarginControl()
    {
        var margin = config.TargetProfitMargin * 100f;
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderFloat("Target Profit Margin", ref margin, 0f, 80f, "%.0f%%"))
        {
            config.TargetProfitMargin = Math.Clamp(margin / 100f, 0f, 0.80f);
            config.Save();
        }
        ImGui.TextDisabled("Sets the max price per ingredient so you keep this % profit.");
    }

    private void DrawRecipeSection(int index)
    {
        var entry = config.ShoppingListItems[index];
        var recipe = recipeCache.GetRecipe(entry.RecipeId);
        if (recipe == null) return;

        var r = recipe.Value;

        // Calculate prices for this recipe
        var priceData = priceCache.Get(entry.ResultItemId);
        var nqPrice = priceData?.NqPrice ?? 0;
        var hqPrice = priceData?.HqPrice ?? 0;

        // Auto-toggle quality if only one is available (no save — user action saves later)
        if (nqPrice == 0 && hqPrice > 0 && !entry.SellAsHq)
            entry.SellAsHq = true;
        else if (hqPrice == 0 && nqPrice > 0 && entry.SellAsHq)
            entry.SellAsHq = false;

        var mbPrice = entry.SellAsHq ? hqPrice : nqPrice;
        var afterTax = (uint)(mbPrice * (1f - config.SalesTaxPercent / 100f));
        var budget = (uint)(afterTax * (1f - config.TargetProfitMargin));

        // Calculate craft cost (memoized in RecipeCache)
        var craftCost = recipeCache.CalculateCraftCost(r, priceCache, out var breakdown);
        var totalMaterialCost = breakdown.Sum(b => (long)b.CostPerUnit * b.Quantity);
        var profit = (int)(afterTax - craftCost);

        // Header line: recipe name + quantity controls + profit + remove
        var headerLabel = $"  {entry.RecipeName}###recipe_{entry.RecipeId}";

        if (ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();

            // Quantity and profit row
            if (ImGui.SmallButton($"-##qty_{entry.RecipeId}"))
            {
                if (entry.Quantity > 1) entry.Quantity--;
                else { config.ShoppingListItems.RemoveAt(index); config.Save(); return; }
                config.Save();
            }
            ImGui.SameLine();
            ImGui.Text($"Craft: {entry.Quantity}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"+##qty_{entry.RecipeId}"))
            {
                entry.Quantity++;
                config.Save();
            }

            ImGui.SameLine();
            var sellHq = entry.SellAsHq;
            if (ImGui.Checkbox($"Sell HQ##hq_{entry.RecipeId}", ref sellHq))
            {
                entry.SellAsHq = sellHq;
                config.Save();
            }

            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();

            // Price summary
            if (mbPrice > 0)
            {
                var priceLabel = entry.SellAsHq ? "Sell (HQ): " : "Sell (NQ): ";
                ImGui.Text($"{priceLabel}{afterTax:N0}");
                ImGui.SameLine();
                var profitColor = profit >= 0
                    ? ImGui.ColorConvertU32ToFloat4(config.ProfitColor)
                    : ImGui.ColorConvertU32ToFloat4(config.LossColor);
                ImGui.TextColored(profitColor, profit >= 0 ? $"Profit: +{profit:N0}" : $"Profit: {profit:N0}");
            }
            else
            {
                ImGui.TextDisabled("Sell: —  Profit: —");
            }

            // Remove button
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.6f, 0.2f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.8f, 0.3f, 0.3f, 1f));
            if (ImGui.SmallButton($"Remove##{entry.RecipeId}"))
            {
                config.ShoppingListItems.RemoveAt(index);
                config.Save();
                ImGui.PopStyleColor(2);
                ImGui.Unindent();
                return;
            }
            ImGui.PopStyleColor(2);

            ImGui.Spacing();

            // Ingredients table
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable;

            if (ImGui.BeginTable($"Ingredients_{entry.RecipeId}", 5, flags))
            {
                ImGui.TableSetupColumn("Material", ImGuiTableColumnFlags.None, 220);
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.None, 45);
                ImGui.TableSetupColumn("Best Price", ImGuiTableColumnFlags.None, 90);
                ImGui.TableSetupColumn("Max Price", ImGuiTableColumnFlags.None, 90);
                ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.None, 60);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var ing in breakdown)
                {
                    var qty = ing.Quantity * entry.Quantity;
                    var isVendorItem = ing.Source == "Vendor";
                    var bestPrice = ing.CostPerUnit;

                    // Max price only applies to MB-sourced items — O(1) using pre-computed total
                    uint maxPrice = 0;
                    if (!isVendorItem && ing.Source != "Craft" && budget > 0 && qty > 0)
                    {
                        var otherCosts = (uint)Math.Max(0, totalMaterialCost * entry.Quantity - (long)ing.CostPerUnit * ing.Quantity * entry.Quantity);
                        var remaining = budget > otherCosts ? budget - otherCosts : 0;
                        maxPrice = remaining / (uint)qty;
                    }

                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    var overBudget = maxPrice > 0 && bestPrice > maxPrice;
                    var matName = recipeCache.GetItemName(ing.ItemId);

                    if (ing.Source == "Craft" && ing.SubCraftBreakdown is { Count: > 0 })
                    {
                        if (overBudget && config.HighlightOverBudgetIngredients)
                            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f), matName);
                        else
                            ImGui.Text(matName);
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"+##sub_{ing.ItemId}_{entry.RecipeId}"))
                        { /* toggle handled by TreeNode below */ }
                    }
                    else
                    {
                        if (overBudget && config.HighlightOverBudgetIngredients)
                            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f), matName);
                        else
                            ImGui.Text(matName);
                    }

                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        ImGui.SetClipboardText(matName);
                        notificationManager.AddNotification(new Notification
                        {
                            Content = matName,
                            Title = "Copied to clipboard",
                            Type = NotificationType.Success,
                        });
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Double-click to copy name");

                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text($"{qty}");

                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text(bestPrice > 0 ? $"{bestPrice:N0}" : "—");

                    ImGui.TableSetColumnIndex(3);
                    if (isVendorItem || ing.Source == "Craft")
                        ImGui.TextDisabled(ing.Source);
                    else if (maxPrice > 0)
                    {
                        if (overBudget)
                            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.3f, 0.3f, 1f), $"{maxPrice:N0}");
                        else
                            ImGui.Text($"{maxPrice:N0}");
                    }
                    else
                        ImGui.TextDisabled("—");

                    ImGui.TableSetColumnIndex(4);
                    ImGui.Text(ing.Source);

                    // Sub-craft breakdown rows
                    if (ing.Source == "Craft" && ing.SubCraftBreakdown is { Count: > 0 })
                    {
                        foreach (var sub in ing.SubCraftBreakdown)
                        {
                            ImGui.TableNextRow();

                            ImGui.TableSetColumnIndex(0);
                            ImGui.TextDisabled($"  └ {recipeCache.GetItemName(sub.ItemId)}");

                            ImGui.TableSetColumnIndex(1);
                            ImGui.TextDisabled($"{sub.Quantity * entry.Quantity}");

                            ImGui.TableSetColumnIndex(2);
                            ImGui.TextDisabled(sub.CostPerUnit > 0 ? $"{sub.CostPerUnit:N0}" : "—");

                            ImGui.TableSetColumnIndex(3);
                            ImGui.TextDisabled("—");

                            ImGui.TableSetColumnIndex(4);
                            ImGui.TextDisabled(sub.Source);
                        }
                    }
                }

                ImGui.EndTable();
            }

            ImGui.Unindent();
        }
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
    }

    private void CopyListToClipboard()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Aygea's Market Insight — Shopping List ===");
        sb.AppendLine($"Target margin: {config.TargetProfitMargin:P0}");
        sb.AppendLine();

        foreach (var entry in config.ShoppingListItems)
        {
            var recipe = recipeCache.GetRecipe(entry.RecipeId);
            if (recipe == null) continue;

            sb.AppendLine($"-- {entry.RecipeName} x{entry.Quantity} --");

            var r = recipe.Value;
            for (int i = 0; i < 8; i++)
            {
                var amount = (int)r.AmountIngredient[i];
                var itemId = r.Ingredient[i].RowId;
                if (amount <= 0 || itemId == 0) continue;

                var qty = amount * entry.Quantity;
                var vendorPrice = recipeCache.GetVendorPrice(itemId);
                var cached = priceCache.Get(itemId);
                var mbPrice = cached?.NqPrice ?? 0;

                var bestPrice = vendorPrice > 0 && (mbPrice == 0 || vendorPrice <= mbPrice) ? vendorPrice : mbPrice;
                var source = vendorPrice > 0 && (mbPrice == 0 || vendorPrice <= mbPrice) ? "Vendor" : "MB";

                sb.AppendLine($"  [{qty}x]  {recipeCache.GetItemName(itemId)}  — {bestPrice:N0} gil ({source})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("==============================================");
        ImGui.SetClipboardText(sb.ToString());
    }
}
