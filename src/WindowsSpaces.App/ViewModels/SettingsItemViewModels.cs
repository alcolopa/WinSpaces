using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WindowsSpaces.App.Helpers;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public sealed class HotkeyItemViewModel : ViewModelBase
{
    private HotkeyAction _action;
    private int _workspaceIndex;
    private ModifierKeys _modifiers;
    private int _virtualKey;
    private bool _isEditing;
    private bool _hasConflict;
    private string? _conflictMessage;
    private bool _representsDigitGroup;

    public HotkeyItemViewModel(HotkeyBinding binding)
    {
        _action = binding.Action;
        _workspaceIndex = binding.WorkspaceIndex;
        _modifiers = binding.Modifiers;
        _virtualKey = binding.VirtualKey;
        UpdateDerivedProperties();
    }

    public IReadOnlyList<KeyOption> AvailableKeys => VirtualKeyHelper.AvailableKeys;

    public HotkeyAction Action
    {
        get => _action;
        set { if (SetProperty(ref _action, value)) UpdateDerivedProperties(); }
    }

    public int WorkspaceIndex
    {
        get => _workspaceIndex;
        set { if (SetProperty(ref _workspaceIndex, value)) UpdateDerivedProperties(); }
    }

    public ModifierKeys Modifiers
    {
        get => _modifiers;
        set
        {
            if (SetProperty(ref _modifiers, value))
            {
                UpdateDerivedProperties();
                OnPropertyChanged(nameof(IsWin));
                OnPropertyChanged(nameof(IsCtrl));
                OnPropertyChanged(nameof(IsAlt));
                OnPropertyChanged(nameof(IsShift));
            }
        }
    }

    public int VirtualKey
    {
        get => _virtualKey;
        set
        {
            if (SetProperty(ref _virtualKey, value))
            {
                UpdateDerivedProperties();
                OnPropertyChanged(nameof(SelectedKey));
            }
        }
    }

    public bool IsWin
    {
        get => Modifiers.HasFlag(ModifierKeys.Win);
        set => SetModifierFlag(ModifierKeys.Win, value);
    }

    public bool IsCtrl
    {
        get => Modifiers.HasFlag(ModifierKeys.Control);
        set => SetModifierFlag(ModifierKeys.Control, value);
    }

    public bool IsAlt
    {
        get => Modifiers.HasFlag(ModifierKeys.Alt);
        set => SetModifierFlag(ModifierKeys.Alt, value);
    }

    public bool IsShift
    {
        get => Modifiers.HasFlag(ModifierKeys.Shift);
        set => SetModifierFlag(ModifierKeys.Shift, value);
    }

    private void SetModifierFlag(ModifierKeys flag, bool enabled)
    {
        var current = Modifiers;
        var updated = enabled ? (current | flag) : (current & ~flag);
        Modifiers = updated;
    }

    public KeyOption SelectedKey
    {
        get => VirtualKeyHelper.AvailableKeys.FirstOrDefault(k => k.VirtualKey == _virtualKey) 
               ?? new KeyOption(_virtualKey, VirtualKeyHelper.GetKeyName(_virtualKey));
        set
        {
            if (value != null && value.VirtualKey != _virtualKey)
            {
                VirtualKey = value.VirtualKey;
            }
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    /// <summary>
    /// True for the single row shown in the Shortcuts page that stands in
    /// for the whole family of per-space bindings (Switch/Move to Space
    /// 1 through <see cref="AppConfiguration.MaxDirectSwitchWorkspaces"/>) —
    /// set by <see cref="SettingsViewModel"/>, never by this class itself.
    /// Editing its modifiers is what lets the user set the combo once
    /// instead of once per space number; the key picker is hidden for it
    /// since the digit is implied per space, not a single fixed key.
    /// </summary>
    public bool RepresentsDigitGroup
    {
        get => _representsDigitGroup;
        set
        {
            if (SetProperty(ref _representsDigitGroup, value))
            {
                OnPropertyChanged(nameof(ShowKeyPicker));
                UpdateDerivedProperties();
            }
        }
    }

    public bool ShowKeyPicker => !RepresentsDigitGroup;

    public bool HasConflict
    {
        get => _hasConflict;
        set => SetProperty(ref _hasConflict, value);
    }

    public string? ConflictMessage
    {
        get => _conflictMessage;
        set => SetProperty(ref _conflictMessage, value);
    }

    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public string CategoryGlyph { get; private set; } = string.Empty;
    public string FormattedDisplay { get; private set; } = string.Empty;
    public IReadOnlyList<string> KeyPills { get; private set; } = Array.Empty<string>();

    private void UpdateDerivedProperties()
    {
        Title = Action switch
        {
            HotkeyAction.SwitchWorkspace => RepresentsDigitGroup ? "Switch to Space" : $"Switch to Space {WorkspaceIndex}",
            HotkeyAction.MoveToWorkspace => RepresentsDigitGroup ? "Move Window to Space" : $"Move Window to Space {WorkspaceIndex}",
            HotkeyAction.ShowAllWindows => "Show All Windows",
            HotkeyAction.ShowOverview => "Spaces Overview",
            HotkeyAction.NextWorkspace => "Next Space",
            HotkeyAction.PreviousWorkspace => "Previous Space",
            HotkeyAction.MoveToNextWorkspace => "Move Window to Next Space",
            HotkeyAction.MoveToPreviousWorkspace => "Move Window to Previous Space",
            HotkeyAction.CreateWorkspace => "New Space",
            HotkeyAction.CloseWorkspace => "Delete Current Space",
            HotkeyAction.MoveToNextMonitor => "Move Window to Next Monitor",
            HotkeyAction.MoveToPreviousMonitor => "Move Window to Previous Monitor",
            _ => Action.ToString()
        };

        Category = Action switch
        {
            HotkeyAction.SwitchWorkspace => "Workspace Navigation",
            HotkeyAction.NextWorkspace => "Workspace Navigation",
            HotkeyAction.PreviousWorkspace => "Workspace Navigation",
            HotkeyAction.MoveToWorkspace => "Window Management",
            HotkeyAction.MoveToNextWorkspace => "Window Management",
            HotkeyAction.MoveToPreviousWorkspace => "Window Management",
            HotkeyAction.CreateWorkspace => "Manage Spaces",
            HotkeyAction.CloseWorkspace => "Manage Spaces",
            HotkeyAction.ShowAllWindows => "Recovery & Utilities",
            HotkeyAction.ShowOverview => "Recovery & Utilities",
            HotkeyAction.MoveToNextMonitor => "Window Management",
            HotkeyAction.MoveToPreviousMonitor => "Window Management",
            _ => "General"
        };

        CategoryGlyph = Action switch
        {
            HotkeyAction.SwitchWorkspace => "\uE7C4", // Switch
            HotkeyAction.NextWorkspace => "\uE72A", // Forward
            HotkeyAction.PreviousWorkspace => "\uE72B", // Back
            HotkeyAction.MoveToWorkspace => "\uE8C8", // Move window
            HotkeyAction.MoveToNextWorkspace => "\uE8C8",
            HotkeyAction.MoveToPreviousWorkspace => "\uE8C8",
            HotkeyAction.CreateWorkspace => "\uE710", // Add
            HotkeyAction.CloseWorkspace => "\uE74D", // Delete
            HotkeyAction.ShowAllWindows => "\uE737", // Eye / View
            HotkeyAction.ShowOverview => "\uE7F4", // Tiles / ViewAll
            HotkeyAction.MoveToNextMonitor => "\uE8A9", // MapPin-ish / go-to
            HotkeyAction.MoveToPreviousMonitor => "\uE8A9",
            _ => "\uE765"
        };

        Description = Action switch
        {
            HotkeyAction.SwitchWorkspace => RepresentsDigitGroup
                ? $"Switches active workspace on the current monitor to Space N — this combo plus 1-{AppConfiguration.MaxDirectSwitchWorkspaces} switches to that numbered space."
                : $"Switches active workspace on the current monitor to Space {WorkspaceIndex}.",
            HotkeyAction.MoveToWorkspace => RepresentsDigitGroup
                ? $"Moves the currently focused window to Space N on its monitor — this combo plus 1-{AppConfiguration.MaxDirectSwitchWorkspaces} moves it to that numbered space."
                : $"Moves the currently focused window to Space {WorkspaceIndex} on its monitor.",
            HotkeyAction.ShowAllWindows => "Unhides all tracked windows across all monitors (emergency recovery).",
            HotkeyAction.ShowOverview => "Opens the interactive Spaces Overview workspace layout across monitors.",
            HotkeyAction.NextWorkspace => "Cycles the monitor under the pointer to the next space, wrapping past the last one.",
            HotkeyAction.PreviousWorkspace => "Cycles the monitor under the pointer to the previous space, wrapping past the first one.",
            HotkeyAction.MoveToNextWorkspace => "Sends the focused window to the next space on its monitor and follows it there.",
            HotkeyAction.MoveToPreviousWorkspace => "Sends the focused window to the previous space on its monitor and follows it there.",
            HotkeyAction.CreateWorkspace => "Creates a new space on the monitor under the pointer and switches to it.",
            HotkeyAction.CloseWorkspace => "Deletes the space showing on the monitor under the pointer; its windows move to a neighbouring space.",
            HotkeyAction.MoveToNextMonitor => "Sends the focused window to the next monitor's active space and follows it there.",
            HotkeyAction.MoveToPreviousMonitor => "Sends the focused window to the previous monitor's active space and follows it there.",
            _ => string.Empty
        };

        if (RepresentsDigitGroup)
        {
            var modifierPills = VirtualKeyHelper.GetKeyPills(Modifiers, VirtualKey);
            modifierPills[^1] = $"1-{AppConfiguration.MaxDirectSwitchWorkspaces}";
            KeyPills = modifierPills;
            FormattedDisplay = string.Join(" + ", modifierPills);
        }
        else
        {
            FormattedDisplay = VirtualKeyHelper.FormatHotkey(Modifiers, VirtualKey);
            KeyPills = VirtualKeyHelper.GetKeyPills(Modifiers, VirtualKey);
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(CategoryGlyph));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(FormattedDisplay));
        OnPropertyChanged(nameof(KeyPills));
    }

    public HotkeyBinding ToBinding() => new(Action, WorkspaceIndex, Modifiers, VirtualKey);
}

public sealed class WorkspaceItemViewModel : ViewModelBase
{
    private string _name;
    private bool _isEditing;

    public WorkspaceItemViewModel(WorkspaceDefinition def)
    {
        Id = def.Id;
        Index = def.Index;
        _name = def.Name;
    }

    public string Id { get; }
    public int Index { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    public WorkspaceDefinition ToDefinition() => new(Id, Name, Index);
}

public sealed class MonitorItemViewModel : ViewModelBase
{
    public MonitorItemViewModel(MonitorWorkspaceConfig config)
    {
        MonitorId = config.MonitorId;
        Workspaces = new ObservableCollection<WorkspaceItemViewModel>(
            config.Workspaces.Select(w => new WorkspaceItemViewModel(w)));
    }

    public string MonitorId { get; }

    /// <summary>
    /// The id without its <c>\\.\</c> device-namespace prefix. Display only —
    /// <see cref="MonitorId"/> stays the identity everything is keyed on.
    /// </summary>
    public string MonitorDisplayName => MonitorNaming.ToDisplayName(MonitorId);

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; }

    public MonitorWorkspaceConfig ToConfig() =>
        new(MonitorId, Workspaces.Select(w => w.ToDefinition()).ToList());
}

public sealed class RuleItemViewModel : ViewModelBase
{
    private string _ruleName;
    private string? _processPath;
    private string? _windowClass;
    private string? _windowTitle;
    private string _targetMonitorId;
    private int _targetWorkspaceIndex;

    public RuleItemViewModel(ApplicationRule rule)
    {
        Id = rule.Id;
        _ruleName = rule.RuleName;
        _processPath = rule.ProcessPath;
        _windowClass = rule.WindowClass;
        _windowTitle = rule.WindowTitle;
        _targetMonitorId = rule.TargetMonitorId;
        _targetWorkspaceIndex = rule.TargetWorkspaceIndex;
    }

    public string Id { get; }

    public string RuleName
    {
        get => _ruleName;
        set => SetProperty(ref _ruleName, value);
    }

    public string? ProcessPath
    {
        get => _processPath;
        set
        {
            if (SetProperty(ref _processPath, value))
            {
                OnPropertyChanged(nameof(ProcessDisplay));
            }
        }
    }

    public string? WindowClass
    {
        get => _windowClass;
        set => SetProperty(ref _windowClass, value);
    }

    public string? WindowTitle
    {
        get => _windowTitle;
        set => SetProperty(ref _windowTitle, value);
    }

    public string TargetMonitorId
    {
        get => _targetMonitorId;
        set
        {
            if (SetProperty(ref _targetMonitorId, value)) OnPropertyChanged(nameof(TargetMonitorDisplayName));
        }
    }

    /// <summary>Display-only form of <see cref="TargetMonitorId"/>.</summary>
    public string TargetMonitorDisplayName => MonitorNaming.ToDisplayName(TargetMonitorId);

    public int TargetWorkspaceIndex
    {
        get => _targetWorkspaceIndex;
        set
        {
            if (SetProperty(ref _targetWorkspaceIndex, value))
            {
                OnPropertyChanged(nameof(TargetWorkspaceDisplay));
            }
        }
    }

    public string ProcessDisplay => string.IsNullOrWhiteSpace(ProcessPath) ? "Any application" : ProcessPath;
    public string TargetWorkspaceDisplay => $"Space {TargetWorkspaceIndex}";

    public ApplicationRule ToRule() => new(Id, RuleName, ProcessPath, WindowClass, WindowTitle, TargetMonitorId, TargetWorkspaceIndex);
}
