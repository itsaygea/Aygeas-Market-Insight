# Aygea's Market Insight — Claude Code Context

## What this plugin does

A Dalamud plugin for Final Fantasy XIV that helps crafters identify profitable recipes. Three integrated features:

1. **Tooltip Augmentation** — When hovering a craftable item, appends a section showing craft cost, market board price, and profit/loss with color coding.
2. **Profit Scanner** (`/ami`) — A sortable, filterable window listing all recipes with their craft cost, MB sell price, profit, and margin %. Supports job filters, iLvl filters, min profit threshold, and HQ-only toggle.
3. **Shopping List** (`/ami list`) — A pinnable overlay where recipes can be pinned. Shows full ingredient breakdown with "max price" guidance (the highest you can pay for an ingredient while remaining profitable). Supports clipboard copy and optional Artisan integration.

Commands: `/ami` (scanner), `/ami list` (shopping list), `/ami config` (settings).

## Architecture overview

| File | Responsibility |
|------|---------------|
| `Plugin.cs` | `IDalamudPlugin` implementation. Constructor injection of all Dalamud services. Command registration, event wiring, WindowSystem setup, lifecycle management. |
| `Configuration.cs` | `IPluginConfiguration` — all user settings across 4 tabs plus `List<ShoppingListEntry>` for persisted shopping list state. |
| `RecipeCache.cs` | One-time init from Lumina `Recipe`, `Item`, `GilShopItem` sheets. Builds `itemId→recipes` lookup, `recipeId→recipe` map, `itemId→vendorPrice` table. Calculates total material cost for a recipe. |
| `PriceCache.cs` | `ConcurrentDictionary<uint, CachedPrice>` with TTL-based expiry. Tracks pending fetches to avoid duplicate requests. Thread-safe. |
| `UniversalisClient.cs` | `HttpClient` wrapper for Universalis REST API v2. Batches up to 100 item IDs per request. Rate-limited with `SemaphoreSlim(8)`. All async, never called on game thread. |
| `ArtisanIpc.cs` | Optional IPC bridge to Artisan plugin. Uses `GetIpcSubscriber<...>("Artisan.<Name>")`. Auto-detects availability, silently hides UI if unavailable. |
| `UI/PluginUI.cs` | Owns `WindowSystem` instance and all window references. Dispatches `Draw()` from `UiBuilder.Draw`. |
| `UI/TooltipHook.cs` | Subscribes `IGameGui.HoveredItemChanged`. In the draw loop, uses `ImRaii.Tooltip()` to render craft cost vs MB price overlay on craftable items. |
| `UI/ConfigWindow.cs` | 4-tab ImGui settings window: General, Profit Scanner, Shopping List, About. |
| `UI/ProfitScannerWindow.cs` | Sortable recipe table with filters (job, iLvl, profit threshold, HQ). Right-click context menu for shopping list and Artisan. |
| `UI/ShoppingListWindow.cs` | Pinnable overlay with ingredient table, max-price guidance, red highlighting for over-budget items, clipboard copy. |

## Price data flow

```
Layer 1 — IMarketBoard events (live, game thread):
  OfferingsReceived / ItemPurchased → PriceCache.Set (TTL 30 min, source = MB)

Layer 2 — Universalis REST API v2 (fallback):
  GET /api/v2/{worldOrDc}/{itemIds}?listings=1&fields=items.minPrice,minPriceNQ,minPriceHQ
  Batch up to 100 IDs, TTL 20 min, rate-limited to 8 concurrent connections

Vendor prices (static):
  From Lumina GilShopItem sheet at init → Dictionary<uint, uint> itemId→gilCost
  Always used as floor price. Never expire.

Cheapest ingredient source = min(MB lowest price, vendor price) per ingredient.
```

## Key Dalamud APIs used

- **IMarketBoard** — `OfferingsReceived`, `ItemPurchased` events for live price data
- **IGameGui** — `HoveredItem` property, `HoveredItemChanged` event for tooltip detection
- **IDataManager / Lumina** — `GetExcelSheet<Recipe>()`, `GetExcelSheet<Item>()`, `GetExcelSheet<GilShopItem>()` for game data
- **IFramework** — `RunOnFrameworkThread(Action)` for dispatching async results to game thread
- **WindowSystem** — `Dalamud.Interface.Windowing.WindowSystem` + `Window` base class for all UI windows
- **IDalamudPluginInterface** — Config save/load, `UiBuilder.Draw` event, `OpenConfigUi` hook, IPC access
- **IPluginLog** — Structured logging (Fatal/Error/Warning/Information/Debug/Verbose)
- **IClientState** — `LocalPlayer?.HomeWorld` for world detection
- **ICommandManager** — `/ami` slash command registration
- **Util.OpenLink** — Opening URLs from About tab buttons

