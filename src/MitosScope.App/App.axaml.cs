using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using MitosScope.App.Overlay;
using MitosScope.App.Services;
using MitosScope.App.Settings;
using MitosScope.App.ViewModels;
using MitosScope.App.ViewModels.Panels;
using MitosScope.Core.Api;
using MitosScope.Core.Sessions;

namespace MitosScope.App;

public partial class App : Application
{
    private AppSettings _settings = null!;
    private MitosApiClient? _api;
    private SessionManager? _sessions;
    private OverlayManager? _overlay;
    private HotkeyService? _hotkeys;
    private FocusController? _focus;
    private MatchStateController? _matchState;
    private GameLauncher _launcher = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // No main window: the app lives in the tray and in the panel windows.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => Shutdown();

            _settings = SettingsStore.Load();
            var firstRun = _settings.Panels.Count == 0;

            var tracker = new TrackerViewModel();
            _launcher = new GameLauncher(_settings);
            tracker.SessionWindowKnown += vm =>
            {
                if (vm.Session.WindowHandle is { } hwnd)
                    _launcher.RememberFromWindow(hwnd);
            };
            StartSessions(tracker);

            var popups = CreatePopups(tracker, _settings);
            _overlay = new OverlayManager(_settings, CreatePanels(tracker, popups));
            _overlay.ShowAll();
            _overlay.AttachPopups([popups.Shop, popups.Inventory, popups.GemTraining, popups.ShopHotkeys]);

            _focus = new FocusController(_sessions, tracker, _overlay);
            _focus.Start();

            _matchState = new MatchStateController(tracker, _overlay);
            _matchState.Start();

            _hotkeys = new HotkeyService();
            _hotkeys.ToggleEditModePressed += () => Dispatcher.UIThread.Post(() => _overlay.ToggleEditMode());
            // Shop hotkeys: buy only while a game window has the focus (the key also reaches the game).
            _hotkeys.KeyPressed += press => Dispatcher.UIThread.Post(() => popups.ShopHotkeys.OnKey(press, _focus?.IsGameFocused == true));
            _hotkeys.Start();

            CreateTrayIcon(desktop);

            if (firstRun)
                _overlay.SetEditMode(true); // let the user place the panels the first time

            StartItemsPackUpdates(tracker);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The popups (big cards in the middle of the screen, see PopupViewModel). Register new ones here,
    /// add them to the AttachPopups call above, and pass them to the panel that opens them.
    /// </summary>
    private sealed record Popups(ShopViewModel Shop, InventoryViewModel Inventory, GemTrainingViewModel GemTraining,
        ShopHotkeysViewModel ShopHotkeys);

    private static Popups CreatePopups(TrackerViewModel tracker, AppSettings settings)
    {
        var sets = new ItemSetStore(settings);
        return new(
            Shop: new ShopViewModel(tracker, settings, sets),
            Inventory: new InventoryViewModel(tracker, settings, sets),
            GemTraining: new GemTrainingViewModel(tracker),
            ShopHotkeys: new ShopHotkeysViewModel(tracker, settings));
    }

    /// <summary>Register new panels here.</summary>
    private static PanelViewModel[] CreatePanels(TrackerViewModel tracker, Popups popups) =>
    [
        new StatusPanelViewModel(tracker),
        new GamePanelViewModel(tracker),
        new AccountPanelViewModel(tracker),
        new QuestsPanelViewModel(tracker),
        new EquipmentPanelViewModel(tracker),
        new ProfilePanelViewModel(tracker),
        new BattlePassPanelViewModel(tracker),
        new ShopPanelViewModel(tracker, popups.Shop),
        new InventoryPanelViewModel(tracker, popups.Inventory),
        new GemTrainingPanelViewModel(tracker, popups.GemTraining),
        new ShopHotkeysPanelViewModel(tracker, popups.ShopHotkeys),
    ];

    private void StartSessions(TrackerViewModel tracker)
    {
        var appKey = AppKeyProvider.TryLoad();
        if (appKey is null)
        {
            tracker.StartupError = $"{AppKeyProvider.FileName} not found next to the executable.";
            return;
        }

        _api = new MitosApiClient();
        _sessions = new SessionManager(_api, appKey, _settings.GameProcessNames);
        if (!_sessions.UsesProcessWatcher)
            tracker.SearchingText = "Looking for the game… (tip: set gameProcessNames in settings.json)";

        tracker.Attach(_sessions);
        _sessions.Start();
    }

    private readonly CancellationTokenSource _itemsCts = new();

