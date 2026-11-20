using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MitosScope.App.Services;
using MitosScope.App.Settings;
using MitosScope.Core.Api;

namespace MitosScope.App.ViewModels;

/// <summary>
/// Shop hotkeys: 5 slots, each one a key (any key, with Ctrl/Shift/Alt) bound to an item of shop
/// category 6 (potions and powers). Pressing the key while a game window has the focus buys one
/// pack of the item for that client (POST /v1/shop/buy, needs the shop permission; works in a
/// match too). The Hotkeys panel shows the 5 slots; its ⚙ button opens this popup, where a slot is
/// picked, an item clicked to bind it and "Set key" captures the next key pressed (Esc cancels).
/// The bindings are saved in settings.json (<see cref="AppSettings.ShopHotkeys"/>). Use on the UI thread.
/// </summary>
public partial class ShopHotkeysViewModel : PopupViewModel
{
    public const int SlotCount = 5;

    /// <summary>The shop category with the potions and powers.</summary>
    public const int ItemsCategory = 6;

    /// <summary>A second press of the same slot sooner than this is ignored (no double purchase by accident).</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>How long the result of a purchase stays on the panel.</summary>
    private static readonly TimeSpan StatusTime = TimeSpan.FromSeconds(3);

    /// <summary>How long a slot glows green/red after a purchase.</summary>
    private static readonly TimeSpan FlashTime = TimeSpan.FromMilliseconds(700);

    private readonly AppSettings _settings;
    private SessionViewModel? _session;
    private CancellationTokenSource? _loadCts;

    public ShopHotkeysViewModel(TrackerViewModel tracker, AppSettings settings)
    {
        _settings = settings;
        while (settings.ShopHotkeys.Count < SlotCount)
            settings.ShopHotkeys.Add(new ShopHotkey());
        Slots = Enumerable.Range(0, SlotCount).Select(i => new ShopHotkeySlotViewModel(i + 1, settings.ShopHotkeys[i])).ToList();
        SelectedSlot = Slots[0];

        Tracker = tracker;
        tracker.PropertyChanged += OnTrackerChanged;
        Watch(tracker.Current);
    }

    public TrackerViewModel Tracker { get; }

    public override string Title => "Shop Hotkeys";
    public override double Width => 780;
    public override double Height => 560;

    public IReadOnlyList<ShopHotkeySlotViewModel> Slots { get; }

    [ObservableProperty] public partial ShopHotkeySlotViewModel? SelectedSlot { get; private set; }

    partial void OnSelectedSlotChanged(ShopHotkeySlotViewModel? oldValue, ShopHotkeySlotViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;
    }

    /// <summary>The slot waiting for a key ("Set key"), or null.</summary>
    private ShopHotkeySlotViewModel? _capturing;

    // ---- items of category 6 (the popup) ---------------------------------------------------------

    [ObservableProperty] public partial IReadOnlyList<ShopItemViewModel>? Items { get; private set; }

