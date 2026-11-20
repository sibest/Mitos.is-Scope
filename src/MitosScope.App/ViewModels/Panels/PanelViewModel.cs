using CommunityToolkit.Mvvm.ComponentModel;
using MitosScope.App.Settings;

namespace MitosScope.App.ViewModels.Panels;

/// <summary>
/// Base for every overlay panel. Each panel gets its own window (see Overlay/PanelWindow);
/// its view is resolved by ViewLocator (XxxPanelViewModel → Views/Panels/XxxPanelView).
/// To add a panel: create the ViewModel + View and register it in App.CreatePanels().
/// </summary>
public abstract partial class PanelViewModel(TrackerViewModel tracker) : ViewModelBase
{
    public TrackerViewModel Tracker { get; } = tracker;

    /// <summary>Stable key used to persist the layout.</summary>
    public abstract string Id { get; }

    public abstract string Title { get; }

    public abstract PanelLayout DefaultLayout { get; }

    // ---- default arrangement inside the game window ----------------------------------------
    // The game's UI scales with its window, so the free areas are at the same percentages on every
    // instance. Measured on the main menu (game area, below the title bar):
    //   top bar (profile, Videos & Live, Ranks, Achievements, Friends, Battle Pass): down to ~11 %;
    //   store / Discord / My Mitos.is buttons: right side, 88-99 % across, down to ~30 %;
    //   middle (logo, mode, Play, Invite): 35-65 % across; EVO/EQUIP and XP bars: from ~91 % down;
    //   Worm.is tile (left) and the round buttons (right): bottom corners, from ~70 % down.
    //
    //   left side, two columns          right side, one column under the store buttons
    //   MitosScope   Quests                           Battle Pass
    //   Match        Equipment                        All Time Stats
    //   Account                                       Shop | Inventory
    //   Shop Hotkeys                                  Gem Training
    //
    // Each column starts at a percentage of the window (its left or right edge, and its top);
    // panels are stacked inside the column with a few DIPs between them.

    protected const double Gap = 6;               // between panels, DIPs
    protected const double ColumnWidth = 220;     // DIPs
    protected const double CardWidth = (ColumnWidth - Gap) / 2;

    protected const double LeftX = 0.008;         // left edge of the first left column (% of width)
    protected const double LeftTop = 0.125;       // top of the left columns (% of height), under the top bar
    protected const double RightX = 0.992;        // right edge of the right column (% of width), same margin as the left
    protected const double RightTop = 0.335;      // top of the right column (% of height), under the store buttons

    // heights (DIPs) of the panels, to stack them
    protected const double StatusH = 80, MatchH = 190, AccountH = 120;
    protected const double QuestsH = 130, EquipmentH = 240;
    protected const double BattlePassH = 72, StatsH = 105, CardH = 48, GemsH = 115;
    protected const double HotkeysH = 118;

    /// <summary>A panel in a left column: <paramref name="dy"/> DIPs below the column's top, <paramref name="dx"/> DIPs right of its left edge.</summary>
    protected static PanelLayout Left(double dy, double height, double dx = 0, double width = ColumnWidth, bool inMatch = true) =>
        Layout(LeftX, LeftTop, dx, dy, width, height, right: false, inMatch);

    /// <summary>A panel in the right column: <paramref name="dy"/> DIPs below its top, <paramref name="dx"/> DIPs left of its right edge.</summary>
    protected static PanelLayout Right(double dy, double height, double dx = 0, double width = ColumnWidth, bool inMatch = true) =>
        Layout(RightX, RightTop, dx, dy, width, height, right: true, inMatch);

    private static PanelLayout Layout(double fx, double fy, double dx, double dy, double width, double height, bool right, bool inMatch) => new()
    {
        Width = width,
        Height = height,
        ShowInMatch = inMatch,
        HasGameOffset = true,
        GameAnchorRight = right,
        GameFractionX = fx,
        GameFractionY = fy,
        GameOffsetX = dx,
        GameOffsetY = dy,
        // place on screen until a game window is known (as if the game filled a 1280x720 area)
        X = (int)(right ? fx * 1280 - dx - width : fx * 1280 + dx),
        Y = (int)(fy * 720 + dy),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClickable))]
    public partial bool IsEditMode { get; set; }

    /// <summary>
    /// Takes clicks outside edit mode (instead of letting them through to the game); a click calls
    /// <see cref="OnClicked"/>. Still never takes the focus from the game. To open a big card in
    /// the middle of the screen from the click, call Toggle() on a PopupViewModel (see ShopPanelViewModel).
    /// </summary>
    public virtual bool IsInteractive => false;

    /// <summary>Clickable right now: in edit mode a click drags the panel instead.</summary>
    public bool IsClickable => IsInteractive && !IsEditMode;

    public virtual void OnClicked() { }
}

