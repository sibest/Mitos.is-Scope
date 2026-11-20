using System;
using System.Collections.Generic;
using SharpHook;
using SharpHook.Data;

namespace MitosScope.App.Services;

/// <summary>
/// Global (system-wide) hotkeys via SharpHook, so they work while the game has focus.
/// Ctrl+Shift+E toggles the overlay edit mode. Every other key press is raised as
/// <see cref="KeyPressed"/> (the shop hotkeys use it). Keys are never blocked: the game gets them too.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public const string ToggleEditModeText = "Ctrl+Shift+E";

    private readonly TaskPoolGlobalHook _hook = new();

    /// <summary>Keys held down right now: Windows repeats KeyPressed while a key is held, only the first one counts.</summary>
    private readonly HashSet<KeyCode> _down = [];

    /// <summary>Raised on a hook thread. Marshal to the UI thread.</summary>
    public event Action? ToggleEditModePressed;

    /// <summary>
    /// A key went down (not a modifier alone, not auto-repeat, not Ctrl+Shift+E), with the modifiers
    /// held at that moment. Raised on a hook thread. Marshal to the UI thread.
    /// </summary>
    public event Action<HotkeyPress>? KeyPressed;

    public void Start()
    {
        _hook.KeyPressed += OnKeyPressed;
        _hook.KeyReleased += OnKeyReleased;
        _ = _hook.RunAsync();
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        var key = e.Data.KeyCode;
        var mask = e.RawEvent.Mask;
        var press = new HotkeyPress(key, mask.HasCtrl(), mask.HasShift(), mask.HasAlt());
        if (press.IsToggleEditMode)
        {
            ToggleEditModePressed?.Invoke();
            return;
        }

        bool first;
        lock (_down)
            first = _down.Add(key);
        if (first && !HotkeyPress.IsModifier(key))
            KeyPressed?.Invoke(press);
    }

    private void OnKeyReleased(object? sender, KeyboardHookEventArgs e)
    {
        lock (_down)
            _down.Remove(e.Data.KeyCode);
    }

    public void Dispose()
    {
        _hook.KeyPressed -= OnKeyPressed;
        _hook.KeyReleased -= OnKeyReleased;
        _hook.Dispose();
    }
}

/// <summary>A key with its modifiers, e.g. Alt+1.</summary>
public readonly record struct HotkeyPress(KeyCode Key, bool Ctrl, bool Shift, bool Alt)
{
    public bool IsToggleEditMode => Key == KeyCode.VcE && Ctrl && Shift && !Alt;

    public static bool IsModifier(KeyCode key) => key is KeyCode.VcLeftControl or KeyCode.VcRightControl
        or KeyCode.VcLeftShift or KeyCode.VcRightShift or KeyCode.VcLeftAlt or KeyCode.VcRightAlt
        or KeyCode.VcLeftMeta or KeyCode.VcRightMeta;

    /// <summary>"Ctrl+Alt+F1", "Num 1", "Q".</summary>
    public string Text => Describe(Key, Ctrl, Shift, Alt);

    public static string Describe(KeyCode key, bool ctrl, bool shift, bool alt)
    {
        var name = key.ToString();
        if (name.StartsWith("Vc", StringComparison.Ordinal))
            name = name[2..];
        if (name.StartsWith("NumPad", StringComparison.Ordinal))
            name = "Num " + name[6..];
        var text = (ctrl ? "Ctrl+" : "") + (shift ? "Shift+" : "") + (alt ? "Alt+" : "") + name;
        return text;
    }
}
