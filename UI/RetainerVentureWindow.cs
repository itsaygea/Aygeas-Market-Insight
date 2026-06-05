using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    private readonly IPluginLog log;

    private List<VentureRow> rows = [];
    private bool isLoading;
    private string loadingStatus = string.Empty;
    private DateTime lastRefreshTime;

    // Filters
    private int selectedType = -1; // -1 = All
    private int levelFilter = 100;
    private int minGilPerHour;

    // Sorting
    private ImGuiSortDirection sortDirection;
    private int sortColumn = 7; // Gil/Hr by default

    private static readonly string[] TypeNames = ["All", "Combat", "Botanist", "Miner", "Fisher"];
    private static readonly VentureType?[] TypeFilters = [null, VentureType.Combat, VentureType.Botanist, VentureType.Miner, VentureType.Fisher];

    public RetainerVentureWindow(
        Configuration config,
        VentureCache ventureCache,
        PriceCache priceCache,
        UniversalisClient universalisClient,
        IObjectTable objectTable,
        IFramework framework,
        IPluginLog log)
        : base("Aygea's Market Insight — Retainer Ventures###AMIRetainer")
    {
        this.config = config;
        this.ventureCache = ventureCache;
        this.priceCache = priceCache;
        this.universalisClient = universalisClient;
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;

        Size = new System.Numerics.Vector2(800, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        if (rows.Count == 0 && !isLoading)
            BuildRows();

        if (!isLoading && (lastRefreshTime == default ||
            (DateTime.UtcNow - lastRefreshTime).TotalMinutes > config.UniversalisCacheTtlMinutes))
            RefreshPrices();
    }

    public override void Draw()
    {
        DrawControls();

        if (isLoading)
        {
            if (rows.Count > 0)
                ImGui.TextDisabled($"Updating... {loadingStatus}  (showing cached data)");
            else
                ImGui.TextDisabled($"Loading prices... {loadingStatus}");
        }

        DrawTable();

        var ago = lastRefreshTime == default ? "never" : $"{(DateTime.UtcNow - lastRefreshTime).TotalMinutes:F0}m ago";
        ImGui.TextDisabled($"Last refreshed: {ago}  |  {rows.Count} ventures");
    }

    private void DrawControls()
    {
        if (ImGui.Button("Refresh Prices"))
            RefreshPrices();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        if (ImGui.Combo("Type", ref selectedType, TypeNames, TypeNames.Length))
            BuildRows();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        if (ImGui.SliderInt("Max Level", ref levelFilter, 1, 100))
            BuildRows();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Min Gil/Hr", ref minGilPerHour, 100);
    }

    private void DrawTable()
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

        // Find best values for highlighting
        var bestGilHr = filteredRows.Count > 0 ? filteredRows.Max(r => r.GilPerHour) : 0;
        var bestXp = filteredRows.Count > 0 ? filteredRows.Max(r => r.XpReward) : 0;

        foreach (var row in filteredRows)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.Text(row.ItemName);

            ImGui.TableSetColumnIndex(1);
            ImGui.Text(row.TypeName);

            ImGui.TableSetColumnIndex(2);
            ImGui.Text($"{row.Level}");

            ImGui.TableSetColumnIndex(3);
            ImGui.Text($"{row.Quantity}");

            ImGui.TableSetColumnIndex(4);
            if (row.XpReward == bestXp && bestXp > 0)
                ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.6f, 1f, 1f), $"{row.XpReward:N0}");
            else
                ImGui.Text($"{row.XpReward:N0}");

            ImGui.TableSetColumnIndex(5);
            ImGui.Text(row.MbPrice > 0 ? $"{row.MbPrice:N0}" : "—");

            ImGui.TableSetColumnIndex(6);
            ImGui.Text(row.GilPerRun > 0 ? $"{row.GilPerRun:N0}" : "—");

            ImGui.TableSetColumnIndex(7);
            if (row.GilPerHour == bestGilHr && bestGilHr > 0)
                ImGui.TextColored(new System.Numerics.Vector4(0f, 0.8f, 0f, 1f), $"{row.GilPerHour:N0}");
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

        // Apply sorting
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

    private void BuildRows()
    {
        rows.Clear();

        var typeFilter = selectedType >= 0 && selectedType < TypeFilters.Length
            ? TypeFilters[selectedType] : null;

        var ventures = ventureCache.GetVenturesForLevel((byte)levelFilter, typeFilter);

        foreach (var v in ventures)
        {
            var cached = priceCache.GetIgnoreExpiry(v.ItemId);
            var mbPrice = cached?.NqPrice ?? 0;
            var velocity = cached?.NqSaleVelocity ?? 0;

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

    private void RefreshPrices()
    {
        if (isLoading) return;

        var worldId = config.HomeWorldId > 0 ? config.HomeWorldId : (objectTable.LocalPlayer?.HomeWorld.RowId ?? 0);
        if (worldId == 0) return;

        isLoading = true;
        loadingStatus = "collecting items...";

        var itemIds = ventureCache.Ventures.Select(v => v.ItemId).Distinct().ToHashSet();

#pragma warning disable CS4014
        _ = Task.Run(async () =>
        {
            try
            {
                var ttl = config.UniversalisCacheTtlMinutes;

                var results = await universalisClient.FetchPrices(worldId, itemIds, ttl,
                    (done, total) => framework.RunOnFrameworkThread(() =>
                        loadingStatus = $"fetching {done}/{total}"));

                framework.RunOnFrameworkThread(() =>
                {
                    foreach (var kvp in results)
                    {
                        var p = kvp.Value;
                        priceCache.Set(kvp.Key, p.NqPrice, p.HqPrice, p.Source,
                            TimeSpan.FromMinutes(ttl));
                    }

                    BuildRows();
                    lastRefreshTime = DateTime.UtcNow;
                    isLoading = false;
                    loadingStatus = string.Empty;
                });
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Venture price refresh failed");
                framework.RunOnFrameworkThread(() =>
                {
                    isLoading = false;
                    loadingStatus = string.Empty;
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
