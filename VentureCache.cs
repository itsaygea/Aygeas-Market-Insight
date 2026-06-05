using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AygeaMarketInsight;

public enum VentureType { Combat, Botanist, Miner, Fisher }

public sealed class VentureInfo
{
    public uint TaskId { get; init; }
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = "";
    public byte RequiredLevel { get; init; }
    public int XpReward { get; init; }
    public byte Quantity { get; init; }
    public ushort DurationMinutes { get; init; }
    public VentureType Type { get; init; }
}

public sealed class VentureCache
{
    private readonly IPluginLog log;
    private readonly List<VentureInfo> ventures = [];
    private readonly Dictionary<uint, string> itemNames = [];

    public IReadOnlyList<VentureInfo> Ventures => ventures;

    public VentureCache(IDataManager dataManager, IPluginLog log)
    {
        this.log = log;
        Load(dataManager);
    }

    private void Load(IDataManager dataManager)
    {
        var taskSheet = dataManager.GetExcelSheet<RetainerTask>();
        var normalSheet = dataManager.GetExcelSheet<RetainerTaskNormal>();
        if (taskSheet == null || normalSheet == null)
        {
            log.Warning("VentureCache: Failed to load RetainerTask sheets");
            return;
        }

        foreach (var task in taskSheet)
        {
            if (task.IsRandom) continue;

            if (task.Task.RowId == 0) continue;

            var normalRow = normalSheet.GetRow(task.Task.RowId);
            if (normalRow == null) continue;
            var normal = normalRow.Value;
            if (normal.Item.RowId == 0) continue;

            var item = normal.Item.Value;

            var itemName = item.Name.ToString();
            if (string.IsNullOrEmpty(itemName)) continue;

            // Use Quantity2 as middle-tier baseline
            byte quantity = normal.Quantity2;
            if (quantity == 0) quantity = 1;

            var type = ClassifyType(task);

            ventures.Add(new VentureInfo
            {
                TaskId = task.RowId,
                ItemId = item.RowId,
                ItemName = itemName,
                Quantity = quantity,
                RequiredLevel = task.RetainerLevel,
                XpReward = task.Experience,
                DurationMinutes = task.MaxTimemin,
                Type = type,
            });

            itemNames[item.RowId] = itemName;
        }

        log.Information($"VentureCache loaded {ventures.Count} ventures");
    }

    private static VentureType ClassifyType(RetainerTask task)
    {
        if (task.RequiredItemLevel > 0) return VentureType.Combat;

        var catName = task.ClassJobCategory.Value.Name.ToString();
        if (catName.Contains("BTN")) return VentureType.Botanist;
        if (catName.Contains("MIN")) return VentureType.Miner;
        if (catName.Contains("FSH")) return VentureType.Fisher;
        return VentureType.Combat;
    }

    public List<VentureInfo> GetVenturesForLevel(byte level, VentureType? filter = null)
    {
        var query = ventures.Where(v => v.RequiredLevel <= level);
        if (filter.HasValue)
            query = query.Where(v => v.Type == filter.Value);
        return query.ToList();
    }

    public string? GetItemName(uint itemId)
    {
        return itemNames.GetValueOrDefault(itemId);
    }
}
