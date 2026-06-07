using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace AygeaMarketInsight.UI;

public sealed class RetainerVentureWindow : Window
{
    private readonly Configuration config;
    private readonly VentureCache ventureCache;
    private readonly PriceCache priceCache;
    private readonly UniversalisClient universalisClient;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly INotificationManager notificationManager;
    private readonly IPluginLog log;

    // Targeted tab state
    private List<VentureRow> rows = [];
    private bool isLoading;
    private string loadingStatus = string.Empty;
    private DateTime lastRefreshTime;

    // Targeted filters
    private readonly HashSet<VentureType> enabledTypes = [VentureType.Combat, VentureType.Botanist, VentureType.Miner, VentureType.Fisher];
    private int levelFilter = 100;
    private int minGilPerHour;

    // Sorting
    private ImGuiSortDirection sortDirection;
    private int sortColumn = 7;

    // Tab state
    private int currentTab;

    // Exploration tab state
    private int selectedExploration = -1;
    private string explorationSearch = string.Empty;
    private readonly HashSet<ExplorationType> enabledExplorationTypes =
        [ExplorationType.Quick, ExplorationType.Highland, ExplorationType.Woodland, ExplorationType.Waterside, ExplorationType.Field];
    private int explorationLevelFilter = 100;
    private List<ExplorationVenture> filteredExplorations = [];
    private List<(uint ItemId, string Name, uint Price)> selectedDrops = [];

    private static readonly (string Name, VentureType Type)[] TypeToggles =
        [("Combat", VentureType.Combat), ("BTN", VentureType.Botanist), ("MIN", VentureType.Miner), ("FSH", VentureType.Fisher)];

    private static readonly (string Name, ExplorationType Type)[] ExplorationTypeToggles =
        [("Quick", ExplorationType.Quick), ("MIN", ExplorationType.Highland), ("BTN", ExplorationType.Woodland), ("FSH", ExplorationType.Waterside), ("Combat", ExplorationType.Field)];

    private static readonly Vector4 TabActiveColor = new(0.3f, 0.6f, 0.3f, 1f);
    private static readonly Vector4 TabInactiveColor = new(0.25f, 0.25f, 0.25f, 1f);

