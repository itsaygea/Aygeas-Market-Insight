using System;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace AygeaMarketInsight.UI;

public sealed class PluginUI : IDisposable
{
    private readonly WindowSystem windowSystem;
    private readonly ConfigWindow configWindow;
    private readonly ProfitScannerWindow scannerWindow;
    private readonly ShoppingListWindow shoppingListWindow;
    private readonly ItemDetailPopout itemDetailPopout;

    public PluginUI(
        IDalamudPluginInterface pluginInterface,
        ConfigWindow configWindow,
        ProfitScannerWindow scannerWindow,
        ShoppingListWindow shoppingListWindow,
        ItemDetailPopout itemDetailPopout)
    {
        this.configWindow = configWindow;
        this.scannerWindow = scannerWindow;
        this.shoppingListWindow = shoppingListWindow;
        this.itemDetailPopout = itemDetailPopout;

        windowSystem = new WindowSystem("AygeaMarketInsight.Windows");
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(scannerWindow);
        windowSystem.AddWindow(shoppingListWindow);
        windowSystem.AddWindow(itemDetailPopout);

        itemDetailPopout.OnAddToShoppingList = () => shoppingListWindow.IsOpen = true;
        scannerWindow.OnAddToShoppingList = () => shoppingListWindow.IsOpen = true;

        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
    }

    public void Draw()
    {
        // WindowSystem.Draw is wired to UiBuilder.Draw in constructor.
    }

    public void ToggleScannerWindow() => scannerWindow.IsOpen = !scannerWindow.IsOpen;
    public void ToggleShoppingListWindow() => shoppingListWindow.IsOpen = !shoppingListWindow.IsOpen;
    public void ToggleConfigWindow() => configWindow.IsOpen = !configWindow.IsOpen;
    public void ToggleItemDetailPopout() => itemDetailPopout.IsOpen = !itemDetailPopout.IsOpen;
    public void OpenItemDetailPopout() => itemDetailPopout.IsOpen = true;
    public void SetPinnedItem(PinnedItemData data) => itemDetailPopout.SetPinnedData(data);

    public void Dispose()
    {
        windowSystem.RemoveWindow(configWindow);
        windowSystem.RemoveWindow(scannerWindow);
        windowSystem.RemoveWindow(shoppingListWindow);
        windowSystem.RemoveWindow(itemDetailPopout);
    }
}
