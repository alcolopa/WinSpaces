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

    public bool EnableTransitions
    {
        get => _enableTransitions;
        set => SetProperty(ref _enableTransitions, value);
    }

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

        ValidateHotkeys();
    }

    private void SubscribeHotkey(HotkeyItemViewModel item)
    {
        item.PropertyChanged += (s, e) =>
        {
            if (!_isValidating && e.PropertyName is not (nameof(HotkeyItemViewModel.HasConflict) 
                                                      or nameof(HotkeyItemViewModel.ConflictMessage) 
                                                      or nameof(HotkeyItemViewModel.IsEditing)))
            {
                ValidateHotkeys();
            }
        };
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
        if (index < 1 || index > 9) return;

        if (!HotkeyItems.Any(h => h.Action == HotkeyAction.SwitchWorkspace && h.WorkspaceIndex == index))
        {
            var key = 0x30 + index;
            var binding = new HotkeyBinding(HotkeyAction.SwitchWorkspace, index, ModifierKeys.Control, key);
            var item = new HotkeyItemViewModel(binding);
            SubscribeHotkey(item);
            HotkeyItems.Add(item);
        }

        if (!HotkeyItems.Any(h => h.Action == HotkeyAction.MoveToWorkspace && h.WorkspaceIndex == index))
        {
            var key = 0x30 + index;
            var binding = new HotkeyBinding(HotkeyAction.MoveToWorkspace, index, ModifierKeys.Control | ModifierKeys.Shift, key);
            var item = new HotkeyItemViewModel(binding);
            SubscribeHotkey(item);
            HotkeyItems.Add(item);
        }

        ValidateHotkeys();
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
