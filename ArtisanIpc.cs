using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AygeaMarketInsight;

public sealed class ArtisanIpc
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private bool warningLogged;

    public bool Available { get; private set; }

    public ArtisanIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        Detect();
    }

    public void Detect()
    {
        try
        {
            // Artisan.IsBusy — IPC method name: "Artisan.IsBusy"
            var subscriber = pluginInterface.GetIpcSubscriber<bool>("Artisan.IsBusy");
            subscriber.InvokeFunc();
            Available = true;
            log.Information("Artisan IPC detected and available");
        }
        catch
        {
            Available = false;
        }
    }

    public bool IsBusy()
    {
        if (!Available) return false;
        try
        {
            // IPC method: "Artisan.IsBusy" → bool()
            return pluginInterface.GetIpcSubscriber<bool>("Artisan.IsBusy").InvokeFunc();
        }
        catch (Exception ex)
        {
            OnFailure(ex);
            return false;
        }
    }

    public void CraftItem(ushort recipeId, int amount)
    {
        if (!Available) return;
        try
        {
            // IPC method: "Artisan.CraftItem" → void(ushort recipeId, int amount)
            pluginInterface.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem")
                .InvokeAction(recipeId, amount);
        }
        catch (Exception ex)
        {
            OnFailure(ex);
        }
    }

    public Dictionary<int, string>? GetLists()
    {
        if (!Available) return null;
        try
        {
            // IPC method: "Artisan.GetLists" → Dictionary<int, string>()
            return pluginInterface.GetIpcSubscriber<Dictionary<int, string>>("Artisan.GetLists")
                .InvokeFunc();
        }
        catch (Exception ex)
        {
            OnFailure(ex);
            return null;
        }
    }

    private void OnFailure(Exception ex)
    {
        Available = false;
        if (!warningLogged)
        {
            log.Warning($"Artisan IPC call failed, marking unavailable: {ex.Message}");
            warningLogged = true;
        }
    }
}