## Artisan IPC

Artisan integration is **optional** and auto-detected. Detection logic:

1. On plugin load and on `ActivePluginsChanged`, attempt `GetIpcSubscriber<bool>("Artisan.IsBusy")`
2. If the subscriber exists and `InvokeFunc()` succeeds, set `ArtisanAvailable = true`
3. If unavailable or throws, set `ArtisanAvailable = false`, hide all Artisan UI silently

IPC methods consumed:
- `"Artisan.CraftItem"` — `(ushort recipeId, int amount) → void`
- `"Artisan.IsBusy"` — `() → bool`
- `"Artisan.GetLists"` — `() → Dictionary<int, string>`

If Artisan IPC method names change between versions, check `Artisan/IPC/IPC.cs` on GitHub: https://github.com/PunishXIV/Artisan/blob/main/Artisan/IPC/IPC.cs

## Commands

| Command | Action |
|---------|--------|
| `/ami` | Toggle Profit Scanner window |
| `/ami list` | Toggle Shopping List window |
| `/ami config` | Toggle Settings window |

The gear icon in the Dalamud plugin list also opens the Settings window via `UiBuilder.OpenConfigUi`.

## Lumina Recipe access pattern

```csharp
recipe.Ingredients()                           // → enumerable of { .Item, .Amount }
recipe.ItemResult                              // → RowRef<Item>
recipe.ItemResult.RowId                        // → uint
recipe.ItemResult.Value.Name                   // → SeString
recipe.AmountResult                            // → int (yield per craft)
recipe.RecipeLevelTable.Value.ClassJobLevel    // → int
recipe.CraftType                               // → craft type ref
recipe.CraftType.Value.Name                    // → craft type name
```

Filter ingredients: `.Where(x => x.Amount > 0 && x.Item.RowId != 0)`

## Build instructions

```
dotnet build
```

Output goes to `bin/Debug/` (TFM set by Dalamud.NET.Sdk).
Install as dev plugin: `/xlsettings → Experimental → Dev Plugin Locations` → point to output directory.

## Branding & links

- Author: Aygea
- Twitch: https://twitch.tv/crazyaygea
- Website: https://itsaygea.com
- Ko-fi: https://ko-fi.com/aygea
- GitHub: https://github.com/itsaygea/Aygeas-Market-Insight

## Things to know / gotchas

- **Dalamud.NET.Sdk** handles TFM targeting — do not manually set `TargetFramework`
- **Artisan IPC** method names may change between Artisan versions — check `Artisan/IPC/IPC.cs` on GitHub if IPC stops working
- **IMarketBoard** events only fire when the player actively browses the MB; Universalis is the fallback for everything else
- **Never await async calls on the framework/game thread** — use `Task.Run` + `IFramework.RunOnFrameworkThread`
- **Shopping list** is serialized in Configuration.json — changes to `ShoppingListEntry` shape require a migration or version bump
- **HQ item IDs**: `HoveredItem` returns values > 1,000,000 for HQ. Use `rawId % 500000` for base ID
- **ImGui tooltips** use `ImRaii.Tooltip()` — `TooltipActionDelegate` does not exist in current Dalamud
- **`recipe.Ingredients()`** is the current access pattern; avoid deprecated `UnkData5` / `MaterialIngredient`

## Release Process

1. Make changes and push to `main`
2. CI build workflow runs automatically to verify the build passes
3. When ready to release, bump the version in `AygeaMarketInsight.csproj` (the `<Version>` property, e.g. `1.0.1`)
4. Commit and push: `git commit -m "chore: bump version to 1.0.1"`
5. Tag the commit: `git tag v1.0.1`
6. Push the tag: `git push --tags`
7. GitHub Actions release workflow fires automatically, builds in Release mode, and creates a GitHub Release with `latest.zip` attached
8. Users with the custom repo added get the update automatically via Dalamud's plugin updater

## Custom Repository

The public `repo.json` is at: `itsaygea/DalamudPlugins`
User-facing install URL: `https://raw.githubusercontent.com/itsaygea/DalamudPlugins/main/repo.json`
