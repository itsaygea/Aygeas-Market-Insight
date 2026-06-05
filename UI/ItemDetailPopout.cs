using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace AygeaMarketInsight.UI;

public sealed class ItemDetailPopout : Window
{
    private readonly RecipeCache recipeCache;
    private readonly PriceCache priceCache;
    private readonly UniversalisClient universalisClient;
    private readonly Configuration config;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    private PinnedItemData? pinned;
    private bool showHq;

    public System.Action? OnAddToShoppingList { get; set; }

    public ItemDetailPopout(
        RecipeCache recipeCache,
        PriceCache priceCache,
        UniversalisClient universalisClient,
        Configuration config,
        IObjectTable objectTable,
        IFramework framework,
        IPluginLog log)
        : base("Aygea's Market Insight — Item Details###AMIItemDetail")
    {
        this.recipeCache = recipeCache;
        this.priceCache = priceCache;
        this.universalisClient = universalisClient;
        this.config = config;
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;

        Size = new System.Numerics.Vector2(450, 400);
        SizeCondition = ImGuiCond.FirstUseEver;

        Flags = ImGuiWindowFlags.NoCollapse;
    }

    public override void OnOpen()
    {
        // Anchor to bottom-right corner
        var viewport = ImGui.GetMainViewport();
        var pos = new System.Numerics.Vector2(
            viewport.Pos.X + viewport.Size.X - (Size?.X ?? 450) - 20,
            viewport.Pos.Y + viewport.Size.Y - (Size?.Y ?? 400) - 20);
        ImGui.SetNextWindowPos(pos, ImGuiCond.FirstUseEver);
    }

    public void SetPinnedData(PinnedItemData data)
    {
        pinned = data;
        showHq = data.IsHq;
        FetchMissingPrices();
    }

    private void FetchMissingPrices()
    {
        if (pinned == null) return;

        var ids = new HashSet<uint> { pinned.ItemId };
        if (recipeCache.GetRecipe(pinned.RecipeId) is { } recipe)
        {
            for (int i = 0; i < 8; i++)
            {
                var amount = (int)recipe.AmountIngredient[i];
                var ingId = recipe.Ingredient[i].RowId;
                if (amount > 0 && ingId != 0)
                    ids.Add(ingId);
            }
        }

        var toFetch = ids
            .Where(id => priceCache.Get(id) == null && !priceCache.IsPending(id))
            .ToList();

        if (toFetch.Count == 0) return;

        foreach (var id in toFetch)
            priceCache.MarkPending(id);

        var worldId = config.HomeWorldId > 0 ? config.HomeWorldId : (objectTable.LocalPlayer?.HomeWorld.RowId ?? 0);
        if (worldId == 0) return;
        var ttl = config.UniversalisCacheTtlMinutes;

#pragma warning disable CS4014
        _ = Task.Run(async () =>
        {
            try
            {
                var results = await universalisClient.FetchPrices(worldId, toFetch, ttl);
                foreach (var kvp in results)
                {
                    var p = kvp.Value;
                    priceCache.Set(kvp.Key, p.NqPrice, p.HqPrice, p.Source,
                        TimeSpan.FromMinutes(ttl));
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "ItemDetailPopout price fetch failed");
                foreach (var id in toFetch)
                    priceCache.Set(id, 0, 0, "failed", TimeSpan.FromMinutes(1));
            }
        });
#pragma warning restore CS4014
    }

