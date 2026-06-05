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
        ImGui.TextDisabled("/ami scan  /ami list  /ami config  /ami detail");
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
}
