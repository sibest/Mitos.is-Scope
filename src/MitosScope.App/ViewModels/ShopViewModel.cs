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
using MitosScope.Core.Items;

namespace MitosScope.App.ViewModels;

/// <summary>
/// The shop browser (API v3, needs the "shop" permission): a popup opened by the small Shop panel.
/// First page: the categories; clicking one shows its items in the same window. Clicking an item
/// shows its card in the middle of the window, with its attributes, description and the Buy button
/// (and how many packs, for consumables). The search box filters the category shown, or looks in
/// every category from the first page. The Build tool finds the 3 items (one per category, like the
/// equipment slots) that give the most points in the attributes the player picks, counting the
/// equipment already owned as free. Everything is loaded when shown, never polled. Use on the UI thread.
/// </summary>
public partial class ShopViewModel : PopupViewModel
{
    private readonly AppSettings _settings;
    private readonly ItemSetStore _sets;
    private SessionViewModel? _session;
    private CancellationTokenSource? _loadCts;

    /// <summary>Every category's items, loaded for the search and the build tool; dropped after a purchase.</summary>
    private Dictionary<int, IReadOnlyList<ShopItemViewModel>>? _allItems;

    /// <summary>The account's equipment from /v1/inventory, for the build tool (owned equipment isn't in the shop).</summary>
    private IReadOnlyList<InventoryItem>? _owned;

    /// <summary>The search is reading every category (more typing just waits for it).</summary>
    private bool _searchLoading;

    public ShopViewModel(TrackerViewModel tracker, AppSettings settings, ItemSetStore sets)
    {
        _settings = settings;
        _sets = sets;
        Tracker = tracker;
        tracker.PropertyChanged += OnTrackerChanged;
        Watch(tracker.Current);
    }

    public TrackerViewModel Tracker { get; }

    public override string Title => "Shop";

    public override double Width => 820;
    public override double Height => 580;

    [ObservableProperty] public partial IReadOnlyList<ShopCategory>? Categories { get; private set; }

