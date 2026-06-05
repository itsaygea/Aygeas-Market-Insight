using System;
using System.Reflection;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace AygeaMarketInsight.UI;

public sealed class HubWindow : Window
{
    private readonly Configuration config;
    private readonly string version;

    public System.Action? OpenScanner { get; set; }
    public System.Action? OpenShoppingList { get; set; }
    public System.Action? OpenRetainer { get; set; }
    public System.Action? OpenConfig { get; set; }

    public HubWindow(Configuration config)
        : base("Aygea's Market Insight###AMIHub")
    {
        this.config = config;
        version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        Size = new System.Numerics.Vector2(450, 350);
        SizeCondition = ImGuiCond.FirstUseEver;

        Flags = ImGuiWindowFlags.NoCollapse;
    }

    public override void Draw()
    {
        // Header
        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.2f, 1f),
            $"Aygea's Market Insight  v{version}");
        ImGui.TextDisabled("A crafting profit and market price tool for FFXIV");
        ImGui.Separator();
        ImGui.Spacing();

        // Navigation buttons
        DrawNavButton("Profit Scanner", "Scan recipes for profit opportunities", OpenScanner);
        DrawNavButton("Shopping List", $"Manage crafting materials ({config.ShoppingListItems.Count} items)", OpenShoppingList);
        DrawNavButton("Retainer Ventures", "Optimize retainer venture earnings", OpenRetainer);
        DrawNavButton("Settings", "Configure plugin settings", OpenConfig);

        ImGui.Spacing();
        ImGui.Separator();

        // Social buttons
        DrawLinkButton("Watch on Twitch", "https://twitch.tv/crazyaygea", 0xFFFF4691);
        ImGui.SameLine();
        DrawLinkButton("itsaygea.com", "https://itsaygea.com", 0xFFF0CF89);
        ImGui.SameLine();
        DrawLinkButton("Support on Ko-fi", "https://ko-fi.com/aygea", 0xFF5B5EFF);

        ImGui.Spacing();
        ImGui.TextDisabled("/ami scan  /ami list  /ami config  /ami r");
    }

    private static void DrawNavButton(string label, string description, System.Action? onClick)
    {
        var available = ImGui.GetContentRegionAvail().X;

        if (ImGui.Button($"##{label}", new System.Numerics.Vector2(available, 45)))
            onClick?.Invoke();

        // Overlay text on top of the button
        var pos = ImGui.GetItemRectMin();
        var cursor = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new System.Numerics.Vector2(cursor.X + 10, cursor.Y - 39));
        ImGui.Text(label);
        ImGui.SameLine();
        ImGui.TextDisabled($"  — {description}");

        // Restore cursor below button
        ImGui.SetCursorPos(new System.Numerics.Vector2(cursor.X, cursor.Y + 4));
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
