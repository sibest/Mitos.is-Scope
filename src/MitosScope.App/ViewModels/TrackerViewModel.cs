using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MitosScope.Core.Sessions;

namespace MitosScope.App.ViewModels;

/// <summary>
/// Root state shared by all panels: the connected clients and which one the panels display.
/// SessionManager events arrive on background threads and are marshalled here.
/// </summary>
public partial class TrackerViewModel : ViewModelBase
{
    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    /// <summary>The client the panels show (lowest slot by default).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrent))]
    public partial SessionViewModel? Current { get; set; }

    public bool HasCurrent => Current is not null;

    /// <summary>Fatal problem shown in the status panel (e.g. missing app key).</summary>
    [ObservableProperty]
    public partial string? StartupError { get; set; }

    [ObservableProperty]
    public partial string SearchingText { get; set; } = "Looking for the game…";

    /// <summary>Game processes running without a reachable API (see SessionManager.GetProcessesWithoutApi).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstancesWithoutApi))]
    public partial IReadOnlyList<int> InstancesWithoutApi { get; private set; } = [];

    public bool HasInstancesWithoutApi => InstancesWithoutApi.Count > 0;

    private SessionManager? _manager;

    public void Attach(SessionManager manager)
    {
        _manager = manager;
        manager.SessionAdded += s => Dispatcher.UIThread.Post(() => { OnAdded(s); RefreshInstancesWithoutApi(); });
        manager.SessionRemoved += s => Dispatcher.UIThread.Post(() => { OnRemoved(s); RefreshInstancesWithoutApi(); });
        manager.GameProcessesChanged += () => Dispatcher.UIThread.Post(RefreshInstancesWithoutApi);
    }

    private void RefreshInstancesWithoutApi()
    {
        var pids = _manager?.GetProcessesWithoutApi() ?? [];
        if (!pids.SequenceEqual(InstancesWithoutApi))
            InstancesWithoutApi = pids;
    }

    [RelayCommand]
    private void Select(SessionViewModel session) => Current = session;

    // ---- item icons download (ItemsPackUpdater), shown as a loader in the panels ----

    /// <summary>What the items pack download is doing; null when nothing is in progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdatingItems))]
    public partial string? ItemsUpdateText { get; set; }

    /// <summary>0..100, or -1 while the size isn't known (indeterminate bar).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ItemsUpdateIndeterminate))]
    public partial double ItemsUpdatePercent { get; set; } = -1;

    public bool IsUpdatingItems => ItemsUpdateText is not null;
    public bool ItemsUpdateIndeterminate => ItemsUpdatePercent < 0;

    /// <summary>Called from any thread by ItemsPackUpdater.</summary>
    public void ReportItemsUpdate(string? text, double fraction) =>
        Dispatcher.UIThread.Post(() =>
        {
            ItemsUpdateText = text;
            ItemsUpdatePercent = fraction < 0 ? -1 : fraction * 100;
        });

    /// <summary>New item icons (items pack updated): rebuild the equipment rows now instead of at the next poll.</summary>
    public void RefreshItemIcons()
    {
        foreach (var session in Sessions)
            session.Refresh(SessionChange.Inventory);
    }

    private int? _focusedProcessId;

    /// <summary>A game client got focus: show the session served by that process (if connected yet).</summary>
    public void FocusProcess(int processId)
    {
        _focusedProcessId = processId;
        if (Sessions.FirstOrDefault(s => s.Session.ProcessId == processId) is { } match)
            Current = match;
    }

    private void OnAdded(ClientSession session)
    {
        // Subscribe first, then pull everything: data that arrived before this Post ran
        // (e.g. auth answered instantly) would otherwise be lost until the next poll.
        var vm = new SessionViewModel(session);
        session.Changed += (_, change) => Dispatcher.UIThread.Post(() => vm.Refresh(change));
        vm.Refresh(SessionChange.All);

        var index = 0;
        while (index < Sessions.Count && Sessions[index].Slot < vm.Slot)
            index++;
        Sessions.Insert(index, vm);

        // The focused client wins; otherwise just make sure something is selected.
        if (Current is null || (_focusedProcessId is not null && vm.Session.ProcessId == _focusedProcessId))
            Current = vm;
    }

    private void OnRemoved(ClientSession session)
    {
        var vm = Sessions.FirstOrDefault(s => s.Session == session);
        if (vm is null)
            return;
        Sessions.Remove(vm);
        if (Current == vm)
            Current = Sessions.FirstOrDefault();
    }
}
