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
            StartSessions(tracker);

            _overlay = new OverlayManager(_settings, CreatePanels(tracker));
            _overlay.ShowAll();

            _focus = new FocusController(_sessions, tracker, _overlay);
            _focus.Start();

            _matchState = new MatchStateController(tracker, _overlay);
            _matchState.Start();

            _hotkeys = new HotkeyService();
            _hotkeys.ToggleEditModePressed += () => Dispatcher.UIThread.Post(() => _overlay.ToggleEditMode());
            _hotkeys.Start();

            CreateTrayIcon(desktop);

            if (firstRun)
                _overlay.SetEditMode(true); // let the user place the panels the first time

            StartItemsPackUpdates(tracker);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Register new panels here.</summary>
    private static PanelViewModel[] CreatePanels(TrackerViewModel tracker) =>
    [
        new StatusPanelViewModel(tracker),
        new GamePanelViewModel(tracker),
        new AccountPanelViewModel(tracker),
        new QuestsPanelViewModel(tracker),
        new EquipmentPanelViewModel(tracker),
        new ProfilePanelViewModel(tracker),
        new BattlePassPanelViewModel(tracker),
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

        var openSettings = new NativeMenuItem("Open settings folder");
        openSettings.Click += (_, _) =>
            Process.Start(new ProcessStartInfo(SettingsStore.DirectoryPath) { UseShellExecute = true });

        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => desktop.Shutdown();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MitosScope.App/Assets/avalonia-logo.ico"))),
            ToolTipText = "MitosScope",
            Menu =
            [
                editMode,
                showOverlay,
                new NativeMenuItem("Panels") { Menu = panelsMenu },
                new NativeMenuItemSeparator(),
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
