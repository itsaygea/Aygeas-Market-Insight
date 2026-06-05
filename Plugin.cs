using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Game.Network.Structures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using AygeaMarketInsight.UI;
using Lumina.Excel.Sheets;

namespace AygeaMarketInsight;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Aygea's Market Insight";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IMarketBoard marketBoard;
    private readonly ICommandManager commandManager;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;

    private readonly Configuration config;
    private readonly RecipeCache recipeCache;
    private readonly VentureCache ventureCache;
    private readonly PriceCache priceCache;
    private readonly UniversalisClient universalisClient;
    private readonly ArtisanIpc artisanIpc;
    private readonly PluginUI pluginUI;

    private readonly TooltipHook tooltipHook;
    private readonly string cacheFilePath;
    private DateTime lastCacheSave = DateTime.MinValue;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IGameGui gameGui,
        IMarketBoard marketBoard,
        IDataManager dataManager,
        IObjectTable objectTable,
        IFramework framework,
        IPluginLog log,
        Dalamud.Plugin.Services.INotificationManager notificationManager)
    {
        this.pluginInterface = pluginInterface;
        this.marketBoard = marketBoard;
        this.commandManager = commandManager;
        this.objectTable = objectTable;
        this.log = log;
        this.dataManager = dataManager;
        this.framework = framework;

        // Config
        config = Configuration.Load(pluginInterface);

        // Data layer
        recipeCache = new RecipeCache(dataManager, log);
        ventureCache = new VentureCache(dataManager, log);
        priceCache = new PriceCache();
        cacheFilePath = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "price_cache.json");
        var loaded = priceCache.LoadFromFile(cacheFilePath);
        log.Information($"PriceCache loaded {loaded} cached prices from disk");
        universalisClient = new UniversalisClient(log);
        artisanIpc = new ArtisanIpc(pluginInterface, log);

        // UI
        var configWindow = new ConfigWindow(config, artisanIpc, dataManager, objectTable, log);
        var scannerWindow = new ProfitScannerWindow(
            config, recipeCache, priceCache, universalisClient, artisanIpc, objectTable, dataManager, framework, log);
        var shoppingListWindow = new ShoppingListWindow(
            config, recipeCache, priceCache, universalisClient, artisanIpc, notificationManager, framework, log);

        tooltipHook = new TooltipHook(
            gameGui, recipeCache, priceCache, universalisClient, config, objectTable, framework, log);
        var itemDetailPopout = new ItemDetailPopout(recipeCache, priceCache, universalisClient, config, objectTable, framework, log);

        var retainerWindow = new RetainerVentureWindow(
            config, ventureCache, priceCache, universalisClient, objectTable, framework, log);

        var hubWindow = new HubWindow(config);

        pluginUI = new PluginUI(pluginInterface, hubWindow, configWindow, scannerWindow, shoppingListWindow, itemDetailPopout, retainerWindow);

        // Events
        pluginInterface.UiBuilder.Draw += OnDraw;
        pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;
        pluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;
        marketBoard.OfferingsReceived += OnMarketBoardOfferings;

        // Artisan re-detection
        pluginInterface.ActivePluginsChanged += OnPluginsChanged;

        // Commands
        commandManager.AddHandler("/ami", new CommandInfo(OnAmiCommand)
        {
            HelpMessage = "Usage: /ami [scan|sl|list|config|detail|r] — Hub (default), Scanner, Shopping List, Settings, Item Detail, or Retainer.",
        });

        log.Information("Aygea's Market Insight loaded");
    }

    private void OnDraw()
    {
        tooltipHook.Draw();

        // Periodic cache save (every 5 minutes)
        if ((DateTime.UtcNow - lastCacheSave).TotalMinutes >= 5)
        {
            priceCache.SaveToFile(cacheFilePath);
            lastCacheSave = DateTime.UtcNow;
        }

        // Ctrl+hover pins item details to the popout
        if (tooltipHook.CheckPinRequest() && tooltipHook.CurrentPinnedData != null)
        {
            pluginUI.OpenItemDetailPopout();
            pluginUI.SetPinnedItem(tooltipHook.CurrentPinnedData);
        }
    }

    private void OnOpenConfig()
    {
        pluginUI.ToggleConfigWindow();
    }

    private void OnOpenMainUi()
    {
        pluginUI.ToggleHubWindow();
    }

    private void OnMarketBoardOfferings(IMarketBoardCurrentOfferings offerings)
    {
        var ttl = TimeSpan.FromMinutes(config.MbPriceCacheTtlMinutes);

        foreach (var item in offerings.ItemListings)
        {
            var itemId = item.ItemId;
            var isHq = item.IsHq;

            uint nq = 0, hq = 0;
            var existing = priceCache.Get(itemId);
            if (existing != null)
            {
                nq = existing.NqPrice;
                hq = existing.HqPrice;
            }

            var price = item.PricePerUnit;
            if (isHq)
                hq = price;
            else
                nq = price;

            priceCache.Set(itemId, nq, hq, "MB", ttl);
        }
    }

    private void OnPluginsChanged(IActivePluginsChangedEventArgs args)
    {
        artisanIpc.Detect();
    }

    private void OnAmiCommand(string command, string args)
    {
        var trimmed = args.Trim().ToLowerInvariant();
        switch (trimmed)
        {
            case "scan":
            case "scanner":
                pluginUI.ToggleScannerWindow();
                break;
            case "sl":
            case "list":
            case "shopping":
                pluginUI.ToggleShoppingListWindow();
                break;
            case "config":
            case "settings":
                pluginUI.ToggleConfigWindow();
                break;
            case "detail":
                pluginUI.ToggleItemDetailPopout();
                break;
            case "r":
            case "retainer":
                pluginUI.ToggleRetainerWindow();
                break;
            default:
                pluginUI.ToggleHubWindow();
                break;
        }
    }

    public uint GetWorldId()
    {
        if (config.HomeWorldId > 0) return config.HomeWorldId;
        return objectTable.LocalPlayer?.HomeWorld.RowId ?? 0;
    }

    public string? GetDcName()
    {
        var worldId = GetWorldId();
        if (worldId == 0) return null;

        var worlds = dataManager.GetExcelSheet<World>();
        if (worlds == null) return null;

        foreach (var w in worlds)
        {
            if (w.RowId == worldId)
            {
                var dc = w.DataCenter.Value;
                return dc.Name.ToString();
            }
        }

        return null;
    }

    public void Dispose()
    {
        priceCache.SaveToFile(cacheFilePath);

        pluginInterface.UiBuilder.Draw -= OnDraw;
        pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;
        pluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;

        marketBoard.OfferingsReceived -= OnMarketBoardOfferings;
        pluginInterface.ActivePluginsChanged -= OnPluginsChanged;

        commandManager.RemoveHandler("/ami");
        tooltipHook.Dispose();
        pluginUI.Dispose();
        universalisClient.Dispose();

        config.Save();
    }
}
