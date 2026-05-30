using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Plugin.Ipc;

namespace AygeaMarketInsight;

public sealed class ArtisanIpc
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private bool warningLogged;

    private ICallGateSubscriber<bool>? isBusySubscriber;
    private ICallGateSubscriber<ushort, int, object>? craftItemSubscriber;
    private ICallGateSubscriber<Dictionary<int, string>>? getListsSubscriber;

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
            isBusySubscriber = pluginInterface.GetIpcSubscriber<bool>("Artisan.IsBusy");
            isBusySubscriber.InvokeFunc();
            craftItemSubscriber = pluginInterface.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem");
            getListsSubscriber = pluginInterface.GetIpcSubscriber<Dictionary<int, string>>("Artisan.GetLists");
            Available = true;
            log.Information("Artisan IPC detected and available");
        }
        catch
        {
            Available = false;
            isBusySubscriber = null;
            craftItemSubscriber = null;
            getListsSubscriber = null;
        }
    }

    public bool IsBusy()
    {
        if (!Available || isBusySubscriber == null) return false;
        try
        {
            return isBusySubscriber.InvokeFunc();
        }
        catch (Exception ex)
        {
            OnFailure(ex);
            return false;
        }
    }

    public void CraftItem(ushort recipeId, int amount)
    {
        if (!Available || craftItemSubscriber == null) return;
        try
        {
            craftItemSubscriber.InvokeAction(recipeId, amount);
        }
        catch (Exception ex)
        {
            OnFailure(ex);
        }
    }

    public Dictionary<int, string>? GetLists()
    {
        if (!Available || getListsSubscriber == null) return null;
        try
        {
            return getListsSubscriber.InvokeFunc();
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
