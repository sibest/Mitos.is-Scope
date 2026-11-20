using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MitosScope.App.Services;
using MitosScope.App.Settings;
using MitosScope.Core.Api;
using SharpHook.Data;

namespace MitosScope.App.ViewModels;

/// <summary>One of the 5 shop hotkey slots (see ShopHotkeysViewModel), over its saved <see cref="ShopHotkey"/>.</summary>
public sealed partial class ShopHotkeySlotViewModel : ObservableObject
{
    private KeyCode? _key;

    public ShopHotkeySlotViewModel(int number, ShopHotkey setting)
    {
        Number = number;
        Setting = setting;
        _key = Enum.TryParse<KeyCode>(setting.Key, out var key) ? key : null;
        Sprite = ItemSprites.Get(setting.ItemSprite);
    }

    public int Number { get; }

    /// <summary>The saved binding (changed in place, then settings.json is saved by the owner).</summary>
    public ShopHotkey Setting { get; }

    // ---- item ------------------------------------------------------------------------------------

    public bool HasItem => Setting.ItemId is not null;

    public string ItemName => EquippedItemViewModel.CleanName(Setting.ItemName);

    [ObservableProperty] public partial IImage? Sprite { get; private set; }

    /// <summary>"300 coins · x5".</summary>
    public string PriceText => !HasItem ? "No item"
        : Setting.ItemPack > 1 ? $"{Setting.ItemPrice:N0} coins · x{Setting.ItemPack:N0}" : $"{Setting.ItemPrice:N0} coins";

    /// <summary>Binds an item (null empties the slot). Returns whether anything changed.</summary>
    public bool SetItem(ShopItem? item)
    {
        if (item is not null && Setting.ItemId == item.Id && Setting.ItemName == item.Name && Setting.ItemSprite == item.Sprite
            && Setting.ItemPrice == item.Price && Setting.ItemPack == item.Pack)
            return false;
        Setting.ItemId = item?.Id;
        Setting.ItemName = item?.Name;
        Setting.ItemSprite = item?.Sprite;
        Setting.ItemPrice = item?.Price ?? 0;
        Setting.ItemPack = item?.Pack ?? 0;
        Sprite = ItemSprites.Get(Setting.ItemSprite);
        OnPropertyChanged(nameof(HasItem));
        OnPropertyChanged(nameof(ItemName));
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(IsReady));
        return true;
    }

    /// <summary>Uses left when the item is equipped, else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesText), nameof(HasUses))]
    public partial long? Uses { get; set; }

    public string UsesText => Uses is { } u ? $"x{u:N0}" : "";
    public bool HasUses => Uses is not null;

    // ---- key -------------------------------------------------------------------------------------

    public bool HasKey => _key is not null;

    /// <summary>Has an item and a key: a press buys.</summary>
    public bool IsReady => HasItem && HasKey;

    /// <summary>Waiting for the key of "Set key".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyText))]
    public partial bool IsCapturing { get; set; }

    /// <summary>"F1", "Alt+1"; "—" without a key.</summary>
    public string KeyText => IsCapturing ? "Press a key… (Esc cancels)"
        : _key is { } k ? HotkeyPress.Describe(k, Setting.Ctrl, Setting.Shift, Setting.Alt) : "—";

    public bool Matches(HotkeyPress press) =>
        _key == press.Key && Setting.Ctrl == press.Ctrl && Setting.Shift == press.Shift && Setting.Alt == press.Alt;

    public void SetKey(HotkeyPress? press)
    {
        _key = press?.Key;
        Setting.Key = press?.Key.ToString();
        Setting.Ctrl = press?.Ctrl ?? false;
        Setting.Shift = press?.Shift ?? false;
        Setting.Alt = press?.Alt ?? false;
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(KeyText));
        OnPropertyChanged(nameof(IsReady));
    }

    // ---- state -----------------------------------------------------------------------------------

    /// <summary>Selected in the popup: a click on an item binds it here.</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>A purchase is in flight (another press of the key waits for it).</summary>
    [ObservableProperty] public partial bool IsBuying { get; set; }

    /// <summary>Last press that started a purchase (presses closer than a moment are ignored).</summary>
    public DateTime LastPress { get; set; }

    /// <summary>Glows green / red for a moment after a purchase.</summary>
    [ObservableProperty] public partial bool IsBought { get; private set; }
    [ObservableProperty] public partial bool IsFailed { get; private set; }

    public void Flash(bool ok)
    {
        IsBought = ok;
        IsFailed = !ok;
    }

    public void EndFlash()
    {
        IsBought = false;
        IsFailed = false;
    }
}
