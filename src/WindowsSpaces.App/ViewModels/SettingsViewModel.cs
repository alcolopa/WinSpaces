using System.Collections.ObjectModel;
using WindowsSpaces.App.Helpers;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppConfiguration _original;
    private bool _enableTransitions;
    private bool _isValidating;

    public ObservableCollection<MonitorItemViewModel> MonitorItems { get; }
    public ObservableCollection<HotkeyItemViewModel> HotkeyItems { get; }
    public ObservableCollection<RuleItemViewModel> RuleItems { get; }
    public ObservableCollection<WorkspaceProfile> ProfileItems { get; }

    /// <summary>
    /// What the Shortcuts page actually shows: every non-per-space binding,
    /// plus exactly one row per per-space action (Switch/Move to Space)
    /// standing in for the whole 1-N family, instead of one row per space
    /// number. <see cref="HotkeyItems"/> stays the full flat list — it's
    /// still what gets persisted, registered, and conflict-checked; this is
    /// purely what the user edits.
    /// </summary>
    public ObservableCollection<HotkeyItemViewModel> DisplayedHotkeyItems { get; } = new();

    private static readonly HotkeyAction[] PerSpaceActions = { HotkeyAction.SwitchWorkspace, HotkeyAction.MoveToWorkspace };

    public bool EnableTransitions
    {
        get => _enableTransitions;
        set { if (SetProperty(ref _enableTransitions, value)) RaiseChanged(); }
    }

    /// <summary>
    /// Raised when an edit has produced settings worth applying. The window
    /// listens for this instead of a Save button.
    ///
    /// A shortcut row is deliberately silent while it is being edited and
    /// speaks up only when its edit toggle closes: registering a half-typed
    /// combination would take that key away from every other application on
    /// the machine for as long as it took to finish typing.
    /// </summary>
    public event EventHandler? Changed;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public SettingsViewModel(AppConfiguration current)
    {
        _original = current;
        _enableTransitions = current.EnableTransitions;

        MonitorItems = new ObservableCollection<MonitorItemViewModel>(
            current.Monitors.Select(m => new MonitorItemViewModel(m)));

        HotkeyItems = new ObservableCollection<HotkeyItemViewModel>(
            current.Hotkeys.Select(h => new HotkeyItemViewModel(h)));

        RuleItems = new ObservableCollection<RuleItemViewModel>(
            current.ActiveRules.Select(r => new RuleItemViewModel(r)));

        ProfileItems = new ObservableCollection<WorkspaceProfile>(current.ActiveProfiles);

        foreach (var hk in HotkeyItems)
        {
            SubscribeHotkey(hk);
        }

        WatchForChanges();
        ValidateHotkeys();
        RebuildDisplayedHotkeyItems();
    }

    /// <summary>
    /// Recomputes <see cref="DisplayedHotkeyItems"/> from <see cref="HotkeyItems"/>.
    /// For each per-space action, the lowest-numbered space's row is flagged
    /// as the group representative (editing it is what the Shortcuts page
    /// exposes) and is the only one of that action shown; every other
    /// row — including its own siblings, which stay in HotkeyItems for
    /// persistence — is filtered out of the display.
    /// </summary>
    private void RebuildDisplayedHotkeyItems()
    {
        foreach (var item in HotkeyItems) item.RepresentsDigitGroup = false;

        var representatives = PerSpaceActions.ToDictionary(
            action => action,
            action => HotkeyItems.Where(h => h.Action == action).OrderBy(h => h.WorkspaceIndex).FirstOrDefault());

        foreach (var rep in representatives.Values)
        {
            if (rep is not null) rep.RepresentsDigitGroup = true;
        }

        DisplayedHotkeyItems.Clear();
        foreach (var item in HotkeyItems)
        {
            var isPerSpace = PerSpaceActions.Contains(item.Action);
            if (!isPerSpace || item.RepresentsDigitGroup)
            {
                DisplayedHotkeyItems.Add(item);
            }
        }
    }

    /// <summary>
    /// Subscribes to every part of the edit graph — the collections, the
    /// items in them, and, for a monitor, its own list of spaces — so any
    /// edit anywhere in the window reaches <see cref="Changed"/>.
    /// </summary>
    private void WatchForChanges()
    {
        WatchCollection(MonitorItems, WatchMonitor);
        WatchCollection(RuleItems, WatchItem);
        WatchCollection(ProfileItems, _ => { });
        WatchCollection(HotkeyItems, _ => { });
    }

    private void WatchCollection<T>(ObservableCollection<T> collection, Action<T> watchItem)
    {
        foreach (var item in collection) watchItem(item);

        collection.CollectionChanged += (_, e) =>
        {
            foreach (var item in e.NewItems?.Cast<T>() ?? Enumerable.Empty<T>()) watchItem(item);
            RaiseChanged();
        };
    }

    private void WatchItem(ViewModelBase item) => item.PropertyChanged += (_, _) => RaiseChanged();

    private void WatchMonitor(MonitorItemViewModel monitor)
    {
        WatchItem(monitor);
        WatchCollection(monitor.Workspaces, WatchItem);
    }

    private void SubscribeHotkey(HotkeyItemViewModel item)
    {
        item.PropertyChanged += (s, e) =>
        {
            if (_isValidating) return;

            if (e.PropertyName is nameof(HotkeyItemViewModel.IsEditing))
            {
                // Closing the editor is the commit point for a shortcut —
                // and, for the group-representative row, also the point its
                // new combo mirrors onto every hidden sibling sharing its
                // action (each keeps its own digit key; only the combo is
                // shared). Not propagated on every checkbox toggle mid-edit,
                // or a half-chosen combination would apply to every space
                // number for as long as it took to finish editing.
                if (!item.IsEditing)
                {
                    PropagateGroupModifiers(item);
                    RaiseChanged();
                }
                return;
            }

            if (e.PropertyName is nameof(HotkeyItemViewModel.HasConflict)
                               or nameof(HotkeyItemViewModel.ConflictMessage))
            {
                return;
            }

            // A change made outside the editor (e.g. Rebind()) has no
            // closing-the-editor moment to propagate at, so do it inline —
            // but only then: while item.IsEditing is true this same
            // PropertyChanged fires on every checkbox toggle, and the
            // IsEditing-closing branch above is what actually commits it.
            if (!item.IsEditing && item.RepresentsDigitGroup && e.PropertyName == nameof(HotkeyItemViewModel.Modifiers))
            {
                PropagateGroupModifiers(item);
            }

            ValidateHotkeys();

            // A row edited outside the editor toggle (the reset-to-defaults
            // button rewrites every row) still has to reach the host.
            if (!item.IsEditing) RaiseChanged();
        };
    }

    private void PropagateGroupModifiers(HotkeyItemViewModel representative)
    {
        if (!representative.RepresentsDigitGroup) return;

        foreach (var sibling in HotkeyItems.Where(h => h.Action == representative.Action && h != representative))
        {
            sibling.Modifiers = representative.Modifiers;
        }
    }

    // Backward-compatibility for existing tests & callers
    public IReadOnlyList<MonitorWorkspaceConfig> Monitors =>
        MonitorItems.Select(m => m.ToConfig()).ToList();

    public IReadOnlyList<HotkeyBinding> Bindings =>
        HotkeyItems.Select(h => h.ToBinding()).ToList();

    public void AddWorkspace(string monitorId)
    {
        var monitor = MonitorItems.FirstOrDefault(m => m.MonitorId == monitorId);
        if (monitor is null)
        {
            throw new ArgumentException($"Unknown monitor '{monitorId}'", nameof(monitorId));
        }

        var nextIndex = monitor.Workspaces.Count == 0 ? 1 : monitor.Workspaces.Max(w => w.Index) + 1;
        var newDef = new WorkspaceDefinition($"{monitorId}:{nextIndex}", $"Space {nextIndex}", nextIndex);
        monitor.Workspaces.Add(new WorkspaceItemViewModel(newDef));

        EnsureHotkeysForWorkspaceIndex(nextIndex);
    }

    private void EnsureHotkeysForWorkspaceIndex(int index)
    {
        // Only the first nine spaces get a number-key binding; past that,
        // next/previous and the overview are how a space is reached.
        if (index < 1 || index > AppConfiguration.MaxDirectSwitchWorkspaces) return;

        // Ctrl+Alt+N and Ctrl+Alt+Shift+N, matching the shipped defaults —
        // unless the user already customized the combo via the group row,
        // in which case a new space number picks up that same combo rather
        // than reverting to the default. Plain Ctrl+N would be registered
        // system-wide and take tab switching away from every browser and
        // editor on the machine.
        if (!HotkeyItems.Any(h => h.Action == HotkeyAction.SwitchWorkspace && h.WorkspaceIndex == index))
        {
            var key = 0x30 + index;
            var modifiers = HotkeyItems.FirstOrDefault(h => h.Action == HotkeyAction.SwitchWorkspace)?.Modifiers
                             ?? ModifierKeys.Control | ModifierKeys.Alt;
            var binding = new HotkeyBinding(HotkeyAction.SwitchWorkspace, index, modifiers, key);
            var item = new HotkeyItemViewModel(binding);
            SubscribeHotkey(item);
            HotkeyItems.Add(item);
        }

        if (!HotkeyItems.Any(h => h.Action == HotkeyAction.MoveToWorkspace && h.WorkspaceIndex == index))
        {
            var key = 0x30 + index;
            var modifiers = HotkeyItems.FirstOrDefault(h => h.Action == HotkeyAction.MoveToWorkspace)?.Modifiers
                             ?? ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;
            var binding = new HotkeyBinding(HotkeyAction.MoveToWorkspace, index, modifiers, key);
            var item = new HotkeyItemViewModel(binding);
            SubscribeHotkey(item);
            HotkeyItems.Add(item);
        }

        ValidateHotkeys();
        RebuildDisplayedHotkeyItems();
    }

    public void RemoveWorkspace(string monitorId, string workspaceId)
    {
        var monitor = MonitorItems.FirstOrDefault(m => m.MonitorId == monitorId);
        if (monitor is null)
        {
            throw new ArgumentException($"Unknown monitor '{monitorId}'", nameof(monitorId));
        }

        var target = monitor.Workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (target != null)
        {
            monitor.Workspaces.Remove(target);
        }
    }

    public void RenameWorkspace(string monitorId, string workspaceId, string newName)
    {
        var monitor = MonitorItems.FirstOrDefault(m => m.MonitorId == monitorId);
        if (monitor is null)
        {
            throw new ArgumentException($"Unknown monitor '{monitorId}'", nameof(monitorId));
        }

        var target = monitor.Workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (target != null)
        {
            target.Name = newName;
        }
    }

    public void Rebind(HotkeyAction action, int workspaceIndex, ModifierKeys modifiers, int virtualKey)
    {
        var item = HotkeyItems.FirstOrDefault(b => b.Action == action && b.WorkspaceIndex == workspaceIndex);
        if (item is null)
        {
            throw new ArgumentException($"No existing binding for {action}/{workspaceIndex}");
        }

        item.Modifiers = modifiers;
        item.VirtualKey = virtualKey;
        ValidateHotkeys();
    }

    public void ResetHotkeysToDefault()
    {
        var dummyMonitors = MonitorItems.Select(m => new WindowsSpaces.Core.Monitor(m.MonitorId, m.MonitorId, new System.Drawing.Rectangle(0, 0, 1920, 1080), IsPrimary: true));
        var defConfig = AppConfiguration.CreateDefault(dummyMonitors);

        HotkeyItems.Clear();
        foreach (var hk in defConfig.Hotkeys)
        {
            var item = new HotkeyItemViewModel(hk);
            SubscribeHotkey(item);
            HotkeyItems.Add(item);
        }

        ValidateHotkeys();
        RebuildDisplayedHotkeyItems();
    }

    public void ValidateHotkeys()
    {
        if (_isValidating) return;
        _isValidating = true;

        try
        {
            for (int i = 0; i < HotkeyItems.Count; i++)
            {
                var itemA = HotkeyItems[i];
                var conflictFound = false;

                for (int j = 0; j < HotkeyItems.Count; j++)
                {
                    if (i == j) continue;
                    var itemB = HotkeyItems[j];

                    if (itemA.Modifiers == itemB.Modifiers && itemA.VirtualKey == itemB.VirtualKey)
                    {
                        itemA.HasConflict = true;
                        itemA.ConflictMessage = $"Conflicts with '{itemB.Title}'";
                        conflictFound = true;
                        break;
                    }
                }

                if (!conflictFound)
                {
                    itemA.HasConflict = false;
                    itemA.ConflictMessage = null;
                }
            }
        }
        finally
        {
            _isValidating = false;
        }
    }

    public bool TrySave(out AppConfiguration updated, out string? error)
    {
        ValidateHotkeys();

        var monitorConfigs = MonitorItems.Select(m => m.ToConfig()).ToList();
        var hotkeyBindings = HotkeyItems.Select(h => h.ToBinding()).ToList();
        var rules = RuleItems.Select(r => r.ToRule()).ToList();

        var candidate = _original with
        {
            Monitors = monitorConfigs,
            Hotkeys = hotkeyBindings,
            Rules = rules,
            Profiles = ProfileItems.ToList(),
            EnableTransitions = _enableTransitions
        };

        if (!candidate.Validate(out error))
        {
            updated = _original;
            return false;
        }

        updated = candidate;
        return true;
    }
}
