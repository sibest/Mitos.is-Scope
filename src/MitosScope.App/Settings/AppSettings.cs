using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MitosScope.App.Settings;

public sealed class AppSettings
{
    /// <summary>
    /// Executable names of the game client (without ".exe"). When a process with one of these
    /// names starts or exits, the API ports are scanned. Empty = scan periodically instead.
    /// </summary>
    public List<string> GameProcessNames { get; set; } = ["Mitosis"];

    /// <summary>Position/size of each panel, keyed by panel id.</summary>
    public Dictionary<string, PanelLayout> Panels { get; set; } = [];

    /// <summary>Inventory window: ids of the categories the player collapsed (ids, not names: names follow the game's language).</summary>
    public List<int> CollapsedInventoryCategories { get; set; } = [];

    /// <summary>Game executable of the last client seen (from its window), used by tray "Open Instance".</summary>
    public string? GamePath { get; set; }

    /// <summary>Item sets made in the inventory window, per account id (they live only here, not on the game server).</summary>
    public Dictionary<string, List<MitosScope.Core.Items.ItemSet>> ItemSets { get; set; } = [];

    /// <summary>Shop build tool: the attributes the player picked last time, and its options.</summary>
    public List<string> BuildAttrs { get; set; } = [];
    public bool BuildMyLevel { get; set; } = true;
    public bool BuildMyCoins { get; set; }
    public bool BuildUseOwned { get; set; } = true;

    /// <summary>Shop hotkeys: the 5 keys that buy an item (shop category 6) with one press. Missing entries = empty slots.</summary>
    public List<ShopHotkey> ShopHotkeys { get; set; } = [];
}

/// <summary>
/// One shop hotkey slot. The key is a SharpHook key code name ("VcF1", "Vc1"…) plus modifiers.
/// The item's name, sprite and price are a copy from the shop, so the panel can draw the slot
/// without loading the shop; they're refreshed every time the hotkeys window loads the items.
/// </summary>
public sealed class ShopHotkey
{
    public string? Key { get; set; }
    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }

    public long? ItemId { get; set; }
    public string? ItemName { get; set; }
    public string? ItemSprite { get; set; }
    public long ItemPrice { get; set; }
    public long ItemPack { get; set; }
}

public sealed class PanelLayout
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    /// <summary>Master switch (tray checkbox / ✕ button in edit mode).</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Show the panel while the player is in the menu (not in a match, dead, replay, no client).</summary>
    public bool ShowInMenu { get; set; } = true;

    /// <summary>Show the panel while the player is in a match (game.in_game).</summary>
    public bool ShowInMatch { get; set; } = true;

    // ---- place inside the game window (see OverlayManager.AttachToGameWindow) ----
    // The panel's edge nearest to a side of the game window is kept at the same percentage of the
    // window (GameFraction, 0..1) plus a distance in DIPs (GameOffset, used by the default layout
    // to stack panels in a column). The game's own UI scales with its window, so percentages keep
    // the panels in the same free spot on every instance, whatever its size.

    /// <summary>False until the panel has been placed against a game window once.</summary>
    public bool HasGameOffset { get; set; }

    /// <summary>The panel's right edge is the reference (else its left edge).</summary>
    public bool GameAnchorRight { get; set; }

    /// <summary>The panel's bottom edge is the reference (else its top edge).</summary>
    public bool GameAnchorBottom { get; set; }

    /// <summary>Where the reference edge is, as a fraction of the game window's width (0 = left side, 1 = right side).</summary>
    public double? GameFractionX { get; set; }

    /// <summary>Where the reference edge is, as a fraction of the game window's height (0 = top, 1 = bottom).</summary>
    public double? GameFractionY { get; set; }

    /// <summary>Extra distance in DIPs from that point, towards the inside of the panel's column.</summary>
    public double GameOffsetX { get; set; }

    /// <summary>Extra distance in DIPs from that point, downwards (upwards when anchored to the bottom).</summary>
    public double GameOffsetY { get; set; }
}

/// <summary>Reads/writes %APPDATA%\MitosScope\settings.json.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DirectoryPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MitosScope");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"[MitosScope] Failed to read settings, using defaults: {ex.Message}");
        }

        var settings = new AppSettings();
        Save(settings); // create the file so the user can find and edit it
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"[MitosScope] Failed to save settings: {ex.Message}");
        }
    }
}
