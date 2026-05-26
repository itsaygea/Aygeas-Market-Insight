# Aygea's Market Insight

**Made by Aygea** — [twitch.tv/crazyaygea](https://twitch.tv/crazyaygea)

A crafting profit and market price tool for **Final Fantasy XIV**. Compare craft costs against market board prices, scan for the most profitable recipes, and build shopping lists with max-price guidance.

## Features

- **Tooltip Augmentation** — Hover any craftable item to see craft cost, MB price, and profit/loss with color coding
- **Profit Scanner** — Sortable, filterable recipe browser with job filters, iLvl thresholds, and min profit settings
- **Shopping List** — Pin recipes to a persistent overlay with full ingredient breakdown and max-price guidance (the most you can pay per ingredient while staying profitable)
- **Artisan Integration** — Optional auto-detected IPC integration with Artisan. If Artisan is installed, "Add to Artisan" buttons appear automatically

## Commands

| Command | Description |
|---------|-------------|
| `/ami` | Open/close the Profit Scanner |
| `/ami list` | Open/close the Shopping List |
| `/ami config` | Open/close Settings |

## Installation

### Custom Plugin Repository (recommended)

Add the following URL to your Dalamud plugin repositories in `/xlsettings → Experimental`:

```
https://raw.githubusercontent.com/itsaygea/Aygea-Market-Insight/main/repo.json
```

### Dev Install

1. Build the plugin (see below)
2. Open `/xlsettings → Experimental → Dev Plugin Locations`
3. Add the output directory (`bin/Debug/`) as a dev plugin path
4. The plugin will load on next Dalamud init

## Build Instructions

Requirements:
- .NET 10 SDK
- Dalamud (via XIVLauncher)

```
git clone https://github.com/itsaygea/Aygea-Market-Insight.git
cd Aygea-Market-Insight
dotnet build
```

## Credits

- **[Universalis.app](https://universalis.app)** — Market board price data via their public REST API
- **Dalamud Plugin Community** — Plugin framework, API, and tooling
- **[Artisan](https://github.com/PunishXIV/Artisan)** — Optional IPC integration for crafting list management

## Links

[![Twitch](https://img.shields.io/badge/Twitch-crazyaygea-9146FF?logo=twitch&logoColor=white)](https://twitch.tv/crazyaygea)
[![Website](https://img.shields.io/badge/Website-itsaygea.com-00b4b4?logo=globe)](https://itsaygea.com)
[![Ko-fi](https://img.shields.io/badge/Ko--fi-Support-FF5E5B?logo=kofi&logoColor=white)](https://ko-fi.com/aygea)
[![GitHub](https://img.shields.io/badge/GitHub-Report%20a%20Bug-333333?logo=github&logoColor=white)](https://github.com/itsaygea/Aygea-Market-Insight/issues)