    public override void Draw()
    {
        if (pinned == null)
        {
            var hint = config.TooltipPopoutModifierKey switch
            {
                0 => "Hover a craftable item to pin details here.",
                2 => "Hover a craftable item + hold Shift to pin details here.",
                3 => "Hover a craftable item + hold Alt to pin details here.",
                _ => "Hover a craftable item + hold Ctrl to pin details here.",
            };
            ImGui.TextDisabled(hint);
            return;
        }

        // Look up live prices from cache, fall back to pinned snapshot
        var cached = priceCache.Get(pinned.ItemId);
        uint mbPrice;
        if (cached != null)
            mbPrice = showHq ? cached.HqPrice : cached.NqPrice;
        else
            mbPrice = showHq ? (pinned.HqSnapshot > 0 ? pinned.HqSnapshot : pinned.MbPriceRaw) : pinned.MbPriceRaw;
        uint mbAfterTax = (uint)(mbPrice * (1f - config.SalesTaxPercent / 100f));

        // Best sell price across DC
        uint bestSellPrice = cached?.MaxDcPrice ?? 0;
        string bestSellWorld = cached?.MaxDcPriceWorld ?? "";
        uint bestSellAfterTax = bestSellPrice > 0 ? (uint)(bestSellPrice * (1f - config.SalesTaxPercent / 100f)) : 0;

        var recipe = recipeCache.GetRecipe(pinned.RecipeId);
        uint craftCost = 0;
        List<RecipeCache.IngredientCost> breakdown = [];
        if (recipe != null)
            craftCost = recipeCache.CalculateCraftCost(recipe.Value, priceCache, out breakdown);

        uint effectiveSellPrice = bestSellAfterTax > mbAfterTax ? bestSellAfterTax : mbAfterTax;
        int profit = (int)(effectiveSellPrice - craftCost);

        // Item name + craft level
        ImGui.Text(pinned.ItemName);

        // HQ/NQ toggle
        ImGui.SameLine();
        if (ImGui.Checkbox("Show HQ", ref showHq))
        {
            // Refresh prices for the new quality if cache is stale
            if (cached == null)
                FetchMissingPrices();
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

            // Best sell across DC
            if (bestSellPrice > 0 && bestSellWorld.Length > 0)
            {
                var premium = bestSellAfterTax > mbAfterTax && mbAfterTax > 0;

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                if (premium)
                    ImGui.TextColored(new System.Numerics.Vector4(0.2f, 0.8f, 1f, 1f), "Best sell:");
                else
                    ImGui.Text("Best sell:");
                ImGui.TableSetColumnIndex(1);
                if (premium)
                {
                    var pct = mbAfterTax > 0 ? (bestSellAfterTax - mbAfterTax) * 100f / mbAfterTax : 0;
                    ImGui.TextColored(new System.Numerics.Vector4(0.2f, 0.8f, 1f, 1f),
                        $"{bestSellPrice:N0} on {bestSellWorld} (+{pct:F0}%)");
                }
                else
                    ImGui.Text($"{bestSellPrice:N0} on {bestSellWorld}");
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

            var tableHeight = Math.Max(ImGui.GetContentRegionAvail().Y - 40, 50f);
            if (ImGui.BeginTable("MaterialTable", 5, flags,
                ImGui.GetContentRegionAvail() with { Y = tableHeight }))
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
                            ImGui.TextDisabled($"{sub.Quantity}");
                            ImGui.TableSetColumnIndex(2);
                            ImGui.TextDisabled(sub.CostPerUnit > 0 ? $"{sub.CostPerUnit:N0}" : "—");
                            ImGui.TableSetColumnIndex(3);
                            ImGui.TextDisabled(sub.CostPerUnit > 0 ? $"{sub.CostPerUnit * (uint)sub.Quantity:N0}" : "—");
                            ImGui.TableSetColumnIndex(4);
                            ImGui.TextDisabled(sub.Source);
                        }
                    }
                }

                ImGui.EndTable();
            }
        }

        // Bottom actions
        ImGui.Spacing();
        if (ImGui.Button("Add to Shopping List"))
        {
            AddOrIncrement(pinned.RecipeId, pinned.ItemName, pinned.ItemId);
            config.Save();
            OnAddToShoppingList?.Invoke();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Unpin"))
            pinned = null;
    }

    private void AddOrIncrement(uint recipeId, string recipeName, uint resultItemId)
    {
        var existing = config.ShoppingListItems.FirstOrDefault(e => e.RecipeId == recipeId);
        if (existing != null)
            existing.Quantity++;
        else
        {
            config.ShoppingListItems.Add(new ShoppingListEntry
            {
                RecipeId = recipeId,
                Quantity = 1,
                RecipeName = recipeName,
                ResultItemId = resultItemId,
            });
        }
    }
}
