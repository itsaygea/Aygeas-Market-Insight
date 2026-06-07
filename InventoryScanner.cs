using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Inventory;
using Dalamud.Plugin.Services;

namespace AygeaMarketInsight;

/// <summary>
/// Lightweight inventory scanner focused on tracking quantities of crafting-relevant items.
/// Designed to be disabled by default and manually activated to minimize performance impact.
/// </summary>
public sealed class InventoryScanner : IDisposable
{
    private readonly IPluginLog log;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    
    // Inventory scanning state
    private bool isEnabled = false;
    private bool isScanning = false;
    private DateTime lastScanTime = DateTime.MinValue;
    private readonly TimeSpan scanCacheTtl = TimeSpan.FromSeconds(60); // Cache results for 60 seconds
    
    // Item ID -> quantity mapping (lightweight, only stores what we need)
    private readonly ConcurrentDictionary<uint, ushort> itemQuantities = new();
    
    // Track which items we're interested in to optimize scanning
    private readonly HashSet<uint> trackedItemIds = new();
    private readonly object trackedLock = new();
    
    // Cancellation for progressive scanning
    private CancellationTokenSource? scanCts;
    
    public InventoryScanner(IPluginLog log, IClientState clientState, IDataManager dataManager, IFramework framework)
    {
        this.log = log;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.framework = framework;
        
        // Subscribe to inventory update events for optional real-time updates
        clientState.InventoryUpdate += OnInventoryUpdate;
    }
    
    /// <summary>
    /// Enable or disable the inventory scanner.
    /// When disabled, all scanning stops and cached data is cleared.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (isEnabled == enabled) return;
        
        isEnabled = enabled;
        
        if (!enabled)
        {
            // Disable scanning and clear data when turned off
            StopScanning();
            ClearData();
        }
        else
        {
            // When enabling, do an initial scan if we have tracked items
            if (HasTrackedItems)
            {
                RequestScan();
            }
        }
        
