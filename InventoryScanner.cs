using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AygeaMarketInsight;

public sealed unsafe class InventoryScanner : IDisposable
{
    private readonly IPluginLog log;
    private readonly IFramework framework;

    private bool isEnabled;
    private bool isScanning;
    private DateTime lastScanTime = DateTime.MinValue;
    private readonly TimeSpan minScanInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<uint, uint> itemQuantities = new();
    private readonly HashSet<uint> trackedItemIds = [];
    private readonly object trackedLock = new();

    // Player inventory containers
    private static readonly InventoryType[] PlayerContainers =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    // Retainer containers (only populated while retainer bell is open)
    private static readonly InventoryType[] RetainerContainers =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    public InventoryScanner(IPluginLog log, IClientState clientState, IDataManager dataManager, IFramework framework)
    {
        this.log = log;
        this.framework = framework;
    }

    public void SetEnabled(bool enabled)
    {
        if (isEnabled == enabled) return;
        isEnabled = enabled;

        if (!enabled)
        {
            itemQuantities.Clear();
            log.Information("Inventory scanner disabled");
        }
        else
        {
            log.Information("Inventory scanner enabled");
            if (trackedItemIds.Count > 0)
                RequestScan();
        }
    }

    public bool IsEnabled => isEnabled;

    public void TrackItems(IEnumerable<uint> itemIds)
    {
        lock (trackedLock)
        {
            foreach (var id in itemIds)
                if (id != 0) trackedItemIds.Add(id);
        }

        if (isEnabled && trackedItemIds.Count > 0)
            RequestScan();
    }

    public void UntrackItems(IEnumerable<uint> itemIds)
    {
        lock (trackedLock)
        {
            foreach (var id in itemIds)
                trackedItemIds.Remove(id);
        }
    }

    public uint GetItemQuantity(uint itemId)
    {
        if (itemId == 0) return 0;
        return itemQuantities.TryGetValue(itemId, out var qty) ? qty : 0;
    }

    public bool HasItemQuantity(uint itemId, uint requiredQuantity)
    {
        return GetItemQuantity(itemId) >= requiredQuantity;
    }

    public void RequestScan(bool force = false)
    {
        if (!isEnabled || isScanning) return;
        if (!force && (DateTime.UtcNow - lastScanTime) < minScanInterval) return;

        isScanning = true;
        lastScanTime = DateTime.UtcNow;
        framework.RunOnFrameworkThread(() =>
        {
            PerformScan();
            isScanning = false;
        });
    }

    public IReadOnlyDictionary<uint, uint> GetAllQuantities()
    {
        var copy = new Dictionary<uint, uint>(itemQuantities.Count);
        foreach (var kvp in itemQuantities)
            copy[kvp.Key] = kvp.Value;
        return copy;
    }

    private void PerformScan()
    {
        HashSet<uint> itemsToTrack;
        lock (trackedLock)
        {
            itemsToTrack = new HashSet<uint>(trackedItemIds);
        }

        if (itemsToTrack.Count == 0) return;

        itemQuantities.Clear();

        var invManager = InventoryManager.Instance();
        if (invManager == null) return;

        ScanContainers(invManager, PlayerContainers, itemsToTrack);
        ScanContainers(invManager, RetainerContainers, itemsToTrack);
    }

    private void ScanContainers(InventoryManager* invManager, InventoryType[] containers, HashSet<uint> itemsToTrack)
    {
        foreach (var type in containers)
        {
            var container = invManager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;

            for (int i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null) continue;

                var itemId = item->ItemId;
                if (itemId == 0) continue;

                if (itemsToTrack.Contains(itemId))
                {
                    var qty = (uint)item->Quantity;
                    itemQuantities.AddOrUpdate(itemId, qty, (_, old) => old + qty);
                }
            }
        }
    }

    public void Dispose()
    {
        isEnabled = false;
        itemQuantities.Clear();
        lock (trackedLock) trackedItemIds.Clear();
    }
}
