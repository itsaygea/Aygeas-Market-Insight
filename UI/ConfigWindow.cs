using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace AygeaMarketInsight.UI;

public sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly ArtisanIpc artisanIpc;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly IObjectTable objectTable;
    private readonly InventoryScanner? inventoryScanner;

    private List<(uint Id, string Name)>? dcWorlds;

    public ConfigWindow(Configuration config, ArtisanIpc artisanIpc, IDataManager dataManager, IObjectTable objectTable, IPluginLog log, InventoryScanner? inventoryScanner = null)
        : base("Aygea's Market Insight — Settings###AMIConfig")
    {
        this.config = config;
        this.artisanIpc = artisanIpc;
        this.log = log;
        this.dataManager = dataManager;
        this.objectTable = objectTable;
        this.inventoryScanner = inventoryScanner;

        Size = new System.Numerics.Vector2(500, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("AMIConfigTabs"))
            return;

        DrawGeneralTab();
        DrawScannerTab();
        DrawShoppingListTab();
        DrawAboutTab();

        ImGui.EndTabBar();
    }

    private void DrawGeneralTab()
    {
        if (!ImGui.BeginTabItem("General"))
            return;

        ImGui.Text("Tooltip Augmentation");
        ImGui.Separator();

        Checkbox("Enable tooltip augmentation", config.EnableTooltipAugmentation, v => config.EnableTooltipAugmentation = v);
        Checkbox("Show \"Fetching...\" placeholder", config.ShowFetchingPlaceholder, v => config.ShowFetchingPlaceholder = v);
        Checkbox("Show craft cost in tooltips", config.ShowCraftCostInTooltips, v => config.ShowCraftCostInTooltips = v);
        Checkbox("Show MB price in tooltips", config.ShowMbPriceInTooltips, v => config.ShowMbPriceInTooltips = v);
        Checkbox("Color profit/loss text", config.ColorProfitLossText, v => config.ColorProfitLossText = v);

        if (config.ColorProfitLossText)
        {
            ImGui.Indent();
            ColorPicker("Profit color", config.ProfitColor, v => config.ProfitColor = v);
            ColorPicker("Loss color", config.LossColor, v => config.LossColor = v);
            ImGui.Unindent();
        }

        ImGui.Spacing();
        ImGui.Text("Profit Calculation");
        ImGui.Separator();

        var tax = config.SalesTaxPercent;
        if (ImGui.SliderFloat("Sales tax %", ref tax, 0f, 10f, "%.0f%%"))
        {
            config.SalesTaxPercent = Math.Clamp(tax, 0f, 10f);
            config.Save();
        }
        ImGui.TextDisabled("Applied to MB sell price when calculating profit.");

        ImGui.Spacing();
        ImGui.Text("Price Cache");
        ImGui.Separator();

        SliderInt("MB-sourced price TTL (minutes)", config.MbPriceCacheTtlMinutes, v => config.MbPriceCacheTtlMinutes = v, 5, 120);
        SliderInt("Universalis-sourced TTL (minutes)", config.UniversalisCacheTtlMinutes, v => config.UniversalisCacheTtlMinutes = v, 5, 120);

        ImGui.TextDisabled("Universalis batch size: 100 items (fixed)");

        ImGui.Spacing();
        ImGui.Text("Home World");
        ImGui.Separator();

        var detectedWorld = objectTable.LocalPlayer?.HomeWorld;
        var detectedName = detectedWorld?.Value.Name.ToString() ?? "Unknown";
        var detectedId = detectedWorld?.RowId ?? 0;

        if (config.HomeWorldId > 0)
            ImGui.TextDisabled($"Auto-detected: {detectedName} (using override)");
        else
            ImGui.TextDisabled($"Auto-detected: {detectedName}");

        // Build DC world list lazily
        dcWorlds ??= BuildDcWorldList(detectedId);

        if (dcWorlds.Count > 0)
        {
            var currentName = config.HomeWorldId > 0 ? config.HomeWorldName : $"Auto ({detectedName})";
            if (ImGui.BeginCombo("Home World", currentName))
            {
                // Auto-detect option
                if (ImGui.Selectable($"Auto ({detectedName})", config.HomeWorldId == 0))
                {
                    config.HomeWorldId = 0;
                    config.HomeWorldName = string.Empty;
                    config.Save();
                }

                foreach (var (id, name) in dcWorlds)
                {
                    if (ImGui.Selectable(name, config.HomeWorldId == id))
                    {
                        config.HomeWorldId = id;
                        config.HomeWorldName = name;
                        config.Save();
                    }
                }

                ImGui.EndCombo();
            }
        }

        ImGui.TextDisabled("Sets your world for accurate buy-side pricing.");

        ImGui.Spacing();
        ImGui.Text("Inventory Scanning");
        ImGui.Separator();

        Checkbox("Enable inventory scanning", config.EnableInventoryScanning, v =>
        {
            config.EnableInventoryScanning = v;
            inventoryScanner?.SetEnabled(v);
        });
        ImGui.TextDisabled("Shows how many materials you already own in the shopping list and profit scanner.");
        ImGui.TextDisabled("Scans your bags, saddlebag, and open retainers.");

        ImGui.Spacing();
        ImGui.Text("Item Detail Popout");
        ImGui.Separator();

        Checkbox("Enable item detail popout on hover", config.EnableTooltipPopout, v => config.EnableTooltipPopout = v);

        var keyNames = new[] { "None (hover only)", "Ctrl", "Shift", "Alt" };
        var keyIdx = Math.Clamp(config.TooltipPopoutModifierKey, 0, 3);
        if (ImGui.Combo("Popout trigger key", ref keyIdx, keyNames, keyNames.Length))
        {
            config.TooltipPopoutModifierKey = keyIdx;
            config.Save();
        }

        var keyHint = keyIdx switch
        {
            0 => "Hover a craftable item to pin details.",
            _ => $"Hold {keyNames[keyIdx]} + hover a craftable item to pin details.",
        };
        ImGui.TextDisabled(keyHint);

        ImGui.EndTabItem();
    }

    private void DrawScannerTab()
    {
        if (!ImGui.BeginTabItem("Profit Scanner"))
            return;

        ImGui.Text("Scanner Defaults");
        ImGui.Separator();

        Checkbox("Remember window position/size", config.RememberScannerWindowPos, v => config.RememberScannerWindowPos = v);
        SliderInt("Default min profit filter", config.DefaultMinProfitFilter, v => config.DefaultMinProfitFilter = v, 0, 1_000_000);
        SliderInt("Default min iLvl filter", config.DefaultMinIlvlFilter, v => config.DefaultMinIlvlFilter = v, 0, 700);
        Checkbox("HQ only by default", config.HqOnlyByDefault, v => config.HqOnlyByDefault = v);
        Checkbox("Show job filter bar", config.ShowJobFilterBar, v => config.ShowJobFilterBar = v);

        ImGui.EndTabItem();
    }

    private void DrawShoppingListTab()
    {
        if (!ImGui.BeginTabItem("Shopping List"))
            return;

        ImGui.Text("Shopping List Settings");
        ImGui.Separator();

        Checkbox("Remember pin state between sessions", config.RememberPinState, v => config.RememberPinState = v);

        var opacity = config.PinnedWindowOpacity * 100f;
        if (ImGui.SliderFloat("Pinned window opacity", ref opacity, 20f, 100f, "%.0f%%"))
            config.PinnedWindowOpacity = Math.Clamp(opacity / 100f, 0.2f, 1f);

        Checkbox("Resolve sub-recipes recursively", config.ResolveSubRecipesRecursively, v => config.ResolveSubRecipesRecursively = v);
        Checkbox("Highlight over-budget ingredients in red", config.HighlightOverBudgetIngredients, v => config.HighlightOverBudgetIngredients = v);

        var margin = config.TargetProfitMargin * 100f;
        if (ImGui.SliderFloat("Target profit margin %", ref margin, 0f, 80f, "%.0f%%"))
        {
            config.TargetProfitMargin = Math.Clamp(margin / 100f, 0f, 0.80f);
            config.Save();
        }
        ImGui.TextDisabled("Used to calculate max price per ingredient in the shopping list.");

        ImGui.EndTabItem();
    }

    private void DrawAboutTab()
    {
        if (!ImGui.BeginTabItem("About"))
            return;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        ImGui.Spacing();
        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.2f, 1f),
            $"Aygea's Market Insight  v{version}");
        ImGui.Separator();
        ImGui.Text("A crafting profit and market price tool");
        ImGui.Text("for Final Fantasy XIV.");
        ImGui.Spacing();
        ImGui.Text("Made with crazyayL by Aygea");
        ImGui.Spacing();

        // Twitch button — purple #9146FF
        DrawLinkButton("Watch on Twitch", "https://twitch.tv/crazyaygea",
            0xFFFF4691);

        ImGui.SameLine();

        // Website button — baby blue #89CFF0
        DrawLinkButton("itsaygea.com", "https://itsaygea.com",
            0xFFF0CF89);

        ImGui.Spacing();

        // Ko-fi button — coral #FF5E5B
        DrawLinkButton("Support on Ko-fi", "https://ko-fi.com/aygea",
            0xFF5B5EFF);

        ImGui.SameLine();

        // GitHub button — dark neutral #333333
        DrawLinkButton("GitHub / Report a Bug",
            "https://github.com/itsaygea/Aygeas-Market-Insight",
            0xFF333333);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("Thanks to Universalis.app for price data");
        ImGui.TextDisabled("and the Dalamud plugin community.");

        ImGui.EndTabItem();
    }

    private void Checkbox(string label, bool current, Action<bool> set)
    {
        var val = current;
        if (ImGui.Checkbox(label, ref val))
        {
            set(val);
            config.Save();
        }
    }

    private void SliderInt(string label, int current, Action<int> set, int min, int max)
    {
        var val = current;
        if (ImGui.SliderInt(label, ref val, min, max))
        {
            set(val);
            config.Save();
        }
    }

    private void ColorPicker(string label, uint current, Action<uint> set)
    {
        var vec = ImGui.ColorConvertU32ToFloat4(current);
        if (ImGui.ColorEdit4(label, ref vec, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
        {
            set(ImGui.ColorConvertFloat4ToU32(vec));
            config.Save();
        }
    }

    private static void DrawLinkButton(string label, string url, uint color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, ImGui.ColorConvertU32ToFloat4(color));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.ColorConvertU32ToFloat4(Lighten(color)));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGui.ColorConvertU32ToFloat4(color));

        if (ImGui.Button(label))
            Dalamud.Utility.Util.OpenLink(url);

        ImGui.PopStyleColor(3);
    }

    private static uint Lighten(uint abgr)
    {
        var v = ImGui.ColorConvertU32ToFloat4(abgr);
        return ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(
            Math.Min(v.X + 0.15f, 1f),
            Math.Min(v.Y + 0.15f, 1f),
            Math.Min(v.Z + 0.15f, 1f),
            v.W));
    }

    private List<(uint Id, string Name)> BuildDcWorldList(uint currentWorldId)
    {
        var worlds = dataManager.GetExcelSheet<World>();
        if (worlds == null) return [];

        // Find the player's DC from their current world
        uint dcId = 0;
        foreach (var w in worlds)
        {
            if (w.RowId == currentWorldId)
            {
                dcId = w.DataCenter.RowId;
                break;
            }
        }

        if (dcId == 0) return [];

        var result = new List<(uint Id, string Name)>();
        foreach (var w in worlds)
        {
            if (w.DataCenter.RowId == dcId && !w.IsPublic)
                continue;
            if (w.DataCenter.RowId != dcId) continue;

            var name = w.Name.ToString();
            if (!string.IsNullOrEmpty(name))
                result.Add((w.RowId, name));
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return result;
    }
}
