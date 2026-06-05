using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AygeaMarketInsight;

public enum VentureType { Combat, Botanist, Miner, Fisher }
public enum ExplorationType { Quick, Highland, Field, Waterside }

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

public sealed class ExplorationVenture
{
    public uint TaskRandomId { get; init; }
    public string Name { get; init; } = "";
    public ExplorationType ExplorationType { get; init; }
    public byte MaxLevel { get; init; }
    public int XpReward { get; init; }
    public ushort DurationMinutes { get; init; }
    public List<uint> DropItemIds { get; init; } = [];
}

public sealed class VentureCache
{
    private readonly IPluginLog log;
    private readonly List<VentureInfo> ventures = [];
    private readonly List<ExplorationVenture> explorations = [];
    private readonly Dictionary<uint, string> itemNames = [];

    public IReadOnlyList<VentureInfo> Ventures => ventures;
    public IReadOnlyList<ExplorationVenture> Explorations => explorations;

    public VentureCache(IDataManager dataManager, IPluginLog log)
    {
        this.log = log;
        Load(dataManager);
        LoadExplorations(dataManager);
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

            var normal = normalSheet.GetRow(task.Task.RowId);
            if (normal.Item.RowId == 0) continue;

            var item = normal.Item.Value;

            var itemName = item.Name.ToString();
            if (string.IsNullOrEmpty(itemName)) continue;

            // TODO: Find correct Lumina property name for Quantity[n] array on RetainerTaskNormal
            byte quantity = 1;

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

    private void LoadExplorations(IDataManager dataManager)
    {
        var dropMap = LoadDropCsv();
        if (dropMap.Count == 0) return;

        var randomSheet = dataManager.GetExcelSheet<RetainerTaskRandom>();
        var taskSheet = dataManager.GetExcelSheet<RetainerTask>();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (randomSheet == null || taskSheet == null || itemSheet == null)
        {
            log.Warning("VentureCache: Failed to load exploration sheets");
            return;
        }

        // Build item name lookup from the Item sheet for drops
        foreach (var item in itemSheet)
        {
            if (item.RowId == 0) continue;
            var n = item.Name.ToString();
            if (!string.IsNullOrEmpty(n))
                itemNames.TryAdd(item.RowId, n);
        }

        // Group RetainerTask random rows by their TaskRandomId for level/XP info
        var taskByRandom = new Dictionary<uint, List<RetainerTask>>();
        foreach (var task in taskSheet)
        {
            if (!task.IsRandom) continue;
            var randomId = task.Task.RowId;
            if (randomId == 0) continue;
            if (!taskByRandom.ContainsKey(randomId))
                taskByRandom[randomId] = [];
            taskByRandom[randomId].Add(task);
        }

        foreach (var randomRow in randomSheet)
        {
            var randomId = randomRow.RowId;
            if (!dropMap.ContainsKey(randomId)) continue;

            var name = randomRow.Name.ToString();
            if (string.IsNullOrEmpty(name)) continue;

            byte maxLevel = 0;
            int xp = 0;
            ushort duration = 0;

            if (taskByRandom.TryGetValue(randomId, out var tasks))
            {
                var highest = tasks.OrderByDescending(t => t.RetainerLevel).First();
                maxLevel = highest.RetainerLevel;
                xp = highest.Experience;
                duration = highest.MaxTimemin;
            }

            explorations.Add(new ExplorationVenture
            {
                TaskRandomId = randomId,
                Name = name,
                ExplorationType = ClassifyExploration(name),
                MaxLevel = maxLevel,
                XpReward = xp,
                DurationMinutes = duration,
                DropItemIds = dropMap[randomId],
            });
        }

        log.Information($"VentureCache loaded {explorations.Count} exploration ventures with {dropMap.Values.Sum(v => v.Count)} total drops");
    }

    private Dictionary<uint, List<uint>> LoadDropCsv()
    {
        var result = new Dictionary<uint, List<uint>>();
        var assembly = typeof(VentureCache).Assembly;

        // Find the resource by suffix since namespace prefix may vary
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("retainer_venture_items.csv"));

        if (resourceName == null)
        {
            log.Warning("VentureCache: Embedded CSV resource not found");
            return result;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return result;

        using var reader = new StreamReader(stream);
        reader.ReadLine(); // skip header

        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            if (!uint.TryParse(parts[0], out var itemId)) continue;
            if (!uint.TryParse(parts[1], out var taskRandomId)) continue;

            if (!result.ContainsKey(taskRandomId))
                result[taskRandomId] = [];
            result[taskRandomId].Add(itemId);
        }

        return result;
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

    private static ExplorationType ClassifyExploration(string name)
    {
        if (name.Contains("Quick")) return ExplorationType.Quick;
        if (name.Contains("Highland")) return ExplorationType.Highland;
        if (name.Contains("Field") || name.Contains("Woodland")) return ExplorationType.Field;
        if (name.Contains("Waterside")) return ExplorationType.Waterside;
        return ExplorationType.Quick;
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