    /// <summary>Null = categories page (or search / build); otherwise the items page of this category.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCategoriesPage), nameof(IsItemsPage), nameof(IsSearchPage), nameof(TitleText),
        nameof(SearchHint), nameof(ShowBack), nameof(ShowItemsList), nameof(ShownItems), nameof(NoResults))]
    public partial ShopCategory? SelectedCategory { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownItems), nameof(NoResults))]
    public partial IReadOnlyList<ShopItemViewModel>? Items { get; private set; }

    /// <summary>Coins from the last items answer (not cached by the server).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoinsText), nameof(HasCoins))]
    public partial long? Coins { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoResults))]
    public partial bool IsLoading { get; private set; }

    /// <summary>"Loading the shop… 3/9" while every category is read (search, build tool).</summary>
    [ObservableProperty] public partial string LoadingText { get; private set; } = "Loading…";

    // ---- search ---------------------------------------------------------------------------------

    /// <summary>Filters the category shown; on the first page it looks in every category.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCategoriesPage), nameof(IsSearchPage), nameof(HasSearch), nameof(TitleText),
        nameof(ShowBack), nameof(ShowItemsList), nameof(ShownItems), nameof(NoResults))]
    public partial string? SearchText { get; set; }

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Results of the search in every category.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownItems), nameof(NoResults))]
    public partial IReadOnlyList<ShopItemViewModel>? SearchResults { get; private set; }

    public string SearchHint => SelectedCategory is { } c ? $"Search in {c.Name}…" : "Search every category (name, attribute, description)…";

    /// <summary>The tiles shown: the category's items (filtered) or the search results.</summary>
    public IReadOnlyList<ShopItemViewModel>? ShownItems => IsItemsPage
        ? HasSearch ? Items?.Where(i => i.Matches(SearchText)).ToList() : Items
        : IsSearchPage ? SearchResults : null;

    public bool ShowItemsList => IsItemsPage || IsSearchPage;
    public bool NoResults => ShowItemsList && !IsLoading && HasSearch && ShownItems is { Count: 0 };

    // ---- item card (buying) ---------------------------------------------------------------------

    /// <summary>Most packs of a consumable bought at once from the card.</summary>
    public const int MaxQuantity = 30;

    /// <summary>How long "Bought" or an error stays on the Buy button.</summary>
    private static readonly TimeSpan BuyStatusTime = TimeSpan.FromSeconds(3);

    /// <summary>The item whose card is shown over the list, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem), nameof(Quantity), nameof(TotalPrice), nameof(PacksText),
        nameof(BuyText), nameof(CanPressBuy), nameof(ShowBuy), nameof(ShowQuantity))]
    public partial ShopItemViewModel? SelectedItem { get; private set; }

    public bool HasSelectedItem => SelectedItem is not null;

    /// <summary>Slider value (1-30). Only consumables have the slider; other items are always 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Quantity), nameof(TotalPrice), nameof(PacksText), nameof(BuyText), nameof(CanPressBuy))]
    public partial double QuantityValue { get; set; } = 1;

    public int Quantity => SelectedItem is { IsConsumable: true } ? Math.Clamp((int)Math.Round(QuantityValue), 1, MaxQuantity) : 1;

    public long TotalPrice => (SelectedItem?.Item.Price ?? 0) * Quantity;

    public bool ShowQuantity => SelectedItem is { IsConsumable: true, IsBuyable: true };

    /// <summary>"3 packs · 15 uses".</summary>
    public string PacksText
    {
        get
        {
            var packs = Quantity == 1 ? "1 pack" : $"{Quantity:N0} packs";
            return SelectedItem?.Item.Pack is > 1 and var p ? $"{packs} · {p * Quantity:N0} uses" : packs;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPressBuy), nameof(BuyText))]
    public partial bool IsBuying { get; private set; }

    /// <summary>Shown on the Buy button instead of the price for a few seconds ("Buying…", "Bought", errors).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuyText), nameof(ShowBuy))]
    public partial string? BuyStatus { get; private set; }

    private bool CanAfford => Coins is not { } c || TotalPrice <= c;

    /// <summary>The Buy button is shown when the item can be bought, or while it shows a result.</summary>
    public bool ShowBuy => SelectedItem is { IsBuyable: true } || BuyStatus is not null;

    public bool CanPressBuy => SelectedItem is { IsBuyable: true } && CanAfford && !IsBuying && CanBrowse;

    public string BuyText => BuyStatus ?? (CanAfford ? $"Buy · {TotalPrice:N0} coins" : $"Not enough coins ({TotalPrice:N0})");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool IsCategoriesPage => SelectedCategory is null && !IsBuildPage && !HasSearch;
    public bool IsItemsPage => SelectedCategory is not null && !IsBuildPage;
    public bool IsSearchPage => SelectedCategory is null && !IsBuildPage && HasSearch;
    public bool ShowBack => SelectedCategory is not null || IsBuildPage;
    public bool HasError => ErrorText is not null;
    public bool HasCoins => Coins is not null;
    public string CoinsText => Coins is { } c ? $"{c:N0} coins" : "";

    public string TitleText => IsBuildPage ? "Build tool"
        : SelectedCategory?.Name is { Length: > 0 } n ? n
        : HasSearch ? "Search" : "Shop";

    /// <summary>Second line of the small Shop panel.</summary>
    public string CardText => _session switch
    {
        null => "No client connected",
        { IsConnected: false } => "Waiting for the game…",
        { HasShopScope: false } => "Shop permission not granted",
        _ => IsOpen ? "Click to close" : "Click to open",
    };

    public bool CanBrowse => _session is { HasShopScope: true };

    // ---- build tool -----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCategoriesPage), nameof(IsItemsPage), nameof(IsSearchPage), nameof(ShowBack),
        nameof(TitleText), nameof(ShowItemsList), nameof(ShownItems), nameof(NoResults))]
    public partial bool IsBuildPage { get; private set; }

    /// <summary>Every attribute found on equipment, to pick from.</summary>
    [ObservableProperty] public partial IReadOnlyList<BuildAttrViewModel>? BuildAttrs { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildResults), nameof(BuildHint))]
    public partial IReadOnlyList<BuildResultViewModel>? BuildResults { get; private set; }

    public bool HasBuildResults => BuildResults is { Count: > 0 };

    /// <summary>Only items the player's level allows.</summary>
    [ObservableProperty] public partial bool BuildMyLevel { get; set; }

    /// <summary>The items to buy must cost, together, at most the player's coins.</summary>
    [ObservableProperty] public partial bool BuildMyCoins { get; set; }

    /// <summary>Count the equipment already owned (free).</summary>
    [ObservableProperty] public partial bool BuildUseOwned { get; set; }

    public string BuildHint => BuildAttrs is null ? ""
        : !BuildAttrs.Any(a => a.IsSelected) ? "Pick the attributes you want, in order of priority: the tool finds the best 3 items (one per category) for all of them together, then lists the builds by the 1°, then the 2°, and so on."
        : HasBuildResults ? ""
        : "No equipment gives these attributes with the options chosen.";

    public bool HasBuildHint => BuildHint.Length > 0;

    [ObservableProperty] public partial string? BuildStatus { get; private set; }

    // ---- commands -------------------------------------------------------------------------------

    protected override void OnOpened()
    {
        SelectedCategory = null;
        Items = null;
        IsBuildPage = false;
        SearchText = null;
        _allItems = null;
        _owned = null;
        OnPropertyChanged(nameof(CardText));
        _ = LoadCategoriesAsync();
    }

    protected override void OnClosed()
    {
        CloseItem();
        CancelLoad();
        IsLoading = false;
        OnPropertyChanged(nameof(CardText));
    }

    [RelayCommand]
    private Task OpenCategory(ShopCategory category)
    {
        SearchText = null;
        SelectedCategory = category;
        Items = null;
        return LoadItemsAsync(category);
    }

    [RelayCommand]
    private void Back()
    {
        CloseItem();
        CancelLoad();
        IsLoading = false;
        ErrorText = null;
        IsBuildPage = false;
        SelectedCategory = null;
        Items = null;
        SearchText = null;
        if (Categories is null)
            _ = LoadCategoriesAsync();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = null;

    [RelayCommand]
    private void SelectItem(ShopItemViewModel item)
    {
        if (IsBuying)
            return;
        BuyStatus = null;
        QuantityValue = 1;
        SelectedItem = item;
    }

    [RelayCommand]
    private void SelectPick(BuildPickViewModel pick)
    {
        if (pick.ShopItem is { } item)
            SelectItem(item);
    }

    [RelayCommand]
    private void CloseItem()
    {
        if (IsBuying)
            return; // keep the card until the answer comes (the coins are being spent)
        BuyStatus = null;
        SelectedItem = null;
    }

    [RelayCommand]
    private void ChangeQuantity(string delta) =>
        QuantityValue = Math.Clamp(Quantity + int.Parse(delta), 1, MaxQuantity);

    /// <summary>
    /// Buys the item of the card (POST /v1/shop/buy, spends coins). The card itself is the
    /// confirmation: the player clicked the item, saw the price and clicked Buy. Never cancelled
    /// halfway; afterwards the items are reloaded (owned / can_buy changed) and the card stays open.
    /// </summary>
    [RelayCommand]
    private async Task BuyAsync()
    {
        if (!CanPressBuy || SelectedItem is not { } item || _session is not { } session)
            return;
        var qty = Quantity;
        IsBuying = true;
        BuyStatus = "Buying…";
        var r = await session.Session.BuyAsync(item.Item.Id, qty, CancellationToken.None);
        IsBuying = false;
        if (_session != session)
            return; // another client is shown now (Watch closed the card)

        var ok = r.StatusCode is >= 200 and < 300;
        BuyStatus = ok
            ? r.Value is { Spent: > 0 } v ? $"Bought (-{v.Spent:N0} coins)" : "Bought"
            : BuyErrorText(r.StatusCode, r.Error);
        if (ok && r.Value is { } bought)
            Coins = bought.Coins;
        if (ok)
        {
            // owned / can_buy changed: the search and the build tool read the shop again
            _allItems = null;
            _owned = null;
            if (SelectedCategory is { } category)
                _ = LoadItemsAsync(category);
            else if (IsBuildPage)
                _ = LoadBuildAsync(keepCard: true);
            else if (IsSearchPage)
                _ = RunSearchAsync();
        }

        var status = BuyStatus;
        await Task.Delay(BuyStatusTime);
        if (BuyStatus == status && !IsBuying)
            BuyStatus = null;
    }

    /// <summary>Also used by the shop hotkeys.</summary>
    internal static string BuyErrorText(int status, string? error) => (status, error) switch
    {
        (0, "not_connected") => "Not connected",
        (0, _) => "Game not responding",
        (403, "scope_required" or "addon_scope_revoked") => "No shop permission",
        (_, "funds_insufficient") => "Not enough coins",
        (504, _) => "Server timeout, try again",
        _ => $"Purchase failed ({error ?? status.ToString()})",
    };

    partial void OnCoinsChanged(long? value)
    {
        OnPropertyChanged(nameof(CanPressBuy));
        OnPropertyChanged(nameof(BuyText));
        if (BuildMyCoins)
            UpdateBuild();
    }

    [RelayCommand]
    private Task Retry() => IsBuildPage ? LoadBuildAsync() : SelectedCategory is { } c ? LoadItemsAsync(c) : HasSearch ? RunSearchAsync() : LoadCategoriesAsync();

    partial void OnSearchTextChanged(string? value)
    {
        if (IsSearchPage)
            _ = RunSearchAsync();
    }

    [RelayCommand]
    private Task OpenBuild()
    {
        CloseItem();
        SearchText = null;
        SelectedCategory = null;
        Items = null;
        IsBuildPage = true;
        BuildMyLevel = _settings.BuildMyLevel;
        BuildMyCoins = _settings.BuildMyCoins;
        BuildUseOwned = _settings.BuildUseOwned;
        return LoadBuildAsync();
    }

    partial void OnBuildMyLevelChanged(bool value) => BuildOptionChanged();
    partial void OnBuildMyCoinsChanged(bool value) => BuildOptionChanged();
    partial void OnBuildUseOwnedChanged(bool value) => BuildOptionChanged();

    private void BuildOptionChanged()
    {
        if (!IsBuildPage)
            return;
        _settings.BuildMyLevel = BuildMyLevel;
        _settings.BuildMyCoins = BuildMyCoins;
        _settings.BuildUseOwned = BuildUseOwned;
        SettingsStore.Save(_settings);
        UpdateBuild();
    }

    /// <summary>The picked attributes in priority order (the order they were clicked).</summary>
    private List<BuildAttrViewModel> PickedAttrs => BuildAttrs?.Where(a => a.IsSelected).OrderBy(a => a.Priority).ToList() ?? [];

    /// <summary>
    /// Picks the attribute as the next priority (last), or removes it: the ones after it move up one.
    /// The order is saved in settings.json.
    /// </summary>
    [RelayCommand]
    private void ToggleBuildAttr(BuildAttrViewModel attr)
    {
        var picked = PickedAttrs;
        if (attr.IsSelected)
            picked.Remove(attr);
        else
            picked.Add(attr);
        attr.Priority = 0;
        for (var i = 0; i < picked.Count; i++)
            picked[i].Priority = i + 1;
        _settings.BuildAttrs = picked.Select(a => a.Name).ToList();
        SettingsStore.Save(_settings);
        UpdateBuild();
    }

    [RelayCommand]
    private void ClearBuildAttrs()
    {
        foreach (var a in BuildAttrs ?? [])
            a.Priority = 0;
        _settings.BuildAttrs = [];
        SettingsStore.Save(_settings);
        UpdateBuild();
    }

    /// <summary>Saves a build as an item set of the inventory window (only owned items can be equipped from a set).</summary>
    [RelayCommand]
    private async Task SaveBuildAsSet(BuildResultViewModel build)
    {
        if (_session?.Account?.Id is not { } account)
            return;
        var name = string.Join(" + ", PickedAttrs.Select(a => a.Label));
        var set = _sets.Add(account, name, build.Picks.Select(p => new ItemSetEntry
        {
            Id = p.Item.Id,
            Name = p.Item.Name,
            Sprite = p.Item.Sprite,
            Category = p.Item.Category,
            Attrs = p.Item.Attrs.ToList(),
        }));
        var status = set is null ? $"You have {ItemSetStore.MaxSets} sets already"
            : build.Picks.All(p => p.IsOwned) ? $"Saved as \"{set.Name}\" (Inventory › Item sets)"
            : $"Saved as \"{set.Name}\": buy the missing items to equip it";
        BuildStatus = status;
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (BuildStatus == status)
            BuildStatus = null;
    }

    // ---- loading --------------------------------------------------------------------------------

    private async Task LoadCategoriesAsync()
    {
        if (BeginLoad() is not { } ct)
            return;
        var r = await _session!.Session.GetShopCategoriesAsync(ct);
        if (ct.IsCancellationRequested)
            return;
        IsLoading = false;
        if (r.IsSuccess)
            Categories = r.Value!.Categories;
        else
            ErrorText = Describe(r.StatusCode, r.Error);
    }

    private async Task LoadItemsAsync(ShopCategory category)
    {
        if (BeginLoad() is not { } ct)
            return;
        var r = await _session!.Session.GetShopItemsAsync(category.Id, ct);
        if (ct.IsCancellationRequested)
            return;
        IsLoading = false;
        if (!r.IsSuccess)
        {
            ErrorText = Describe(r.StatusCode, r.Error);
            return;
        }

        var shop = r.Value!;
        Coins = shop.Coins;
        var level = _session.Account?.Level;
        Items = shop.Items
            .Select(i => new ShopItemViewModel(i, shop.Coins, level))
            .ToList();

        // Reloaded after a purchase: the card shows the item's new state (owned, can_buy).
        if (SelectedItem is { } selected)
            SelectedItem = Items.FirstOrDefault(i => i.Item.Id == selected.Item.Id);
    }

    /// <summary>
    /// Every category's items (one request each, kept until a purchase or the window closes). Null
    /// when cancelled or failed (ErrorText says why). Leaves IsLoading on: the caller ends the load.
    /// </summary>
    private async Task<Dictionary<int, IReadOnlyList<ShopItemViewModel>>?> LoadAllItemsAsync(CancellationToken ct)
    {
        if (_allItems is { } cached)
            return cached;
        var session = _session!;
        if (Categories is null)
        {
            var c = await session.Session.GetShopCategoriesAsync(ct);
            if (ct.IsCancellationRequested)
                return null;
            if (!c.IsSuccess)
            {
                ErrorText = Describe(c.StatusCode, c.Error);
                return null;
            }
            Categories = c.Value!.Categories;
        }

        var all = new Dictionary<int, IReadOnlyList<ShopItemViewModel>>();
        var level = session.Account?.Level;
        var n = 0;
        foreach (var category in Categories)
        {
            LoadingText = $"Reading the shop… {++n}/{Categories.Count}";
            var r = await session.Session.GetShopItemsAsync(category.Id, ct);
            if (ct.IsCancellationRequested)
                return null;
            if (!r.IsSuccess)
            {
                // a category that fails (e.g. a seasonal one) doesn't stop the others
                if (r.StatusCode is 0 or 403)
                {
                    ErrorText = Describe(r.StatusCode, r.Error);
                    return null;
                }
                continue;
            }
            Coins = r.Value!.Coins;
            all[category.Id] = r.Value.Items.Select(i => new ShopItemViewModel(i, r.Value.Coins, level, category.Name)).ToList();
        }
        LoadingText = "Loading…";
        _allItems = all;
        return all;
    }

    private async Task RunSearchAsync()
    {
        if (_allItems is { } cached)
        {
            ShowSearch(cached);
            return;
        }
        if (_searchLoading)
            return; // the load running shows the results of the latest text when it ends
        if (BeginLoad() is not { } ct)
            return;
        _searchLoading = true;
        var all = await LoadAllItemsAsync(ct);
        if (ct.IsCancellationRequested)
            return;
        _searchLoading = false;
        IsLoading = false;
        LoadingText = "Loading…";
        if (all is not null)
            ShowSearch(all);
    }

    private void ShowSearch(Dictionary<int, IReadOnlyList<ShopItemViewModel>> all)
    {
        SearchResults = HasSearch
            ? all.Values.SelectMany(i => i).Where(i => i.Matches(SearchText)).DistinctBy(i => i.Item.Id).Take(200).ToList()
            : null;
    }

    private async Task LoadBuildAsync(bool keepCard = false)
    {
        if (BeginLoad() is not { } ct)
            return;
        var session = _session!;
        var all = await LoadAllItemsAsync(ct);
        if (ct.IsCancellationRequested)
            return;
        if (all is null)
        {
            IsLoading = false;
            return;
        }

        if (_owned is null)
        {
            LoadingText = "Reading your inventory…";
            var inv = await session.Session.GetInventoryAsync(ct);
            if (ct.IsCancellationRequested)
                return;
            _owned = inv.IsSuccess ? inv.Value!.Data : [];
        }
        IsLoading = false;
        LoadingText = "Loading…";

        BuildAttrs = Candidates(all, _owned, useOwned: true)
            .SelectMany(c => c.Attrs)
            .GroupBy(a => a.Name)
            .Select(g => new BuildAttrViewModel(g.Key, g.First().DisplayName))
            .OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        // the saved choice, in its order (attributes no longer on any item are dropped)
        var n = 0;
        foreach (var name in _settings.BuildAttrs)
            if (BuildAttrs.FirstOrDefault(a => a.Name == name) is { Priority: 0 } attr)
                attr.Priority = ++n;
        UpdateBuild();

        if (keepCard && SelectedItem is { } selected)
            SelectedItem = all.Values.SelectMany(i => i).FirstOrDefault(i => i.Item.Id == selected.Item.Id) ?? selected;
    }

    /// <summary>Equipment for slots 0-2 with attributes: shop items and, if asked, the owned ones.</summary>
    private static IEnumerable<BuildCandidate> Candidates(Dictionary<int, IReadOnlyList<ShopItemViewModel>> all, IReadOnlyList<InventoryItem>? owned, bool useOwned)
    {
        var shop = all.Values.SelectMany(i => i)
            .Where(i => IsSlotEquipment(i.Item.Type, i.Item.Category, i.Item.Passive) && i.Item.Attrs.Count > 0)
            .Where(i => useOwned || !i.Item.Owned)
            .Select(i => new BuildCandidate(i.Item.Id, i.Name, i.Item.Sprite, i.Item.Category, i.Item.Price, i.Item.Level, i.Item.Owned, i.Item.Attrs));
        if (!useOwned || owned is null)
            return shop;
        var mine = owned
            .Where(i => IsSlotEquipment(i.Type, i.Category, i.Passive) && i.Attrs.Count > 0)
            .Select(i => new BuildCandidate(i.Id, EquippedItemViewModel.CleanName(i.Name), i.Sprite, i.Category, 0, i.Level, true, i.Attrs));
        return shop.Concat(mine);
    }

    /// <summary>What goes in the equipment slots 0-2: not potions, passive powers or gems (slot 5).</summary>
    internal static bool IsSlotEquipment(string? type, int category, bool passive) =>
        string.Equals(type, "equipment", StringComparison.OrdinalIgnoreCase) && !passive && category != 10;

    private void UpdateBuild()
    {
        if (!IsBuildPage || _allItems is not { } all || BuildAttrs is null)
            return;
        var wanted = PickedAttrs;
        var shopById = all.Values.SelectMany(i => i).GroupBy(i => i.Item.Id).ToDictionary(g => g.Key, g => g.First());
        var results = BuildOptimizer.Find(Candidates(all, _owned, BuildUseOwned), wanted.Select(a => a.Name).ToList(), new BuildOptions
        {
            MaxLevel = BuildMyLevel ? _session?.Account?.Level : null,
            Budget = BuildMyCoins ? Coins : null,
        });
        BuildResults = results.Select((r, i) => new BuildResultViewModel(i + 1, r, wanted, shopById)).ToList();
        OnPropertyChanged(nameof(HasBuildHint));
    }

    /// <summary>Cancels the previous load; returns null (and shows why) when there's nothing to load from.</summary>
    private CancellationToken? BeginLoad()
    {
        CancelLoad();
        ErrorText = null;
        if (!CanBrowse)
        {
            IsLoading = false;
            ErrorText = _session is { IsConnected: true }
                ? "The shop permission wasn't granted. Allow it in the game's popup (restart the game if you refused it)."
                : "No game client connected.";
            return null;
        }
        _loadCts = new CancellationTokenSource();
        IsLoading = true;
        return _loadCts.Token;
    }

    private void CancelLoad()
    {
        _searchLoading = false;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        LoadingText = "Loading…";
    }

    private static string Describe(int status, string? error) => (status, error) switch
    {
        (403, "scope_required" or "addon_scope_revoked") => "The shop permission isn't available for this addon.",
        (0, "not_connected") => "No game client connected.",
        (0, _) => "The game didn't answer. Try again.",
        (504, _) => "The game servers didn't answer in time. Try again.",
        _ => $"Couldn't load the shop ({error ?? status.ToString()}).",
    };

    // ---- following the displayed client ---------------------------------------------------------

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

        // Another account: its prices, discounts and owned items differ.
        Categories = null;
        Coins = null;
        IsBuying = false;
        _allItems = null;
        _owned = null;
        BuildAttrs = null;
        BuildResults = null;
        if (IsOpen)
            Back();
        NotifyAccess();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.HasShopScope) or nameof(SessionViewModel.IsConnected))
        {
            NotifyAccess();
            // Permission just granted while the window shows the "not granted" message.
            if (IsOpen && CanBrowse && Categories is null && !IsLoading && SelectedCategory is null && !IsBuildPage)
                _ = LoadCategoriesAsync();
        }
    }

    private void NotifyAccess()
    {
        OnPropertyChanged(nameof(CardText));
        OnPropertyChanged(nameof(CanBrowse));
        OnPropertyChanged(nameof(CanPressBuy));
    }
}
