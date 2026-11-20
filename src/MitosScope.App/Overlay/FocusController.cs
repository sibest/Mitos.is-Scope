using System;
using MitosScope.App.Services;
using MitosScope.App.ViewModels;
using MitosScope.Core.Sessions;

namespace MitosScope.App.Overlay;

/// <summary>
/// Follows the foreground window:
/// - a game client gets focus → the panels show that client and the overlay is visible. The client
///   is found by its window (/v1/hwnd), else by its process (older clients, or before login);
/// - anything else gets focus → the overlay hides until a game client is focused again.
/// Edit mode always keeps the overlay visible (otherwise using the tray menu would hide it).
/// Our own windows never count (ForegroundWatcher skips them).
/// The focused game window is where the panels go: they're placed inside it each time it gets the
/// focus, and they follow it when it's dragged or resized (see OverlayManager.AttachToGameWindow).
/// </summary>
public sealed class FocusController : IDisposable
{
    private readonly ForegroundWatcher _watcher = new();
    private readonly GameWindowWatcher _gameWindow = new();
    private readonly SessionManager? _sessions;
    private readonly TrackerViewModel _tracker;
    private readonly OverlayManager _overlay;
    private bool _gameFocused;
    private long _foregroundWindow;
    private int _foregroundProcess;

    public FocusController(SessionManager? sessions, TrackerViewModel tracker, OverlayManager overlay)
    {
        _sessions = sessions;
        _tracker = tracker;
        _overlay = overlay;
    }

    /// <summary>
    /// A game client has the focus right now (the shop hotkeys only buy then). Always true when the
    /// foreground can't be watched (no focus info: the overlay is always visible too).
    /// </summary>
    public bool IsGameFocused => !ForegroundWatcher.IsSupported || _gameFocused;

    public void Start()
    {
        if (!ForegroundWatcher.IsSupported || _sessions is null)
            return; // no focus info: keep the overlay always visible

        _watcher.ForegroundChanged += OnForegroundChanged;
        _overlay.EditModeChanged += _ => UpdateVisibility();
        // The window of a client is known only after it logs in: it may already be in the foreground.
        _tracker.SessionWindowKnown += _ => Evaluate();
        _gameWindow.Changed += _overlay.GameWindowChanged;
        _watcher.Start();
    }

    private void OnForegroundChanged(long window, int processId)
    {
        _foregroundWindow = window;
        _foregroundProcess = processId;
        Evaluate();
    }

    private void Evaluate()
    {
        if (_foregroundProcess == 0)
            return; // nothing seen yet
        if (_sessions!.FindByWindow(_foregroundWindow) is { } session)
        {
            _gameFocused = true;
            _tracker.FocusSession(session);
        }
        else
        {
            _gameFocused = _sessions.IsGameProcess(_foregroundProcess);
            if (_gameFocused)
                _tracker.FocusProcess(_foregroundProcess);
        }
        if (_gameFocused)
        {
            // The active instance: the panels go inside its window (game start, switching instance,
            // coming back to it), and follow it from now on.
            _gameWindow.Watch(_foregroundWindow);
            _overlay.AttachToGameWindow(_foregroundWindow);
        }
        UpdateVisibility();
    }

    private void UpdateVisibility() => _overlay.SetFocusVisible(_overlay.IsEditMode || _gameFocused);

    public void Dispose()
    {
        _watcher.Dispose();
        _gameWindow.Dispose();
    }
}