        log.Information($"Inventory scanner {(enabled ? "enabled" : "disabled")}");
    }
    
    /// <summary>
    /// Add items to track for inventory scanning.
    /// Only these items will be scanned to minimize overhead.
    /// </summary>
    public void TrackItems(IEnumerable<uint> itemIds)
    {
        if (itemIds == null) return;
        
        lock (trackedLock)
        {
            foreach (var itemId in itemIds)
            {
                if (itemId != 0)
                {
                    trackedItemIds.Add(itemId);
                }
            }
        }
        
        // If we're enabled and now have items to track, request a scan
        if (isEnabled && HasTrackedItems && !isScanning)
        {
            RequestScan();
        }
    }
    
    /// <summary>
    /// Remove items from tracking.
    /// </summary>
    public void UntrackItems(IEnumerable<uint> itemIds)
    {
        if (itemIds == null) return;
        
        lock (trackedLock)
        {
            foreach (var itemId in itemIds)
            {
                trackedItemIds.Remove(itemId);
            }
        }
        
        // Clear quantities for untracked items to free memory
        foreach (var itemId in itemIds)
        {
            itemQuantities.TryRemove(itemId, out _);
        }
    }
    
    /// <summary>
    /// Get the quantity of an item in inventory (0 if not tracked or not found).
    /// </summary>
    public ushort GetItemQuantity(uint itemId)
    {
        if (itemId == 0) return 0;
        return itemQuantities.TryGetValue(itemId, out var quantity) ? quantity : (ushort)0;
    }
    
    /// <summary>
    /// Check if we have at least the required quantity of an item.
    /// </summary>
    public bool HasItemQuantity(uint itemId, ushort requiredQuantity)
    {
        return GetItemQuantity(itemId) >= requiredQuantity;
    }
    
    /// <summary>
    /// Request an inventory scan (non-blocking).
    /// If a scan is already in progress, this will be ignored unless force is true.
    /// </summary>
    public void RequestScan(bool force = false)
    {
        if (!isEnabled) return;
        
        // Don't scan too frequently unless forced
        if (!force && (DateTime.UtcNow - lastScanTime) < TimeSpan.FromSeconds(5))
        {
            return;
        }
        
        // Don't start a new scan if one is already running (unless forcing)
        if (!force && isScanning) 
        {
            return;
        }
        
        // If we have nothing to track, don't scan
        if (!HasTrackedItems)
        {
            return;
        }
        
        // Start the scan
        StartScan();
    }
    
    /// <summary>
    /// Get a snapshot of all tracked item quantities.
    /// Returns a copy to avoid modification during enumeration.
    /// </summary>
    public IReadOnlyDictionary<uint, ushort> GetAllQuantities()
    {
        // Return a copy to prevent external modification of our internal dictionary
        var copy = new Dictionary<uint, ushort>(itemQuantities.Count);
        foreach (var kvp in itemQuantities)
        {
            copy[kvp.Key] = kvp.Value;
        }
        return copy;
    }
    
    // Private implementation methods
    
    private bool HasTrackedItems
    {
        get
        {
            lock (trackedLock)
            {
                return trackedItemIds.Count > 0;
            }
        }
    }
    
    private void StopScanning()
    {
        scanCts?.Cancel();
        scanCts?.Dispose();
        scanCts = null;
        isScanning = false;
    }
    
    private void ClearData()
    {
        itemQuantities.Clear();
        lock (trackedLock)
        {
            trackedItemIds.Clear();
        }
        lastScanTime = DateTime.MinValue;
    }
    
    private void StartScan()
    {
        if (isScanning) return;
        
        isScanning = true;
        lastScanTime = DateTime.UtcNow;
        
        // Create new cancellation token for this scan
        scanCts?.Dispose();
        scanCts = new CancellationTokenSource();
        var token = scanCts.Token;
        
        // Run scan on background thread to avoid blocking UI
        _ = Task.Run(() => PerformScan(token), token)
            .ContinueWith(t =>
            {
                // Handle completion or cancellation
                isScanning = false;
                scanCts?.Dispose();
                scanCts = null;
                
                if (t.IsCanceled)
                {
                    log.Debug("Inventory scan was canceled");
                }
                else if (t.IsFaulted)
                {
                    log.Error(t.Exception, "Inventory scan failed");
                }
                else
                {
                    log.Debug($"Inventory scan completed at {lastScanTime:HH:mm:ss}");
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }
    
    private void PerformScan(CancellationToken token)
    {
        try
        {
            // Clear previous quantities before scanning
            itemQuantities.Clear();
            
            // Get the items we need to track
            HashSet<uint> itemsToTrack;
            lock (trackedLock)
            {
                itemsToTrack = new HashSet<uint>(trackedItemIds);
            }
            
            if (itemsToTrack.Count == 0) 
            {
                return;
            }
            
            // Scan player inventory (main focus)
            ScanPlayerInventory(itemsToTrack, token);
            
            // Optionally scan retainers (only those recently accessed to minimize API calls)
            if (!token.IsCancellationRequested)
            {
                ScanRetainerInventories(itemsToTrack, token);
            }
            
            log.Debug($"Inventory scan finished. Tracked {itemsToTrack.Count} items, found quantities for {itemQuantities.Count} items.");
        }
        catch (OperationCanceledException)
        {
            // Expected when scan is canceled
            throw;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error during inventory scan");
            throw;
        }
    }
    
    private void ScanPlayerInventory(HashSet<uint> itemsToTrack, CancellationToken token)
    {
        // Progressive scanning: process inventory in small batches to prevent hitches
        const int batchSize = 20; // Process 20 slots at a time
        
        var inventory = clientState.Inventory;
        if (inventory == null) return;
        
        // Get all inventory containers (main inventory + satchels)
        var containers = new[]
        {
            inventory.MainInventory,
            inventory.Satchel1,
            inventory.Satchel2,
            inventory.Satchel3,
            inventory.Satchel4
        };
        
        int processedSlots = 0;
        
        foreach (var container in containers)
        {
            if (container == null) continue;
            
            // Process this container in batches
            for (int i = 0; i < container.Size; i += batchSize)
            {
                token.ThrowIfCancellationRequested();
                
                int endIndex = Math.Min(i + batchSize, container.Size);
                
                for (int j = i; j < endIndex; j++)
                {
                    if (!container.TryGetItem(j, out var item)) continue;
                    
                    // Only track items we're interested in
                    if (itemsToTrack.Contains(item.DataId))
                    {
                        // Add to existing quantity (stacks across slots)
                        itemQuantities.AddOrUpdate(
                            item.DataId,
                            (ushort)item.Quantity,
                            (id, oldQty) => (ushort)Math.Min(ushort.MaxValue, oldQty + item.Quantity)
                        );
                    }
                }
                
                processedSlots += (endIndex - i);
                
                // Yield control periodically to prevent hitches
                if (processedSlots % 100 == 0)
                {
                    Thread.Sleep(1); // Sleep for 1ms to yield to UI thread
                }
            }
        }
    }
    
    private void ScanRetainerInventories(HashSet<uint> itemsToTrack, CancellationToken token)
    {
        // For now, we'll keep retainer scanning simple to avoid complexity
        // In a full implementation, we would:
        // 1. Only scan retainers the player has opened recently
        // 2. Use progressive scanning similar to player inventory
        // 3. Handle retainer visitation/api calls appropriately
        
        // Placeholder for retainer scanning - to be implemented based on specific needs
        // log.Debug("Retainer scanning not yet implemented in lightweight scanner");
    }
    
    private void OnInventoryUpdate(object? sender, InventoryUpdateEventArgs e)
    {
        // Optional: Handle inventory updates for real-time scanning
        // This would be configurable to avoid excessive scanning
        // For now, we rely on manual scanning to keep performance predictable
    }
    
    public void Dispose()
    {
        // Unsubscribe from events
        clientState.InventoryUpdate -= OnInventoryUpdate;
        
        // Stop any ongoing scan
        StopScanning();
        
        // Clear data
        ClearData();
    }
}