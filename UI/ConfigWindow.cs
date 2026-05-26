using System;
using System.Reflection;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace AygeaMarketInsight.UI;

public sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly ArtisanIpc artisanIpc;
    private readonly IPluginLog log;

    public ConfigWindow(Configuration config, ArtisanIpc artisanIpc, IPluginLog log)
        : base("Aygea's Market Insight — Settings###AMIConfig")
    {
        this.config = config;
        this.artisanIpc = artisanIpc;
        this.log = log;

        Size = new System.Numerics.Vector2(500, 450);
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

        Checkbox("Enable tooltip augmentation", ref config.EnableTooltipAugmentation);
        Checkbox("Show \"Fetching...\" placeholder", ref config.ShowFetchingPlaceholder);
        Checkbox("Show craft cost in tooltips", ref config.ShowCraftCostInTooltips);
        Checkbox("Show MB price in tooltips", ref config.ShowMbPriceInTooltips);
        Checkbox("Color profit/loss text", ref config.ColorProfitLossText);

        if (config.ColorProfitLossText)
        {
            ImGui.Indent();
            ColorPicker("Profit color", ref config.ProfitColor);
            ColorPicker("Loss color", ref config.LossColor);
            ImGui.Unindent();
        }

        ImGui.Spacing();
        ImGui.Text("Price Cache");
        ImGui.Separator();

        SliderInt("MB-sourced price TTL (minutes)", ref config.MbPriceCacheTtlMinutes, 5, 120);
        SliderInt("Universalis-sourced TTL (minutes)", ref config.UniversalisCacheTtlMinutes, 5, 120);

        ImGui.TextDisabled("Universalis batch size: 100 items (fixed)");

        ImGui.EndTabItem();
    }

    private void DrawScannerTab()
    {
        if (!ImGui.BeginTabItem("Profit Scanner"))
            return;

        ImGui.Text("Scanner Defaults");
        ImGui.Separator();

        Checkbox("Remember window position/size", ref config.RememberScannerWindowPos);
        SliderInt("Default min profit filter", ref config.DefaultMinProfitFilter, 0, 1_000_000);
        SliderInt("Default min iLvl filter", ref config.DefaultMinIlvlFilter, 0, 700);
        Checkbox("HQ only by default", ref config.HqOnlyByDefault);
        Checkbox("Show job filter bar", ref config.ShowJobFilterBar);

        ImGui.EndTabItem();
    }

    private void DrawShoppingListTab()
    {
        if (!ImGui.BeginTabItem("Shopping List"))
            return;

        ImGui.Text("Shopping List Settings");
        ImGui.Separator();

        Checkbox("Remember pin state between sessions", ref config.RememberPinState);

        var opacity = config.PinnedWindowOpacity * 100f;
        if (ImGui.SliderFloat("Pinned window opacity", ref opacity, 20f, 100f, "%.0f%%"))
            config.PinnedWindowOpacity = Math.Clamp(opacity / 100f, 0.2f, 1f);

        Checkbox("Resolve sub-recipes recursively", ref config.ResolveSubRecipesRecursively);
        Checkbox("Highlight over-budget ingredients in red", ref config.HighlightOverBudgetIngredients);

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
        ImGui.Text("Made with \xE2\x99\xA5 by Aygea");
        ImGui.Spacing();

        // Twitch button — purple #9146FF
        DrawLinkButton("Watch on Twitch", "https://twitch.tv/crazyaygea",
            0xFF9146FF);

        ImGui.SameLine();

        // Website button — teal #00b4b4
        DrawLinkButton("itsaygea.com", "https://itsaygea.com",
            0xFF00B4B4);

        ImGui.Spacing();

        // Ko-fi button — orange #FF5E5B
        DrawLinkButton("Support on Ko-fi", "https://ko-fi.com/aygea",
            0xFFFF5E5B);

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

    private void Checkbox(string label, ref bool value)
    {
        if (ImGui.Checkbox(label, ref value))
            config.Save();
    }

    private void SliderInt(string label, ref int value, int min, int max)
    {
        if (ImGui.SliderInt(label, ref value, min, max))
            config.Save();
    }

    private void ColorPicker(string label, ref uint color)
    {
        var vec = ImGui.ColorConvertU32ToFloat4(color);
        if (ImGui.ColorEdit4(label, ref vec, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaOpaque))
        {
            color = ImGui.ColorConvertFloat4ToU32(vec);
            config.Save();
        }
    }

    private static void DrawLinkButton(string label, string url, uint color)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, ImGui.ColorConvertU32ToFloat4(color));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.ColorConvertU32ToFloat4(Lighten(color)));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGui.ColorConvertU32ToFloat4(color));

        if (ImGui.Button(label))
            Dalamud.Game.Utils.Util.OpenLink(url);

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
}