    [ObservableProperty] public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => ErrorText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoinsText))]
    public partial long? Coins { get; private set; }

    public string CoinsText => Coins is { } c ? $"{c:N0} coins" : "";

    /// <summary>Why the items or the purchases can't work right now, or "".</summary>
    public string StateText => _session switch
    {
        null => "No client connected",
        { IsConnected: false } => "Waiting for the game…",
        { HasShopScope: false } => "Buying needs the shop permission: allow it in the game.",
        _ => "",
    };

    public bool HasStateText => StateText.Length > 0;

    // ---- the panel -------------------------------------------------------------------------------

    /// <summary>"Bought Speed potion (-300 coins)", errors: the last line of the panel for a few seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastStatus), nameof(PanelHint))]
    public partial string? LastStatus { get; private set; }

    [ObservableProperty] public partial bool LastStatusFailed { get; private set; }

    public bool HasLastStatus => LastStatus is not null;

    /// <summary>The panel's ⚙ button: hidden in a match (the popup closes in a match, and the button would take clicks there).</summary>
    public bool CanConfigure => _session?.Game?.InGame != true;

    /// <summary>Last line of the panel when there's no purchase to report.</summary>
    public string PanelHint => Slots.Any(s => s.IsReady) ? "" : "⚙ to bind items and keys";

    // ---- keys ------------------------------------------------------------------------------------

    /// <summary>
    /// Every key press seen by the global hook (HotkeyService), on the UI thread. A "Set key" waits
    /// for it; otherwise the slot bound to that key buys its item, if a game window has the focus.
    /// </summary>
    public void OnKey(HotkeyPress press, bool gameFocused)
    {
        if (_capturing is { } capturing)
        {
            Capture(capturing, press);
            return;
        }
        if (!gameFocused)
            return;
        if (Slots.FirstOrDefault(s => s.IsReady && s.Matches(press)) is { } slot)
            _ = BuyAsync(slot);
    }

    private void Capture(ShopHotkeySlotViewModel slot, HotkeyPress press)
    {
        _capturing = null;
        slot.IsCapturing = false;
        if (press.Key == SharpHook.Data.KeyCode.VcEscape && !press.Ctrl && !press.Shift && !press.Alt)
            return; // cancelled, keeps its key

        // One key, one slot: a key moved here leaves the slot it was on.
        foreach (var other in Slots.Where(s => s != slot && s.Matches(press)))
            other.SetKey(null);
        slot.SetKey(press);
        Save();
    }

    /// <summary>The panel's ⚙ button.</summary>
    [RelayCommand]
    private void OpenSettings() => Toggle();

    [RelayCommand]
    private void SelectSlot(ShopHotkeySlotViewModel slot) => SelectedSlot = slot;

    /// <summary>"Set key": the next key pressed (anywhere, the game keeps the focus) goes to this slot.</summary>
    [RelayCommand]
    private void SetKey(ShopHotkeySlotViewModel slot)
    {
        if (_capturing is { } previous)
            previous.IsCapturing = false;
        SelectedSlot = slot;
        _capturing = slot;
        slot.IsCapturing = true;
    }

    /// <summary>Empties the slot (item and key).</summary>
    [RelayCommand]
    private void ClearSlot(ShopHotkeySlotViewModel slot)
    {
        if (_capturing == slot)
        {
            _capturing = null;
            slot.IsCapturing = false;
        }
        slot.SetKey(null);
        slot.SetItem(null);
        Save();
    }

    /// <summary>Click on an item: it goes to the selected slot.</summary>
    [RelayCommand]
    private void AssignItem(ShopItemViewModel item)
    {
        if (SelectedSlot is not { } slot)
            return;
        slot.SetItem(item.Item);
        Save();
        UpdateUses();
    }

    [RelayCommand]
    private Task Retry() => LoadAsync();

    private void Save()
    {
        SettingsStore.Save(_settings);
        OnPropertyChanged(nameof(PanelHint));
    }

    // ---- buying ----------------------------------------------------------------------------------

    /// <summary>
    /// One pack of the slot's item for the displayed client (the focused one). The key press is the
    /// player's request; the game shows a notice for every purchase. Never cancelled halfway.
    /// </summary>
    private async Task BuyAsync(ShopHotkeySlotViewModel slot)
    {
        if (slot.IsBuying || slot.Setting.ItemId is not { } itemId)
            return;
        var now = DateTime.UtcNow;
        if (now - slot.LastPress < MinInterval)
            return;
        slot.LastPress = now;

        if (_session is not { HasShopScope: true } session)
        {
            _ = ShowResultAsync(slot, false, _session is { IsConnected: true } ? "No shop permission" : "Not connected");
            return;
        }

        slot.IsBuying = true;
        var r = await session.Session.BuyAsync(itemId, 1, CancellationToken.None);
        slot.IsBuying = false;

        var ok = r.StatusCode is >= 200 and < 300;
        if (ok && r.Value is { } v)
        {
            if (_session == session && IsOpen)
                Coins = v.Coins;
            var spent = v.Spent > 0 ? $" (-{v.Spent:N0} coins)" : "";
            _ = ShowResultAsync(slot, true, $"Bought {slot.ItemName}{spent}");
        }
        else if (ok)
            _ = ShowResultAsync(slot, true, $"Bought {slot.ItemName}");
        else
            _ = ShowResultAsync(slot, false, $"{slot.ItemName}: {ShopViewModel.BuyErrorText(r.StatusCode, r.Error)}");
    }

    private async Task ShowResultAsync(ShopHotkeySlotViewModel slot, bool ok, string text)
    {
        LastStatus = text;
        LastStatusFailed = !ok;
        slot.Flash(ok);
        await Task.Delay(FlashTime);
        slot.EndFlash();
        await Task.Delay(StatusTime - FlashTime);
        if (LastStatus == text)
            LastStatus = null;
    }

    // ---- loading the items (popup) ---------------------------------------------------------------

    protected override void OnOpened() => _ = LoadAsync();

    protected override void OnClosed()
    {
        _loadCts?.Cancel();
        IsLoading = false;
        if (_capturing is { } capturing)
        {
            capturing.IsCapturing = false;
            _capturing = null;
        }
    }

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        ErrorText = null;
        if (_session is not { HasShopScope: true } session)
        {
            Items = null;
            return; // StateText explains it
        }

        IsLoading = true;
        var r = await session.Session.GetShopItemsAsync(ItemsCategory, ct);
        if (ct.IsCancellationRequested)
            return;
        IsLoading = false;
        if (!r.IsSuccess || r.Value is not { } value)
        {
            ErrorText = r.StatusCode == 0 ? "The game didn't answer. Try again." : $"Couldn't load the items ({r.Error ?? r.StatusCode.ToString()}).";
            return;
        }

        Coins = value.Coins;
        var level = session.Account?.Level;
        Items = value.Items
            .Select(i => new ShopItemViewModel(i, value.Coins, level))
            .OrderBy(i => i.Item.Level)
            .ThenBy(i => i.Item.Price)
            .ToList();

        // Names follow the game's language and prices the player's discounts: refresh the copies.
        var changed = false;
        foreach (var slot in Slots)
            if (slot.Setting.ItemId is { } id && value.Items.FirstOrDefault(i => i.Id == id) is { } item)
                changed |= slot.SetItem(item);
        if (changed)
            Save();
    }

    // ---- following the displayed client ----------------------------------------------------------

    private void OnTrackerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackerViewModel.Current))
            Watch(Tracker.Current);
    }

    private void Watch(SessionViewModel? session)
    {
        if (_session == session)
            return;
        if (_session is not null)
            _session.PropertyChanged -= OnSessionChanged;
        _session = session;
        if (_session is not null)
            _session.PropertyChanged += OnSessionChanged;

        Coins = null;
        Items = null;
        NotifyState();
        UpdateUses();
        if (IsOpen)
            _ = LoadAsync();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionViewModel.EquippedItems):
                UpdateUses();
                break;
            case nameof(SessionViewModel.Game):
                OnPropertyChanged(nameof(CanConfigure));
                break;
            case nameof(SessionViewModel.IsConnected) or nameof(SessionViewModel.HasShopScope) or nameof(SessionViewModel.Status):
                NotifyState();
                if (IsOpen && Items is null && !IsLoading)
                    _ = LoadAsync();
                break;
        }
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HasStateText));
        OnPropertyChanged(nameof(CanConfigure));
    }

    /// <summary>Uses left of each slot's item when it's equipped (consumables in the item slots).</summary>
    private void UpdateUses()
    {
        var equipped = _session?.EquippedItems;
        foreach (var slot in Slots)
        {
            var item = slot.Setting.ItemId is { } id ? equipped?.FirstOrDefault(e => e.Item.Id == id) : null;
            slot.Uses = item is { IsConsumable: true } ? item.Item.Durability : null;
        }
    }
}
