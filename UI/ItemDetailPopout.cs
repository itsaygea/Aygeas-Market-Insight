using System;
using System.Collections.Generic;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace AygeaMarketInsight.UI;

public sealed class ItemDetailPopout : Window
{
    private readonly RecipeCache recipeCache;
    private readonly PriceCache priceCache;
    private readonly Configuration config;

    private PinnedItemData? pinned;

    public System.Action? OnAddToShoppingList { get; set; }

    public ItemDetailPopout(RecipeCache recipeCache, PriceCache priceCache, Configuration config)
        : base("Aygea's Market Insight — Item Details###AMIItemDetail")
    {
        this.recipeCache = recipeCache;
        this.priceCache = priceCache;
        this.config = config;

        Size = new System.Numerics.Vector2(450, 400);
        SizeCondition = ImGuiCond.FirstUseEver;

        Flags = ImGuiWindowFlags.NoCollapse;
    }

    public override void OnOpen()
    {
        // Anchor to bottom-right corner
        var viewport = ImGui.GetMainViewport();
        var pos = new System.Numerics.Vector2(
            viewport.Pos.X + viewport.Size.X - Size.Value.X - 20,
            viewport.Pos.Y + viewport.Size.Y - Size.Value.Y - 20);
        ImGui.SetNextWindowPos(pos, ImGuiCond.FirstUseEver);
    }

    public void SetPinnedData(PinnedItemData data) => pinned = data;

    public override void Draw()
    {
        if (pinned == null)
        {
            ImGui.TextDisabled("Hover a craftable item and hold Ctrl to pin details here.");
            return;
        }

        // Look up live prices from cache
        var cached = priceCache.Get(pinned.ItemId);
        uint mbPrice = pinned.IsHq ? cached?.HqPrice ?? 0 : cached?.NqPrice ?? 0;
        uint mbAfterTax = (uint)(mbPrice * (1f - config.SalesTaxPercent / 100f));

        // Recalculate craft cost with live prices
        var recipe = recipeCache.GetRecipe(pinned.RecipeId);
        uint craftCost = 0;
        List<RecipeCache.IngredientCost> breakdown = [];
        if (recipe != null)
            craftCost = recipeCache.CalculateCraftCost(recipe.Value, priceCache, out breakdown);

        int profit = (int)(mbAfterTax - craftCost);

        // Item name + craft level
        ImGui.Text(pinned.ItemName);
        if (pinned.IsHq)
        {
            ImGui.SameLine();
            ImGui.TextColored(new System.Numerics.Vector4(0.2f, 0.8f, 1f, 1f), "(HQ)");
        }

        var (level, craftType, isExpert) = recipeCache.GetRecipeDifficulty(pinned.RecipeId);
        ImGui.TextDisabled($"Craft: {craftType} Lv. {level}" + (isExpert ? " (Expert)" : ""));

        ImGui.Separator();

        // Price summary
        var tax = config.SalesTaxPercent;
        if (ImGui.BeginTable("PriceSummary", 2, ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Text("Craft cost:");
            ImGui.TableSetColumnIndex(1);
            ImGui.Text(craftCost > 0 ? $"{craftCost:N0} gil" : "—");

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Text("MB sell price:");
            ImGui.TableSetColumnIndex(1);
            ImGui.Text(mbPrice > 0 ? $"{mbPrice:N0} gil" : "—");

            if (tax > 0)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled($"After tax ({tax:F0}%):");
                ImGui.TableSetColumnIndex(1);
                ImGui.TextDisabled(mbAfterTax > 0 ? $"{mbAfterTax:N0} gil" : "—");
            }

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Text("Profit:");
            ImGui.TableSetColumnIndex(1);
            if (mbPrice > 0 && craftCost > 0)
            {
                var profitColor = profit >= 0
                    ? ImGui.ColorConvertU32ToFloat4(config.ProfitColor)
                    : ImGui.ColorConvertU32ToFloat4(config.LossColor);
                var profitText = profit >= 0 ? $"+{profit:N0} gil" : $"{profit:N0} gil";
                ImGui.TextColored(profitColor, profitText);
            }
            else
            {
                ImGui.TextDisabled("—");
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();

        // Ingredient breakdown — use live prices
        if (breakdown.Count > 0)
        {
            ImGui.Text("Materials");
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                        ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;

            if (ImGui.BeginTable("MaterialTable", 5, flags,
                ImGui.GetContentRegionAvail() with { Y = ImGui.GetContentRegionAvail().Y - 40 }))
            {
                ImGui.TableSetupColumn("Material", ImGuiTableColumnFlags.None, 180);
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.None, 40);
                ImGui.TableSetupColumn("Each", ImGuiTableColumnFlags.None, 70);
                ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.None, 80);
                ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.None, 55);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var ing in breakdown)
                {
                    ImGui.TableNextRow();

                    var vendorPrice = recipeCache.GetVendorPrice(ing.ItemId);
                    var source = vendorPrice > 0 && vendorPrice <= ing.CostPerUnit ? "Vendor" : "MB";
                    var total = ing.CostPerUnit * (uint)ing.Quantity;

                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(recipeCache.GetItemName(ing.ItemId));
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text($"{ing.Quantity}");
                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text(ing.CostPerUnit > 0 ? $"{ing.CostPerUnit:N0}" : "—");
                    ImGui.TableSetColumnIndex(3);
                    ImGui.Text(total > 0 ? $"{total:N0}" : "—");
                    ImGui.TableSetColumnIndex(4);
                    ImGui.Text(source);
                }

                ImGui.EndTable();
            }
        }

        // Bottom actions
        ImGui.Spacing();
        if (ImGui.Button("Add to Shopping List"))
        {
            config.ShoppingListItems.Add(new ShoppingListEntry
            {
                RecipeId = pinned.RecipeId,
                Quantity = 1,
                RecipeName = pinned.ItemName,
                ResultItemId = pinned.ItemId,
            });
            config.Save();
            OnAddToShoppingList?.Invoke();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Unpin"))
            pinned = null;
    }
}