    /// <summary>
    /// Item icons follow the game: the items pack is checked at startup and every 6 hours
    /// (ItemsPackUpdater). A new pack reloads the icons in the Equipment panel right away.
    /// </summary>
    private void StartItemsPackUpdates(TrackerViewModel tracker)
    {
        var ct = _itemsCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                if (await ItemsPackUpdater.UpdateAsync(tracker.ReportItemsUpdate, ct).ConfigureAwait(false))
                    Dispatcher.UIThread.Post(() =>
                    {
                        ItemSprites.Reload();
                        tracker.RefreshItemIcons();
                    });
                try { await Task.Delay(TimeSpan.FromHours(6), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }, ct);
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var overlay = _overlay!;

        var editMode = CheckItem($"Edit mode ({HotkeyService.ToggleEditModeText})", overlay.IsEditMode, overlay.ToggleEditMode);
        overlay.EditModeChanged += on => editMode.IsChecked = on;

        var showOverlay = CheckItem("Show overlay", overlay.IsOverlayShown, overlay.ToggleOverlayShown);
        overlay.OverlayShownChanged += on => showOverlay.IsChecked = on;

        // One submenu per panel: "Enabled" (the ✕ button in edit mode unchecks it), then whether it
        // shows in the menu and/or in a match.
        var panelsMenu = new NativeMenu();
        var enabledItems = new Dictionary<PanelViewModel, NativeMenuItem>();
        var stateItems = new Dictionary<(PanelViewModel, bool), NativeMenuItem>();
        foreach (var panel in overlay.Panels)
        {
            var enabled = CheckItem("Enabled", overlay.IsPanelEnabled(panel),
                () => overlay.SetPanelEnabled(panel, !overlay.IsPanelEnabled(panel)));
            enabledItems[panel] = enabled;

            var submenu = new NativeMenu { enabled, new NativeMenuItemSeparator() };
            foreach (var (inMatch, header) in new[] { (false, "Show in menu"), (true, "Show in match") })
            {
                var item = CheckItem(header, overlay.IsShownIn(panel, inMatch),
                    () => overlay.SetShownIn(panel, inMatch, !overlay.IsShownIn(panel, inMatch)));
                stateItems[(panel, inMatch)] = item;
                submenu.Add(item);
            }
            panelsMenu.Add(new NativeMenuItem(panel.Title) { Menu = submenu });
        }
        overlay.PanelEnabledChanged += (panel, on) => enabledItems[panel].IsChecked = on;
        overlay.PanelGameStateChanged += (panel, inMatch, on) => stateItems[(panel, inMatch)].IsChecked = on;

        // Every panel back on, with its default size and options, at its default place inside the game window.
        var resetPanels = new NativeMenuItem("Reset panels");
        resetPanels.Click += (_, _) => overlay.ResetPanels();
        panelsMenu.Add(new NativeMenuItemSeparator());
        panelsMenu.Add(resetPanels);

        // "Open Instance": slots 1-20 → starts the game with "-slot0".."-slot19".
        var instanceMenu = new NativeMenu();
        void FillInstanceMenu()
        {
            instanceMenu.Items.Clear();
            if (_launcher.GamePath is null)
            {
                instanceMenu.Add(new NativeMenuItem("Open the game once so its location is known") { IsEnabled = false });
                return;
            }
            for (var slot = 0; slot < GameLauncher.SlotCount; slot++)
            {
                var item = new NativeMenuItem((slot + 1).ToString());
                var s = slot;
                item.Click += (_, _) => _launcher.Launch(s);
                instanceMenu.Add(item);
            }
        }
        FillInstanceMenu();
        _launcher.GamePathChanged += FillInstanceMenu;

        var startWithWindows = CheckItem("Start with Windows", StartupRegistration.IsEnabled, () => { });
        startWithWindows.IsEnabled = StartupRegistration.IsSupported;
        startWithWindows.Click += (_, _) =>
        {
            StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled);
            startWithWindows.IsChecked = StartupRegistration.IsEnabled;
        };

        var openSettings = new NativeMenuItem("Open settings folder");
        openSettings.Click += (_, _) =>
            Process.Start(new ProcessStartInfo(SettingsStore.DirectoryPath) { UseShellExecute = true });

        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => desktop.Shutdown();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MitosScope.App/Assets/MitosScope.ico"))),
            ToolTipText = "MitosScope",
            Menu =
            [
                editMode,
                showOverlay,
                new NativeMenuItem("Panels") { Menu = panelsMenu },
                new NativeMenuItem("Open Instance") { Menu = instanceMenu },
                new NativeMenuItemSeparator(),
                startWithWindows,
                openSettings,
                exit,
            ],
        };
        tray.Clicked += (_, _) => overlay.ToggleEditMode();

        TrayIcon.SetIcons(this, [tray]);
    }

    private static NativeMenuItem CheckItem(string header, bool isChecked, Action onClick)
    {
        var item = new NativeMenuItem(header) { ToggleType = MenuItemToggleType.CheckBox, IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void Shutdown()
    {
        if (_overlay is { IsEditMode: true })
            _overlay.SaveLayout();
        _itemsCts.Cancel();
        _hotkeys?.Dispose();
        _focus?.Dispose();
        _matchState?.Dispose();

        // Session loops use ConfigureAwait(false), so blocking here can't deadlock the UI thread.
        try { _sessions?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _api?.Dispose();
    }
}