/// <summary>Small clickable card that opens the shop window (see ShopViewModel).</summary>
public sealed class ShopPanelViewModel(TrackerViewModel tracker, ShopViewModel shop) : PanelViewModel(tracker)
{
    public ShopViewModel Shop { get; } = shop;
    public override string Id => "shop";
    public override string Title => "Shop";
    // Menu only by default: in a match a clickable card would catch clicks meant for the game.
    public override PanelLayout DefaultLayout => Right(BattlePassH + Gap + StatsH + Gap, CardH, dx: CardWidth + Gap, width: CardWidth, inMatch: false);
    public override bool IsInteractive => true;
    public override void OnClicked() => Shop.Toggle();
}

/// <summary>Small clickable card that opens the inventory window (see InventoryViewModel).</summary>
public sealed class InventoryPanelViewModel(TrackerViewModel tracker, InventoryViewModel inventory) : PanelViewModel(tracker)
{
    public InventoryViewModel Inventory { get; } = inventory;
    public override string Id => "inventory";
    public override string Title => "Inventory";
    // Menu only by default, like the Shop card (next to it).
    public override PanelLayout DefaultLayout => Right(BattlePassH + Gap + StatsH + Gap, CardH, width: CardWidth, inMatch: false);
    public override bool IsInteractive => true;
    public override void OnClicked() => Inventory.Toggle();
}

/// <summary>The 3 gem training slots, compact; a click opens the gem training window (see GemTrainingViewModel).</summary>
public sealed class GemTrainingPanelViewModel(TrackerViewModel tracker, GemTrainingViewModel gems) : PanelViewModel(tracker)
{
    public GemTrainingViewModel Gems { get; } = gems;
    public override string Id => "gemtraining";
    public override string Title => "Gem Training";
    // Menu only by default, like the Shop and Inventory cards (it's clickable too).
    public override PanelLayout DefaultLayout => Right(BattlePassH + Gap + StatsH + Gap + CardH + Gap, GemsH, inMatch: false);
    public override bool IsInteractive => true;
    public override void OnClicked() => Gems.Toggle();
}

/// <summary>
/// The 5 shop hotkey slots (item + key, see ShopHotkeysViewModel). Shown in the menu and in a match:
/// it's click-through like the info panels, only its ⚙ button (an overlay button, hidden in a match)
/// takes a click, to open the hotkeys window.
/// </summary>
public sealed class ShopHotkeysPanelViewModel(TrackerViewModel tracker, ShopHotkeysViewModel hotkeys) : PanelViewModel(tracker)
{
    public ShopHotkeysViewModel Hotkeys { get; } = hotkeys;
    public override string Id => "shophotkeys";
    public override string Title => "Shop Hotkeys";
    // Left column, under Account.
    public override PanelLayout DefaultLayout => Left(StatusH + Gap + MatchH + Gap + AccountH + Gap, HotkeysH);
}

public sealed class StatusPanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "status";
    public override string Title => "MitosScope";
    public override PanelLayout DefaultLayout => Left(0, StatusH);
}

public sealed class GamePanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "game";
    public override string Title => "Match";
    public override PanelLayout DefaultLayout => Left(StatusH + Gap, MatchH);
}

public sealed class AccountPanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "account";
    public override string Title => "Account";
    public override PanelLayout DefaultLayout => Left(StatusH + Gap + MatchH + Gap, AccountH);
}

public sealed class QuestsPanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "quests";
    public override string Title => "Quests";
    public override PanelLayout DefaultLayout => Left(0, QuestsH, dx: ColumnWidth + Gap);
}

public sealed class ProfilePanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "profile"; // kept as "profile" so the saved layout still applies
    public override string Title => "All Time Stats";
    public override PanelLayout DefaultLayout => Right(BattlePassH + Gap, StatsH);
}

public sealed class BattlePassPanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "battlepass";
    public override string Title => "Battle Pass";
    public override PanelLayout DefaultLayout => Right(0, BattlePassH);
}

public sealed class EquipmentPanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "equipment";
    public override string Title => "Equipment";
    public override PanelLayout DefaultLayout => Left(QuestsH + Gap, EquipmentH, dx: ColumnWidth + Gap);
}
