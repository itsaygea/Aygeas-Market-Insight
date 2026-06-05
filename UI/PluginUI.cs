using System;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace AygeaMarketInsight.UI;

public sealed class PluginUI : IDisposable
{
    private readonly WindowSystem windowSystem;
    private readonly HubWindow hubWindow;
    private readonly ConfigWindow configWindow;
    private readonly ProfitScannerWindow scannerWindow;
    private readonly ShoppingListWindow shoppingListWindow;
    private readonly ItemDetailPopout itemDetailPopout;
    private readonly RetainerVentureWindow retainerWindow;

    public PluginUI(
        IDalamudPluginInterface pluginInterface,
        HubWindow hubWindow,
        ConfigWindow configWindow,
        ProfitScannerWindow scannerWindow,
        ShoppingListWindow shoppingListWindow,
        ItemDetailPopout itemDetailPopout,
        RetainerVentureWindow retainerWindow)
    {
        this.hubWindow = hubWindow;
        this.configWindow = configWindow;
        this.scannerWindow = scannerWindow;
        this.shoppingListWindow = shoppingListWindow;
        this.itemDetailPopout = itemDetailPopout;
        this.retainerWindow = retainerWindow;

        windowSystem = new WindowSystem("AygeaMarketInsight.Windows");
        windowSystem.AddWindow(hubWindow);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(scannerWindow);
        windowSystem.AddWindow(shoppingListWindow);
        windowSystem.AddWindow(itemDetailPopout);
        windowSystem.AddWindow(retainerWindow);

        hubWindow.OpenScanner = () => scannerWindow.IsOpen = !scannerWindow.IsOpen;
        hubWindow.OpenShoppingList = () => shoppingListWindow.IsOpen = !shoppingListWindow.IsOpen;
        hubWindow.OpenRetainer = () => retainerWindow.IsOpen = !retainerWindow.IsOpen;
        hubWindow.OpenConfig = () => configWindow.IsOpen = !configWindow.IsOpen;

        itemDetailPopout.OnAddToShoppingList = () => shoppingListWindow.IsOpen = true;
        scannerWindow.OnAddToShoppingList = () => shoppingListWindow.IsOpen = true;
        scannerWindow.OnOpenItemDetail = data =>
        {
            itemDetailPopout.SetPinnedData(data);
            itemDetailPopout.IsOpen = true;
        };

        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
    }

    public void ToggleHubWindow() => hubWindow.IsOpen = !hubWindow.IsOpen;
    public void ToggleScannerWindow() => scannerWindow.IsOpen = !scannerWindow.IsOpen;
    public void ToggleShoppingListWindow() => shoppingListWindow.IsOpen = !shoppingListWindow.IsOpen;
    public void ToggleConfigWindow() => configWindow.IsOpen = !configWindow.IsOpen;
    public void ToggleRetainerWindow() => retainerWindow.IsOpen = !retainerWindow.IsOpen;
    public void ToggleItemDetailPopout() => itemDetailPopout.IsOpen = !itemDetailPopout.IsOpen;
    public void OpenItemDetailPopout() => itemDetailPopout.IsOpen = true;
    public void SetPinnedItem(PinnedItemData data) => itemDetailPopout.SetPinnedData(data);

    public void Dispose()
    {
        windowSystem.RemoveWindow(hubWindow);
        windowSystem.RemoveWindow(configWindow);
        windowSystem.RemoveWindow(scannerWindow);
        windowSystem.RemoveWindow(shoppingListWindow);
        windowSystem.RemoveWindow(itemDetailPopout);
        windowSystem.RemoveWindow(retainerWindow);
    }
}
