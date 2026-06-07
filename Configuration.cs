using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;
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
    public float SalesTaxPercent { get; set; } = 5f;

    public uint ProfitColor { get; set; } = 0xFF00C800; // ABGR green
    public uint LossColor { get; set; } = 0xFF0000C8;   // ABGR red

    public int MbPriceCacheTtlMinutes { get; set; } = 120;
    public int UniversalisCacheTtlMinutes { get; set; } = 1440;

    // Home world override (0 = auto-detect from player)
    public uint HomeWorldId { get; set; } = 0;
    public string HomeWorldName { get; set; } = string.Empty;

    // Tooltip popout
    public bool EnableTooltipPopout { get; set; } = true;
    public int TooltipPopoutModifierKey { get; set; } = 1; // 0=None, 1=Ctrl, 2=Shift, 3=Alt

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
    public float TargetProfitMargin { get; set; } = 0.20f;
    public bool EnableInventoryScanning { get; set; } = false;
    public bool ShowOnlyCraftableWithMaterials { get; set; } = false;

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
        ValidateAndSanitizeConfig(config);
        return config;
    }

    private static void ValidateAndSanitizeConfig(Configuration config)
    {
        // Validate and sanitize general settings
        config.EnableTooltipAugmentation = ValidateBool(config.EnableTooltipAugmentation, nameof(config.EnableTooltipAugmentation));
        config.ShowFetchingPlaceholder = ValidateBool(config.ShowFetchingPlaceholder, nameof(config.ShowFetchingPlaceholder));
        config.ShowCraftCostInTooltips = ValidateBool(config.ShowCraftCostInTooltips, nameof(config.ShowCraftCostInTooltips));
        config.ShowMbPriceInTooltips = ValidateBool(config.ShowMbPriceInTooltips, nameof(config.ShowMbPriceInTooltips));
        config.ColorProfitLossText = ValidateBool(config.ColorProfitLossText, nameof(config.ColorProfitLossText));
        
        // Validate numeric ranges
        config.SalesTaxPercent = ValidateRange(config.SalesTaxPercent, 0f, 100f, nameof(config.SalesTaxPercent));
        config.MbPriceCacheTtlMinutes = ValidateRange(config.MbPriceCacheTtlMinutes, 1, 10080, nameof(config.MbPriceCacheTtlMinutes)); // 1 min to 1 week
        config.UniversalisCacheTtlMinutes = ValidateRange(config.UniversalisCacheTtlMinutes, 1, 43200, nameof(config.UniversalisCacheTtlMinutes)); // 1 min to 30 days
        
        // Validate color values (ensure they're valid ARGB)
        config.ProfitColor = ValidateColor(config.ProfitColor, nameof(config.ProfitColor));
        config.LossColor = ValidateColor(config.LossColor, nameof(config.LossColor));
        
        // Validate tooltip settings
        config.EnableTooltipPopout = ValidateBool(config.EnableTooltipPopout, nameof(config.EnableTooltipPopout));
        config.TooltipPopoutModifierKey = ValidateRange(config.TooltipPopoutModifierKey, 0, 3, nameof(config.TooltipPopoutModifierKey));
        
        // Validate profit scanner settings
        config.RememberScannerWindowPos = ValidateBool(config.RememberScannerWindowPos, nameof(config.RememberScannerWindowPos));
        config.DefaultMinProfitFilter = ValidateRange(config.DefaultMinProfitFilter, -1000000, 1000000, nameof(config.DefaultMinProfitFilter));
        config.DefaultMinIlvlFilter = ValidateRange(config.DefaultMinIlvlFilter, 0, 130, nameof(config.DefaultMinIlvlFilter));
        config.HqOnlyByDefault = ValidateBool(config.HqOnlyByDefault, nameof(config.HqOnlyByDefault));
        config.ShowJobFilterBar = ValidateBool(config.ShowJobFilterBar, nameof(config.ShowJobFilterBar));
        
        // Validate shopping list settings
        config.RememberPinState = ValidateBool(config.RememberPinState, nameof(config.RememberPinState));
        config.PinnedWindowOpacity = ValidateRange(config.PinnedWindowOpacity, 0.1f, 1.0f, nameof(config.PinnedWindowOpacity));
        config.ResolveSubRecipesRecursively = ValidateBool(config.ResolveSubRecipesRecursively, nameof(config.ResolveSubRecipesRecursively));
        config.HighlightOverBudgetIngredients = ValidateBool(config.HighlightOverBudgetIngredients, nameof(config.HighlightOverBudgetIngredients));
        config.TargetProfitMargin = ValidateRange(config.TargetProfitMargin, 0f, 1.0f, nameof(config.TargetProfitMargin));
        config.EnableInventoryScanning = ValidateBool(config.EnableInventoryScanning, nameof(config.EnableInventoryScanning));
        config.ShowOnlyCraftableWithMaterials = ValidateBool(config.ShowOnlyCraftableWithMaterials, nameof(config.ShowOnlyCraftableWithMaterials));
        
        // Ensure shopping list items are valid
        if (config.ShoppingListItems != null)
        {
            // Remove any null entries
            config.ShoppingListItems.RemoveAll(item => item == null);
            
            // Validate each entry
            foreach (var entry in config.ShoppingListItems)
            {
                if (entry != null)
                {
                    entry.Quantity = Math.Max(1, entry.Quantity); // Ensure at least 1
                    entry.RecipeName = entry.RecipeName ?? string.Empty;
                    // Note: RecipeId and ResultItemId validation would require RecipeCache, which we don't have here
                    // These are validated when the entry is actually used
                }
            }
        }
    }

    private static bool ValidateBool(bool value, string fieldName)
    {
        // In a real implementation, we might log if the value was corrupted
        // For bool, any value is technically valid, but we keep the method for consistency
        return value;
    }

    private static T ValidateRange<T>(T value, T min, T max, string fieldName) where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0)
        {
            // Log warning about value being below minimum
            return min;
        }
        if (value.CompareTo(max) > 0)
        {
            // Log warning about value being above maximum
            return max;
        }
        return value;
    }

    private static uint ValidateColor(uint value, string fieldName)
    {
        // Basic validation: ensure it's a valid 32-bit ARGB value
        // More sophisticated validation could check for reasonable color values
        return value;
    }
}

[Serializable]
public class ShoppingListEntry
{
    public uint RecipeId { get; set; }
    public int Quantity { get; set; } = 1;
    public string RecipeName { get; set; } = string.Empty;
    public uint ResultItemId { get; set; }
    public bool SellAsHq { get; set; } = false;
}
