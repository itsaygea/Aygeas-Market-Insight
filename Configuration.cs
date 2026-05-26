using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Newtonsoft.Json;

namespace AygeaMarketInsight;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Tab 1 — General
    public bool EnableTooltipAugmentation { get; set; } = true;
    public bool ShowFetchingPlaceholder { get; set; } = true;
    public bool ShowCraftCostInTooltips { get; set; } = true;
    public bool ShowMbPriceInTooltips { get; set; } = true;
    public bool ColorProfitLossText { get; set; } = true;

    public uint ProfitColor { get; set; } = 0xFF00C800; // ABGR green
    public uint LossColor { get; set; } = 0xFF0000C8;   // ABGR red

    public int MbPriceCacheTtlMinutes { get; set; } = 30;
    public int UniversalisCacheTtlMinutes { get; set; } = 20;

    // Tab 2 — Profit Scanner
    public bool RememberScannerWindowPos { get; set; } = true;
    public int DefaultMinProfitFilter { get; set; } = 0;
    public int DefaultMinIlvlFilter { get; set; } = 0;
    public bool HqOnlyByDefault { get; set; } = false;
    public bool ShowJobFilterBar { get; set; } = true;

    // Tab 3 — Shopping List
    public bool RememberPinState { get; set; } = false;
    public float PinnedWindowOpacity { get; set; } = 0.85f;
    public bool ResolveSubRecipesRecursively { get; set; } = true;
    public bool HighlightOverBudgetIngredients { get; set; } = true;

    // Persisted shopping list
    public List<ShoppingListEntry> ShoppingListItems { get; set; } = [];

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }

    public static Configuration Load(IDalamudPluginInterface pluginInterface)
    {
        var config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.pluginInterface = pluginInterface;
        return config;
    }
}

[Serializable]
public class ShoppingListEntry
{
    public uint RecipeId { get; set; }
    public int Quantity { get; set; } = 1;
    public string RecipeName { get; set; } = string.Empty;
    public uint ResultItemId { get; set; }
}
