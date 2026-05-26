using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Game.Network.Structures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using AygeaMarketInsight.UI;

namespace AygeaMarketInsight;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Aygea's Market Insight";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IMarketBoard marketBoard;
    private readonly ICommandManager commandManager;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    private readonly Configuration config;
    private readonly RecipeCache recipeCache;
    private readonly PriceCache priceCache;
    private readonly UniversalisClient universalisClient;
    private readonly ArtisanIpc artisanIpc;
    private readonly PluginUI pluginUI;

    private readonly TooltipHook tooltipHook;

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

        // Config
        config = Configuration.Load(pluginInterface);

        // Data layer
        recipeCache = new RecipeCache(dataManager, log);
        priceCache = new PriceCache();
        universalisClient = new UniversalisClient(log);
        artisanIpc = new ArtisanIpc(pluginInterface, log);

        // UI
        var configWindow = new ConfigWindow(config, artisanIpc, log);
        var scannerWindow = new ProfitScannerWindow(
            config, recipeCache, priceCache, universalisClient, artisanIpc, objectTable, framework, log);
        var shoppingListWindow = new ShoppingListWindow(
            config, recipeCache, priceCache, universalisClient, artisanIpc, notificationManager, framework, log);

        tooltipHook = new TooltipHook(
            gameGui, recipeCache, priceCache, universalisClient, config, objectTable, framework, log);
        var itemDetailPopout = new ItemDetailPopout(recipeCache, priceCache, config);

        pluginUI = new PluginUI(pluginInterface, configWindow, scannerWindow, shoppingListWindow, itemDetailPopout);

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
            HelpMessage = "Usage: /ami [scan|sl|list|config|detail] — Settings (default), Scanner, Shopping List, or Item Detail.",
        });

        log.Information("Aygea's Market Insight loaded");
    }

    private void OnDraw()
    {
        pluginUI.Draw();
        tooltipHook.Draw();

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
        pluginUI.ToggleScannerWindow();
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
            default:
                pluginUI.ToggleConfigWindow();
                break;
        }
    }

    public void Dispose()
    {
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
