using System;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace AygeaMarketInsight.UI;

public sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly ArtisanIpc artisanIpc;
    private readonly ITextureProvider textureProvider;
    private readonly IPluginLog log;

    private IDalamudTextureWrap? emoteTexture;

    public ConfigWindow(Configuration config, ArtisanIpc artisanIpc, ITextureProvider textureProvider, IPluginLog log)
        : base("Aygea's Market Insight — Settings###AMIConfig")
    {
        this.config = config;
        this.artisanIpc = artisanIpc;
        this.textureProvider = textureProvider;
        this.log = log;

        Size = new System.Numerics.Vector2(500, 450);
        SizeCondition = ImGuiCond.FirstUseEver;

        _ = DownloadEmoteAsync();
    }

    private async Task DownloadEmoteAsync()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var bytes = await http.GetByteArrayAsync(
                "https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_6abe43bf242c4ec785966edbd450b433/default/dark/1.0");
            emoteTexture = await textureProvider.CreateFromImageAsync(bytes, "crazyayL emote");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to download Twitch emote image");
        }
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
        if (emoteTexture != null)
        {
            var scale = 22f / emoteTexture.Size.Y;
            var emoteSize = new System.Numerics.Vector2(emoteTexture.Size.X * scale, emoteTexture.Size.Y * scale);
            ImGui.Text("Made with");
            ImGui.SameLine();
            ImGui.Image(emoteTexture.ImGuiIntPtr, emoteSize);
            ImGui.SameLine();
            ImGui.Text("by Aygea");
        }
        else
        {
            ImGui.Text("Made with crazyayL by Aygea");
        }
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
}
