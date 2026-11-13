# MitosScope

An overlay for the **Mitos.is** desktop client that shows your match, account, quests, battle pass and equipment on top of the game, for every account you have open.

MitosScope only reads data through the game client's local addon API. It never changes anything in the game.

Created by **Chains** ([SwapChains](https://github.com/SwapChains)).

## Features

- **Live match data** (refreshed every second): mass and peak mass, cells, leaderboard position, match XP, players and food eaten, plus how long you've been alive.
- **Last match summary**: when you die, the Match panel switches to your previous match's highest mass, best position, XP and totals.
- **Account data** (refreshed every 60 s): level, coins, rating, guild, quests, battle pass, equipment durability and all-time stats.
- **Several accounts at once**: connects to up to 20 open clients, and the panels follow the game window you focus.
- **Free layout**: each panel is its own window that you can move, resize, turn off, or set to show only in the menu or only during a match.
- **Click-through**: once the layout is locked, your clicks go straight to the game.

## Panels

| Panel | Shows | Refresh |
|---|---|---|
| **MitosScope** | Connected game clients and their connection status | Live |
| **Match** | Alive, room, type, mode, mass (current / peak), cells, leaderboard, match XP, players eaten, food eaten. Shows the last match while dead. | Every 1 s |
| **Account** | Level and XP, coins, rating (MMR and rank), guild, server | Every 60 s |
| **Quests** | Open quests with progress and reward | Every 60 s |
| **Battle Pass** | Tier and expiry (premium only), type, current level and XP, next level | Every 60 s |
| **Equipment** | Equipped items with durability | Every 60 s |
| **All Time Stats** | Time played, max score, kills, splits, ranked wins / losses | Every 60 s |

## Requirements

- Windows 10 or 11
- The Mitos.is desktop client, logged in
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source

## Building from source

1. **Add the app key.** Place `API_KEY.txt` (one line with the addon's app key) in the repository root. The file is git-ignored and never committed; ask a maintainer for it. The build copies it next to the executable, and without it the app shows *"API_KEY.txt not found next to the executable."*
2. **Build and run:**

   ```
   dotnet build
   dotnet run --project src/MitosScope.App
   ```

3. **Publish a release build** (optional):

   ```
   dotnet publish src/MitosScope.App -c Release
   ```

## Usage

1. Run MitosScope. It has no main window: it lives in the system tray.
2. Open Mitos.is and log in. When the game asks whether to allow **MitosScope**, click **Allow**. The popup only appears in the menu, never during a match.
3. On the first launch the panels open in **edit mode**: drag and resize them, then press **Ctrl+Shift+E** to lock the layout.

The overlay is visible only while a game window is focused. **Ctrl+Shift+E** toggles edit mode from anywhere, even while the game has focus.

### Tray menu

Right-click the tray icon (a left-click toggles edit mode):

- **Edit mode**: same as Ctrl+Shift+E.
- **Show overlay**: hides or shows all panels at once.
- **Panels**: one submenu per panel:
  - **Enabled**: turns the panel on or off.
  - **Show in menu** / **Show in match**: show the panel only in the menu (including while dead), only during a match, or both.
  - In edit mode every enabled panel is shown, so you can arrange them all.
- **Open settings folder**: opens `%APPDATA%\MitosScope`.
- **Exit**

### Settings

The layout and panel options are saved in `%APPDATA%\MitosScope\settings.json`. You can also set `gameProcessNames` there, the executable names of the game client (default `Mitosis`), which MitosScope watches to detect when the game starts.

## Troubleshooting

| You see | What to do |
|---|---|
| "Looking for the game…" | Open Mitos.is and log in. MitosScope only connects to logged-in clients. |
| "Instance without API (PID …)" | That client is running but its addon API is off. Log in; if it's already logged in, restart that client. |
| No popup in the game | Go back to the main menu. If you clicked **Deny** before, restart the game. |
| Panels are missing | Focus a game window, then check **Show overlay** and the panel's options under **Panels** in the tray. |
| A panel is off-screen | Turn on edit mode. Panels saved on a monitor that's no longer connected return to their default position. |
| Can't click the game through a panel | Edit mode is on. Press Ctrl+Shift+E to lock. |

## Project structure

```
src/
├── MitosScope.Core/          Game API client and session logic (no UI)
│   ├── Api/                  HTTP client and response models for the addon API
│   ├── Discovery/            Finding running game clients and their API ports
│   ├── Sessions/             One session per client: auth, polling, change events
│   └── Stats/                Match tracking (peak mass, best position, last match)
└── MitosScope.App/           Avalonia overlay app
    ├── Overlay/              Panel windows, visibility rules, focus and match state
    ├── Services/             Hotkeys, foreground watcher, app key, item sprites
    ├── Settings/             settings.json model and storage
    ├── ViewModels/           Session and panel view models
    └── Views/Panels/         One view per panel
```

To add a panel, create its `XxxPanelViewModel` and `Views/Panels/XxxPanelView.axaml`, then register it in `App.CreatePanels()`.

## Built with

[.NET 10](https://dotnet.microsoft.com/), [Avalonia UI](https://avaloniaui.net/), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) and [SharpHook](https://github.com/TolikPylypchuk/SharpHook) (global hotkeys).

## Credits

MitosScope was designed and written by **Chains** ([SwapChains](https://github.com/SwapChains)). This repository is a fork of the original project, maintained for the official Mitos.is releases.