    public RetainerVentureWindow(
        Configuration config,
        VentureCache ventureCache,
        PriceCache priceCache,
        UniversalisClient universalisClient,
        IObjectTable objectTable,
        IFramework framework,
        INotificationManager notificationManager,
        IPluginLog log)
        : base("Aygea's Market Insight — Retainer Ventures###AMIRetainer")
    {
        this.config = config;
        this.ventureCache = ventureCache;
        this.priceCache = priceCache;
        this.universalisClient = universalisClient;
        this.objectTable = objectTable;
        this.framework = framework;
        this.notificationManager = notificationManager;
        this.log = log;

        Size = new Vector2(900, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        if (rows.Count == 0 && !isLoading)
            BuildRows();

        if (!isLoading && (lastRefreshTime == default ||
            (DateTime.UtcNow - lastRefreshTime).TotalMinutes > config.UniversalisCacheTtlMinutes))
            RefreshPrices();

        if (filteredExplorations.Count == 0)
            BuildExplorationRows();
    }

    public void RefreshIfStale()
    {
        if (IsOpen && !isLoading && (lastRefreshTime == default ||
            (DateTime.UtcNow - lastRefreshTime).TotalMinutes > config.UniversalisCacheTtlMinutes))
        {
            BuildRows();
            if (selectedExploration >= 0)
                BuildSelectedDrops();
        }
    }

    public override void Draw()
    {
        DrawTabBar();

        if (currentTab == 0)
            DrawTargetedTab();
        else
            DrawExplorationTab();

        // Shared status bar with data source info
        var ago = lastRefreshTime == default ? "never" : $"{(DateTime.UtcNow - lastRefreshTime).TotalMinutes:F0}m ago";
        var source = "—";
        if (lastRefreshTime != default)
            source = "Universalis";
        // Check if any MB live data is present
        var mbCount = 0; var apiCount = 0; var cachedCount = 0;
        foreach (var v in ventureCache.Ventures)
        {
            var p = priceCache.Get(v.ItemId);
            if (p == null) { var pi = priceCache.GetIgnoreExpiry(v.ItemId); if (pi != null) cachedCount++; continue; }
            if (p.Source == "MB") mbCount++; else apiCount++;
        }
        if (mbCount > 0 || apiCount > 0 || cachedCount > 0)
            source = $"{apiCount} API" + (mbCount > 0 ? $", {mbCount} MB live" : "") + (cachedCount > 0 ? $", {cachedCount} expired" : "");
        ImGui.TextDisabled($"Last refreshed: {ago}  |  Source: {source}  |  {ventureCache.Ventures.Count} ventures, {ventureCache.Explorations.Count} explorations");
    }

    private void DrawTabBar()
    {
        DrawTabButton("Targeted", 0);
        ImGui.SameLine();
        DrawTabButton("Explorations", 1);
        ImGui.Separator();
    }

    private void DrawTabButton(string label, int tab)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, currentTab == tab ? TabActiveColor : TabInactiveColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, TabActiveColor with { X = TabActiveColor.X + 0.1f, Y = TabActiveColor.Y + 0.1f });
        if (ImGui.Button($"  {label}  "))
            currentTab = tab;
        ImGui.PopStyleColor(2);
    }

    // ── Targeted Tab ──────────────────────────────────────────────

    private void DrawTargetedTab()
    {
        DrawTargetedControls();

        if (isLoading)
        {
            if (rows.Count > 0)
                ImGui.TextDisabled($"Updating... {loadingStatus}  (showing cached data)");
            else
                ImGui.TextDisabled($"Loading prices... {loadingStatus}");
        }

        DrawTargetedTable();
    }

    private void DrawTargetedControls()
    {
        if (ImGui.Button("Refresh Prices"))
            RefreshPrices();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        if (ImGui.SliderInt("Max Level", ref levelFilter, 1, 100))
            BuildRows();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Min Gil/Hr", ref minGilPerHour, 100);

        ImGui.Spacing();
        foreach (var (name, type) in TypeToggles)
        {
            var enabled = enabledTypes.Contains(type);
            ImGui.PushStyleColor(ImGuiCol.Button,
                enabled ? new Vector4(0.3f, 0.6f, 0.3f, 1f) : new Vector4(0.3f, 0.3f, 0.3f, 1f));

            if (ImGui.SmallButton(name))
            {
                if (enabled) enabledTypes.Remove(type);
                else enabledTypes.Add(type);
                BuildRows();
            }

            ImGui.PopStyleColor();
            ImGui.SameLine();
        }

        ImGui.NewLine();
    }

    private void DrawTargetedTable()
    {
        var filteredRows = GetFilteredRows();

        var flags = ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit;

        if (!ImGui.BeginTable("VentureTable", 9, flags,
            ImGui.GetContentRegionAvail() with { Y = ImGui.GetContentRegionAvail().Y - 25 }))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.DefaultSort, 55);
        ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.DefaultSort, 35);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.DefaultSort, 35);
        ImGui.TableSetupColumn("XP", ImGuiTableColumnFlags.DefaultSort, 55);
        ImGui.TableSetupColumn("MB Price", ImGuiTableColumnFlags.DefaultSort, 80);
        ImGui.TableSetupColumn("Gil/Run", ImGuiTableColumnFlags.DefaultSort, 80);
        ImGui.TableSetupColumn("Gil/Hr", ImGuiTableColumnFlags.DefaultSort, 80);
        ImGui.TableSetupColumn("Velocity", ImGuiTableColumnFlags.DefaultSort, 60);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var sorts = ImGui.TableGetSortSpecs();
        if (sorts.SpecsDirty && sorts.SpecsCount > 0)
        {
            var spec = sorts.Specs[0];
            sortColumn = spec.ColumnIndex;
            sortDirection = spec.SortDirection;
            sorts.SpecsDirty = false;
        }

        var bestGilHr = filteredRows.Count > 0 ? filteredRows.Max(r => r.GilPerHour) : 0;
        var bestXp = filteredRows.Count > 0 ? filteredRows.Max(r => r.XpReward) : 0;

        foreach (var row in filteredRows)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.Text(row.ItemName);
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                ImGui.SetClipboardText(row.ItemName);
                notificationManager.AddNotification(new Notification
                {
                    Content = row.ItemName,
                    Title = "Copied to clipboard",
                    Type = NotificationType.Success,
                });
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Double-click to copy");

            ImGui.TableSetColumnIndex(1);
            ImGui.Text(row.TypeName);

            ImGui.TableSetColumnIndex(2);
            ImGui.Text($"{row.Level}");

            ImGui.TableSetColumnIndex(3);
            ImGui.Text($"{row.Quantity}");

            ImGui.TableSetColumnIndex(4);
            if (row.XpReward == bestXp && bestXp > 0)
                ImGui.TextColored(new Vector4(0.4f, 0.6f, 1f, 1f), $"{row.XpReward:N0}");
            else
                ImGui.Text($"{row.XpReward:N0}");

            ImGui.TableSetColumnIndex(5);
            ImGui.Text(row.MbPrice > 0 ? $"{row.MbPrice:N0}" : "—");

            ImGui.TableSetColumnIndex(6);
            ImGui.Text(row.GilPerRun > 0 ? $"{row.GilPerRun:N0}" : "—");

            ImGui.TableSetColumnIndex(7);
            if (row.GilPerHour == bestGilHr && bestGilHr > 0)
                ImGui.TextColored(new Vector4(0f, 0.8f, 0f, 1f), $"{row.GilPerHour:N0}");
            else if (row.GilPerHour > 0)
                ImGui.Text($"{row.GilPerHour:N0}");
            else
                ImGui.TextDisabled("—");

            ImGui.TableSetColumnIndex(8);
            ImGui.Text(row.Velocity > 0 ? $"{row.Velocity:F1}" : "—");
        }

        ImGui.EndTable();
    }

    private List<VentureRow> GetFilteredRows()
    {
        var query = rows.AsEnumerable();

        if (minGilPerHour > 0)
            query = query.Where(r => r.GilPerHour >= minGilPerHour);

        query = sortDirection == ImGuiSortDirection.Ascending
            ? sortColumn switch
            {
                0 => query.OrderBy(r => r.ItemName),
                1 => query.OrderBy(r => r.TypeName),
                2 => query.OrderBy(r => r.Level),
                3 => query.OrderBy(r => r.Quantity),
                4 => query.OrderBy(r => r.XpReward),
                5 => query.OrderBy(r => r.MbPrice),
                6 => query.OrderBy(r => r.GilPerRun),
                7 => query.OrderBy(r => r.GilPerHour),
                8 => query.OrderBy(r => r.Velocity),
                _ => query,
            }
            : sortColumn switch
            {
                0 => query.OrderByDescending(r => r.ItemName),
                1 => query.OrderByDescending(r => r.TypeName),
                2 => query.OrderByDescending(r => r.Level),
                3 => query.OrderByDescending(r => r.Quantity),
                4 => query.OrderByDescending(r => r.XpReward),
                5 => query.OrderByDescending(r => r.MbPrice),
                6 => query.OrderByDescending(r => r.GilPerRun),
                7 => query.OrderByDescending(r => r.GilPerHour),
                8 => query.OrderByDescending(r => r.Velocity),
                _ => query,
            };

        return query.ToList();
    }

    // ── Exploration Tab ────────────────────────────────────────────

    private void DrawExplorationTab()
    {
        DrawExplorationControls();
        DrawExplorationSplit();
    }

    private void DrawExplorationControls()
    {
        if (ImGui.Button("Refresh Prices##Ex"))
            RefreshPrices();

        if (isLoading)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Updating... {loadingStatus}");
        }

        ImGui.Spacing();

        ImGui.SetNextItemWidth(200);
        if (ImGui.InputTextWithHint("##ExplorationSearch", "Search drops (minion, material...)", ref explorationSearch, 100))
            BuildExplorationRows();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        if (ImGui.SliderInt("Max Level##Ex", ref explorationLevelFilter, 1, 100))
            BuildExplorationRows();

        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Search filters by drop item names.");
            ImGui.Text("Click a venture row to see its drops below.");
            ImGui.Spacing();
            ImGui.Text("Name colors (by best drop value):");
            ImGui.TextColored(new Vector4(0f, 0.8f, 0f, 1f), "  Green");
            ImGui.SameLine();
            ImGui.TextUnformatted("— Top value drops (80%+ of best)");
            ImGui.TextColored(new Vector4(0.4f, 0.6f, 1f, 1f), "  Blue");
            ImGui.SameLine();
            ImGui.TextUnformatted("— Good value drops (40%+ of best)");
            ImGui.EndTooltip();
        }

        ImGui.Spacing();
        foreach (var (name, type) in ExplorationTypeToggles)
        {
            var enabled = enabledExplorationTypes.Contains(type);
            ImGui.PushStyleColor(ImGuiCol.Button,
                enabled ? new Vector4(0.3f, 0.6f, 0.3f, 1f) : new Vector4(0.3f, 0.3f, 0.3f, 1f));

            if (ImGui.SmallButton($"{name}##ex"))
            {
                if (enabled) enabledExplorationTypes.Remove(type);
                else enabledExplorationTypes.Add(type);
                BuildExplorationRows();
            }

            ImGui.PopStyleColor();
            ImGui.SameLine();
        }

        ImGui.NewLine();
    }

    private void DrawExplorationSplit()
    {
        var avail = ImGui.GetContentRegionAvail();
        // Reserve 25px at bottom for the shared status bar
        var usableHeight = avail.Y - 25;
        var tableHeight = selectedExploration >= 0 ? usableHeight * 0.45f : usableHeight - 5;

        // Venture table (top half)
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp;

        if (ImGui.BeginTable("ExplorationTable", 6, flags,
            new Vector2(avail.X, tableHeight)))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 4f);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableSetupColumn("Best Drop", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Drops", ImGuiTableColumnFlags.WidthStretch, 0.6f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            // Pre-calculate best drop price per venture for profit coloring.
            // Three tiers: (1) active (has velocity), (2) non-outlier (reasonable price),
            // (3) nothing matched → show "—". Never fall back to outlier prices.
            var bestPrices = new float[filteredExplorations.Count];
            for (var i = 0; i < filteredExplorations.Count; i++)
            {
                var activeBest = 0u;   // passes outlier + velocity filter
                var reasonBest = 0u;   // passes outlier but not velocity
                foreach (var dropId in filteredExplorations[i].DropItemIds)
                {
                    var cached = priceCache.GetIgnoreExpiry(dropId);
                    var p = cached?.NqPrice ?? 0;
                    if (p == 0) continue;

                    // Outlier: world price > 10x DC cheapest = price spread too high
                    var dcMin = cached?.DcMinPrice ?? 0;
                    if (dcMin > 0 && p > dcMin * 10) continue;

                    if (p > reasonBest) reasonBest = p;

                    // Dead item: 0 DC velocity = nobody buys it on entire data center
                    var vel = cached?.NqSaleVelocity ?? 0;
                    var src = cached?.Source ?? "";
                    if (vel == 0 && src != "MB") continue;

                    if (p > activeBest) activeBest = p;
                }
                bestPrices[i] = activeBest > 0 ? activeBest : reasonBest;
            }
            var topPrice = bestPrices.Length > 0 ? bestPrices.Max() : 0;

            for (var i = 0; i < filteredExplorations.Count; i++)
            {
                var ex = filteredExplorations[i];
                ImGui.TableNextRow();

                var isSelected = i == selectedExploration;
                if (isSelected)
                {
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.4f, 0.2f, 0.4f)));
                }

                ImGui.TableSetColumnIndex(0);
                if (ImGui.Selectable($"##ex{i}", isSelected, ImGuiSelectableFlags.SpanAllColumns))
                {
                    selectedExploration = isSelected ? -1 : i;
                    if (selectedExploration >= 0)
                        BuildSelectedDrops();
                }
                ImGui.SameLine();

                // Color venture name based on best drop value
                var bestDrop = bestPrices[i];
                if (bestDrop > 0 && topPrice > 0)
                {
                    var ratio = bestDrop / (float)topPrice;
                    if (ratio >= 0.8f)
                        ImGui.TextColored(new Vector4(0f, 0.8f, 0f, 1f), ex.Name); // green — top value
                    else if (ratio >= 0.4f)
                        ImGui.TextColored(new Vector4(0.4f, 0.6f, 1f, 1f), ex.Name); // blue — good value
                    else
                        ImGui.Text(ex.Name);
                }
                else
                {
                    ImGui.Text(ex.Name);
                }

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(GetExplorationTypeLabel(ex.ExplorationType));

                ImGui.TableSetColumnIndex(2);
                ImGui.Text($"{ex.MaxLevel}");

                ImGui.TableSetColumnIndex(3);
                if (bestDrop > 0)
                    ImGui.Text($"{bestDrop:N0}");
                else
                    ImGui.TextDisabled("—");

                ImGui.TableSetColumnIndex(4);
                ImGui.Text($"{ex.DropItemIds.Count}");
            }

            ImGui.EndTable();
        }

        // Drops panel (bottom half)
        if (selectedExploration >= 0 && selectedExploration < filteredExplorations.Count)
        {
            var ex = filteredExplorations[selectedExploration];

            // Find which item is the actual Best Drop (same tiered logic as the top table)
            uint activeBestId = 0, activeBestPrice = 0;
            uint reasonBestId = 0, reasonBestPrice = 0;
            foreach (var dropId in ex.DropItemIds)
            {
                var cached = priceCache.GetIgnoreExpiry(dropId);
                var p = cached?.NqPrice ?? 0;
                if (p == 0) continue;
                var dcMin = cached?.DcMinPrice ?? 0;
                if (dcMin > 0 && p > dcMin * 10) continue;
                if (p > reasonBestPrice) { reasonBestPrice = p; reasonBestId = dropId; }
                var vel = cached?.NqSaleVelocity ?? 0;
                var src = cached?.Source ?? "";
                if (vel == 0 && src != "MB") continue;
                if (p > activeBestPrice) { activeBestPrice = p; activeBestId = dropId; }
            }
            var bestDropItemId = activeBestPrice > 0 ? activeBestId : reasonBestId;

            ImGui.Separator();
            ImGui.Text($"Drops from: {ex.Name}");
            ImGui.SameLine();
            ImGui.TextDisabled($"({selectedDrops.Count} items)");

            var pricedCount = selectedDrops.Count(d => d.Price > 0);
            if (pricedCount > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0f, 0.8f, 0f, 1f),
                    $"  Highest: {selectedDrops.Max(d => d.Price):N0}");
            }

            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "(?)");
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text("Color highlights:");
                ImGui.TextColored(new Vector4(0f, 0.8f, 0f, 1f), "  Green row");
                ImGui.SameLine();
                ImGui.TextUnformatted("— Best Drop (matches top table)");
                ImGui.TextColored(new Vector4(0.6f, 0.3f, 0.3f, 1f), "  Red text");
                ImGui.SameLine();
                ImGui.TextUnformatted("— Price spread too high");
                ImGui.TextColored(new Vector4(0.8f, 0.6f, 0.2f, 1f), "  Orange notes");
                ImGui.SameLine();
                ImGui.TextUnformatted("— Low velocity or filtered");
                ImGui.EndTooltip();
            }

            DrawDropsTable(avail, bestDropItemId);
        }
    }

    private void DrawDropsTable(Vector2 avail, uint bestDropItemId)
    {
        if (!ImGui.BeginTable("DropsTable", 5,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV |
            ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp,
            new Vector2(avail.X, ImGui.GetContentRegionAvail().Y - 30)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn("MB Price", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("DC Min", ImGuiTableColumnFlags.WidthStretch, 1.3f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("Notes", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var sorted = selectedDrops.OrderByDescending(d => d.Price).ToList();

        foreach (var drop in sorted)
        {
            var cached = priceCache.GetIgnoreExpiry(drop.ItemId);
            var dcMin = cached?.DcMinPrice ?? 0;
            var dcMinWorld = cached?.DcMinPriceWorld ?? "";
            var vel = cached?.NqSaleVelocity ?? 0;
            var src = cached?.Source ?? "";

            var isOutlier = dcMin > 0 && drop.Price > dcMin * 10;
            var isLowVel = vel == 0 && src != "MB" && drop.Price > 0;
            var isBestDrop = drop.ItemId == bestDropItemId;

            ImGui.TableNextRow();

            if (isBestDrop)
            {
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.4f, 0.2f, 0.4f)));
            }

            ImGui.TableSetColumnIndex(0);
            if (isBestDrop)
                ImGui.TextColored(new Vector4(0f, 0.8f, 0f, 1f), drop.Name);
            else if (isOutlier)
                ImGui.TextColored(new Vector4(0.6f, 0.3f, 0.3f, 1f), drop.Name);
            else
                ImGui.Text(drop.Name);
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                ImGui.SetClipboardText(drop.Name);
                notificationManager.AddNotification(new Notification
                {
                    Content = drop.Name,
                    Title = "Copied to clipboard",
                    Type = NotificationType.Success,
                });
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Double-click to copy");

            ImGui.TableSetColumnIndex(1);
            if (drop.Price > 0)
            {
                if (isOutlier)
                    ImGui.TextColored(new Vector4(0.6f, 0.3f, 0.3f, 1f), $"{drop.Price:N0}");
                else if (isBestDrop)
                    ImGui.TextColored(new Vector4(0f, 0.8f, 0f, 1f), $"{drop.Price:N0}");
                else
                    ImGui.Text($"{drop.Price:N0}");
            }
            else
            {
                ImGui.TextDisabled("—");
            }

            ImGui.TableSetColumnIndex(2);
            if (dcMin > 0)
            {
                var dcMinText = dcMinWorld.Length > 0 ? $"{dcMin:N0} ({dcMinWorld})" : $"{dcMin:N0}";
                if (drop.Price > 0 && dcMin < drop.Price)
                    ImGui.TextColored(new Vector4(0.4f, 0.7f, 0.4f, 1f), dcMinText);
                else
                    ImGui.Text(dcMinText);
            }
            else
                ImGui.TextDisabled("—");

            ImGui.TableSetColumnIndex(3);
            ImGui.TextDisabled(src.Length > 0 ? src : "—");

            ImGui.TableSetColumnIndex(4);
            if (isOutlier)
                ImGui.TextColored(new Vector4(0.8f, 0.6f, 0.2f, 1f), "Price spread too high");
            else if (isLowVel)
                ImGui.TextColored(new Vector4(0.8f, 0.6f, 0.2f, 1f), "Low velocity");
            else
                ImGui.TextDisabled("—");
        }

        ImGui.EndTable();
    }

    private static string GetExplorationTypeLabel(ExplorationType type) => type switch
    {
        ExplorationType.Quick => "Quick",
        ExplorationType.Highland => "MIN",
        ExplorationType.Woodland => "BTN",
        ExplorationType.Waterside => "FSH",
        ExplorationType.Field => "Combat",
        _ => type.ToString(),
    };

    // ── Data Building ──────────────────────────────────────────────

    private void BuildRows()
    {
        rows.Clear();

        var ventures = ventureCache.GetVenturesForLevel((byte)levelFilter)
            .Where(v => enabledTypes.Contains(v.Type));

        foreach (var v in ventures)
        {
            var cached = priceCache.GetIgnoreExpiry(v.ItemId);
            var mbPrice = cached?.NqPrice ?? 0;
            var velocity = cached?.NqSaleVelocity ?? 0;

            // Zero out obvious outliers (price spread too high): world price > 10x DC cheapest
            var dcMin = cached?.DcMinPrice ?? 0;
            if (dcMin > 0 && mbPrice > dcMin * 10)
                mbPrice = 0;

            var gilPerRun = mbPrice > 0
                ? (int)(v.Quantity * mbPrice * (1f - config.SalesTaxPercent / 100f))
                : 0;

            var hoursPerRun = v.DurationMinutes > 0 ? v.DurationMinutes / 60f : 1f;
            var gilPerHour = gilPerRun > 0 ? (int)(gilPerRun / hoursPerRun) : 0;

            rows.Add(new VentureRow
            {
                TaskId = v.TaskId,
                ItemId = v.ItemId,
                ItemName = v.ItemName,
                TypeName = v.Type.ToString(),
                Level = v.RequiredLevel,
                Quantity = v.Quantity,
                XpReward = v.XpReward,
                MbPrice = mbPrice,
                GilPerRun = gilPerRun,
                GilPerHour = gilPerHour,
                Velocity = velocity,
                DurationMinutes = v.DurationMinutes,
            });
        }
    }

    private void BuildExplorationRows()
    {
        var query = ventureCache.Explorations.AsEnumerable();

        // Filter by type
        query = query.Where(e => enabledExplorationTypes.Contains(e.ExplorationType));

        // Filter by level
        if (explorationLevelFilter < 100)
            query = query.Where(e => e.MaxLevel <= explorationLevelFilter);

        // Search filter — match against drop item names
        if (!string.IsNullOrWhiteSpace(explorationSearch))
        {
            var search = explorationSearch.Trim().ToLowerInvariant();
            query = query.Where(e =>
                e.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.DropItemIds.Any(id =>
                {
                    var name = ventureCache.GetItemName(id);
                    return name != null && name.Contains(search, StringComparison.OrdinalIgnoreCase);
                }));
        }

        filteredExplorations = query.OrderBy(e => e.ExplorationType).ThenBy(e => e.Name).ToList();
        selectedExploration = -1;
        selectedDrops.Clear();
    }

    private void BuildSelectedDrops()
    {
        selectedDrops.Clear();
        if (selectedExploration < 0 || selectedExploration >= filteredExplorations.Count) return;

        var ex = filteredExplorations[selectedExploration];
        foreach (var itemId in ex.DropItemIds)
        {
            var name = ventureCache.GetItemName(itemId) ?? $"Item #{itemId}";
            var cached = priceCache.GetIgnoreExpiry(itemId);
            var price = cached?.NqPrice ?? 0;
            selectedDrops.Add((itemId, name, price));
        }
    }

    private void RefreshPrices()
    {
        if (isLoading) return;

        // Get world ID directly from player data
        var worldId = objectTable.LocalPlayer?.HomeWorld.RowId ?? 0;
        if (worldId == 0)
        {
            notificationManager.AddNotification(new Notification
            {
                Content = "Could not detect your home world. Are you logged in?",
                Title = "Refresh Failed",
                Type = NotificationType.Error,
            });
            return;
        }

        isLoading = true;
        loadingStatus = "collecting items...";

        // Collect only retainer-relevant IDs: venture items + exploration drops
        var ids = new HashSet<uint>();
        foreach (var v in ventureCache.Ventures)
            ids.Add(v.ItemId);
        foreach (var ex in ventureCache.Explorations)
            foreach (var dropId in ex.DropItemIds)
                ids.Add(dropId);

        var fetchCount = ids.Count;
        var ttl = config.UniversalisCacheTtlMinutes;

#pragma warning disable CS4014
        _ = Task.Run(async () =>
        {
            try
            {
                framework.RunOnFrameworkThread(() =>
                    loadingStatus = $"fetching {fetchCount} items...");

                var results = await universalisClient.FetchPrices(worldId, ids, ttl,
                    (done, total) => framework.RunOnFrameworkThread(() =>
                        loadingStatus = $"fetching {done}/{total}"));

                framework.RunOnFrameworkThread(() =>
                {
                    foreach (var kvp in results)
                    {
                        var p = kvp.Value;
                        priceCache.SetFull(kvp.Key, p.NqPrice, p.HqPrice,
                            0, string.Empty, p.NqSaleVelocity, p.HqSaleVelocity,
                            p.Source, TimeSpan.FromMinutes(ttl), p.NqDcPrice, p.NqDcPriceWorld);
                    }

                    BuildRows();
                    if (selectedExploration >= 0)
                        BuildSelectedDrops();
                    lastRefreshTime = DateTime.UtcNow;
                    isLoading = false;
                    loadingStatus = string.Empty;
                    notificationManager.AddNotification(new Notification
                    {
                        Content = $"Updated prices for {fetchCount} items",
                        Title = "Prices Refreshed",
                        Type = NotificationType.Success,
                    });
                });
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Retainer price refresh failed");
                framework.RunOnFrameworkThread(() =>
                {
                    isLoading = false;
                    loadingStatus = string.Empty;
                    notificationManager.AddNotification(new Notification
                    {
                        Content = $"Price refresh failed: {ex.Message}",
                        Title = "Refresh Error",
                        Type = NotificationType.Error,
                    });
                });
            }
        });
#pragma warning restore CS4014
    }
}

internal sealed class VentureRow
{
    public uint TaskId;
    public uint ItemId;
    public string ItemName = string.Empty;
    public string TypeName = string.Empty;
    public byte Level;
    public byte Quantity;
    public int XpReward;
    public uint MbPrice;
    public int GilPerRun;
    public int GilPerHour;
    public float Velocity;
    public ushort DurationMinutes;
}
