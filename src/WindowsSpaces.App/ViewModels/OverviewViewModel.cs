using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.ViewModels;

/// <summary>One window tile in the overview.</summary>
public sealed class WindowOverviewViewModel : ViewModelBase
{
    public WindowOverviewViewModel(WindowState state, string workspaceId, bool canPreview)
    {
        Hwnd = state.Hwnd;
        WorkspaceId = workspaceId;
        CanPreview = canPreview;
        ProcessPath = state.ProcessPath ?? string.Empty;
        WindowClass = state.WindowClass ?? string.Empty;
        IsMinimized = state.IsMinimized;

        ProcessName = string.IsNullOrEmpty(ProcessPath)
            ? "Application"
            : Path.GetFileNameWithoutExtension(ProcessPath);

        Title = string.IsNullOrWhiteSpace(state.Title) ? ProcessName : state.Title!;

        var source = !string.IsNullOrWhiteSpace(ProcessName) ? ProcessName : Title;
        Initial = source.Length > 0 ? char.ToUpperInvariant(source[0]).ToString() : "?";
    }

    public nint Hwnd { get; }
    public string Title { get; }
    public string ProcessPath { get; }
    public string ProcessName { get; }
    public string WindowClass { get; }
    public string Initial { get; }
    public bool IsMinimized { get; }

    /// <summary>The space this tile currently sits in. Updated in place when the tile is dragged.</summary>
    public string WorkspaceId { get; internal set; }

    /// <summary>
    /// Whether a live DWM preview can be shown. Only windows in the active
    /// space have one: everything else is SW_HIDE'd and has no composition
    /// surface for DWM to mirror, so those tiles fall back to the letter mark.
    /// </summary>
    public bool CanPreview { get; }

    public Visibility MinimizedBadgeVisibility => IsMinimized ? Visibility.Visible : Visibility.Collapsed;

    public string Subtitle => IsMinimized ? $"{ProcessName} · minimized" : ProcessName;
}

/// <summary>One space column in the overview.</summary>
public sealed class WorkspaceOverviewViewModel : ViewModelBase
{
    private string _name;
    private bool _isActive;
    private bool _isSelected;
    private bool _isDropTarget;
    private bool _isEditingName;
    private bool _canRemove;
    private int _position;

    public WorkspaceOverviewViewModel(
        string monitorId,
        string workspaceId,
        string name,
        int position,
        bool isActive,
        bool canRemove,
        IEnumerable<WindowOverviewViewModel> windows)
    {
        MonitorId = monitorId;
        WorkspaceId = workspaceId;
        _name = name;
        _position = position;
        _isActive = isActive;
        _canRemove = canRemove;
        Windows = new ObservableCollection<WindowOverviewViewModel>(windows);
        Windows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(WindowCountLabel));
            OnPropertyChanged(nameof(EmptyHintVisibility));
        };
    }

    public string MonitorId { get; }
    public string WorkspaceId { get; }
    public ObservableCollection<WindowOverviewViewModel> Windows { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public int Position
    {
        get => _position;
        set
        {
            if (SetProperty(ref _position, value)) OnPropertyChanged(nameof(PositionLabel));
        }
    }

    /// <summary>Shown on the card so the number-key shortcut for it is discoverable. Blank past 9, which has no number key.</summary>
    public string PositionLabel => Position >= 1 && Position <= AppConfiguration.MaxDirectSwitchWorkspaces
        ? Position.ToString()
        : "·";

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(ActiveIndicatorVisibility));
                OnPropertyChanged(nameof(ActiveBorderVisibility));
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) OnPropertyChanged(nameof(SelectionBorderVisibility));
        }
    }

    /// <summary>True while a dragged window tile is hovering this column.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (SetProperty(ref _isDropTarget, value)) OnPropertyChanged(nameof(DropTargetVisibility));
        }
    }

    public bool IsEditingName
    {
        get => _isEditingName;
        set
        {
            if (SetProperty(ref _isEditingName, value))
            {
                OnPropertyChanged(nameof(NameTextVisibility));
                OnPropertyChanged(nameof(NameEditVisibility));
            }
        }
    }

    public bool CanRemove
    {
        get => _canRemove;
        set
        {
            if (SetProperty(ref _canRemove, value)) OnPropertyChanged(nameof(RemoveButtonVisibility));
        }
    }

    public string WindowCountLabel => Windows.Count switch
    {
        0 => "Empty",
        1 => "1 window",
        _ => $"{Windows.Count} windows"
    };

    public string StatusLabel => IsActive ? "On screen" : "Hidden";

    public Visibility ActiveIndicatorVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActiveBorderVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectionBorderVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DropTargetVisibility => IsDropTarget ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyHintVisibility => Windows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveButtonVisibility => CanRemove ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NameTextVisibility => IsEditingName ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NameEditVisibility => IsEditingName ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Re-pushes Name to the UI so a cancelled or rejected inline edit snaps back to the committed value.</summary>
    public void RevertName() => OnPropertyChanged(nameof(Name));
}

/// <summary>
/// Backs the overview pane for one monitor. Every action a user can take in
/// the overview — switch space, focus a window, move a window between spaces,
/// close a window, add/rename/delete a space — routes through here into
/// <see cref="WorkspaceManager"/>, never into Win32 directly.
/// </summary>
public sealed class OverviewViewModel : ViewModelBase
{
    /// <summary>Signatures deliberately match AppHost.AddWorkspace/RemoveWorkspace/RenameWorkspace.</summary>
    public delegate bool AddWorkspaceHandler(string monitorId, out string? newWorkspaceId, out string? error);
    public delegate bool RemoveWorkspaceHandler(string monitorId, string workspaceId, out string? error);
    public delegate bool RenameWorkspaceHandler(string monitorId, string workspaceId, string newName, out string? error);
    public delegate bool MoveWindowToMonitorHandler(nint hwnd, string targetMonitorId, string targetWorkspaceId, out string? error);
    public delegate bool MoveWorkspaceToMonitorHandler(string sourceMonitorId, string workspaceId, string targetMonitorId, out string? newWorkspaceId, out string? error);

    private readonly string _monitorId;
    private readonly WorkspaceManager _workspaceManager;
    private readonly WindowTracker _windowTracker;
    private readonly AddWorkspaceHandler? _addWorkspace;
    private readonly RemoveWorkspaceHandler? _removeWorkspace;
    private readonly RenameWorkspaceHandler? _renameWorkspace;
    private readonly MoveWindowToMonitorHandler? _moveWindowToMonitor;
    private readonly MoveWorkspaceToMonitorHandler? _moveWorkspaceToMonitor;

    private string? _statusMessage;
    private string _monitorLabel;

    public OverviewViewModel(
        string monitorId,
        WorkspaceManager workspaceManager,
        WindowTracker windowTracker,
        AddWorkspaceHandler? addWorkspace = null,
        RemoveWorkspaceHandler? removeWorkspace = null,
        RenameWorkspaceHandler? renameWorkspace = null,
        MoveWindowToMonitorHandler? moveWindowToMonitor = null,
        MoveWorkspaceToMonitorHandler? moveWorkspaceToMonitor = null)
    {
        _monitorId = monitorId;
        _monitorLabel = monitorId;
        _workspaceManager = workspaceManager;
        _windowTracker = windowTracker;
        _addWorkspace = addWorkspace;
        _removeWorkspace = removeWorkspace;
        _renameWorkspace = renameWorkspace;
        _moveWindowToMonitor = moveWindowToMonitor;
        _moveWorkspaceToMonitor = moveWorkspaceToMonitor;

        Workspaces = new ObservableCollection<WorkspaceOverviewViewModel>();
        Refresh();
    }

    /// <summary>
    /// Convenience overload for callers that still hold an
    /// <see cref="AppConfiguration"/>. Seeds the manager's live space list
    /// from config when it has none yet, so the overview shows the configured
    /// spaces rather than nothing.
    /// </summary>
    public OverviewViewModel(string monitorId, WorkspaceManager workspaceManager, WindowTracker windowTracker, AppConfiguration config)
        : this(monitorId, SeedWorkspaces(monitorId, workspaceManager, config), windowTracker, null, null, null)
    {
    }

    private static WorkspaceManager SeedWorkspaces(string monitorId, WorkspaceManager workspaceManager, AppConfiguration config)
    {
        if (workspaceManager.GetWorkspaces(monitorId).Count == 0)
        {
            var monitorConfig = config.Monitors.FirstOrDefault(m => m.MonitorId == monitorId);
            if (monitorConfig is not null && monitorConfig.Workspaces.Count > 0)
            {
                workspaceManager.SetMonitorWorkspaces(monitorId, monitorConfig.Workspaces);
            }
        }

        return workspaceManager;
    }

    public string MonitorId => _monitorId;

    public ObservableCollection<WorkspaceOverviewViewModel> Workspaces { get; }

    public string MonitorLabel
    {
        get => _monitorLabel;
        set => SetProperty(ref _monitorLabel, value);
    }

    /// <summary>Transient feedback (e.g. why a delete was refused). Null when there is nothing to say.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value)) OnPropertyChanged(nameof(StatusVisibility));
        }
    }

    public Visibility StatusVisibility => string.IsNullOrEmpty(StatusMessage) ? Visibility.Collapsed : Visibility.Visible;

    public bool CanAddWorkspace =>
        _addWorkspace is not null && Workspaces.Count < AppConfiguration.MaxWorkspacesPerMonitor;

    public Visibility AddButtonVisibility => CanAddWorkspace ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Raised after the space list or window placement changes, so the view can re-attach previews.</summary>
    public event EventHandler? Refreshed;

    /// <summary>
    /// Rebuilds the whole pane from live state. Cheap enough to run on every
    /// change: a monitor has a handful of spaces and tens of windows.
    /// Selection and in-progress rename are preserved across the rebuild.
    /// </summary>
    public void Refresh()
    {
        var previousSelection = Workspaces.FirstOrDefault(w => w.IsSelected)?.WorkspaceId;
        var editing = Workspaces.FirstOrDefault(w => w.IsEditingName)?.WorkspaceId;

        var definitions = _workspaceManager.GetWorkspaces(_monitorId);
        var activeWorkspaceId = _workspaceManager.GetActiveWorkspace(_monitorId);
        var canRemove = definitions.Count > 1 && _removeWorkspace is not null;

        var windowsByWorkspace = _windowTracker.TrackedWindows.Values
            .Where(w => w.MonitorId == _monitorId && w.WorkspaceId is not null)
            .GroupBy(w => w.WorkspaceId!)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        Workspaces.Clear();

        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            var isActive = definition.Id == activeWorkspaceId;

            var windows = (windowsByWorkspace.GetValueOrDefault(definition.Id) ?? new List<WindowState>())
                .OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(w => new WindowOverviewViewModel(w, definition.Id, isActive));

            Workspaces.Add(new WorkspaceOverviewViewModel(
                _monitorId,
                definition.Id,
                definition.Name,
                i + 1,
                isActive,
                canRemove,
                windows)
            {
                IsSelected = definition.Id == previousSelection,
                IsEditingName = definition.Id == editing
            });
        }

        // Nothing was selected yet (first paint), or the selected space was
        // deleted — fall back to whatever is on screen.
        if (!Workspaces.Any(w => w.IsSelected))
        {
            var fallback = Workspaces.FirstOrDefault(w => w.IsActive) ?? Workspaces.FirstOrDefault();
            if (fallback is not null) fallback.IsSelected = true;
        }

        OnPropertyChanged(nameof(CanAddWorkspace));
        OnPropertyChanged(nameof(AddButtonVisibility));
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    public WorkspaceOverviewViewModel? SelectedWorkspace => Workspaces.FirstOrDefault(w => w.IsSelected);

    public void Select(string workspaceId)
    {
        foreach (var workspace in Workspaces)
        {
            workspace.IsSelected = workspace.WorkspaceId == workspaceId;
        }
    }

    /// <summary>Moves the keyboard selection by <paramref name="delta"/> columns, wrapping around.</summary>
    public void MoveSelection(int delta)
    {
        if (Workspaces.Count == 0) return;

        var current = Workspaces.IndexOf(SelectedWorkspace ?? Workspaces[0]);
        if (current < 0) current = 0;

        var count = Workspaces.Count;
        var next = ((current + delta) % count + count) % count;
        Select(Workspaces[next].WorkspaceId);
    }

    /// <summary>Switches the monitor to a space. Returns false if the space is already showing.</summary>
    public bool SwitchToWorkspace(string workspaceId)
    {
        Select(workspaceId);

        if (_workspaceManager.GetActiveWorkspace(_monitorId) == workspaceId) return false;

        _workspaceManager.SwitchWorkspace(_monitorId, workspaceId);
        Refresh();
        return true;
    }

    /// <summary>Whether this monitor has an Nth space (1-based).</summary>
    public bool HasPosition(int position) => position >= 1 && position <= Workspaces.Count;

    /// <summary>Switches to the Nth space (1-based). Returns false when there is no such space.</summary>
    public bool SwitchToPosition(int position)
    {
        if (position < 1 || position > Workspaces.Count) return false;
        SwitchToWorkspace(Workspaces[position - 1].WorkspaceId);
        return true;
    }

    public void ActivateWindow(nint hwnd) => _workspaceManager.ActivateWindow(hwnd);

    public void CloseWindow(nint hwnd)
    {
        _workspaceManager.CloseWindow(hwnd);

        // The window is gone from the user's point of view the moment they hit
        // the close button; the tracker only learns of it when the app
        // actually exits, which can take a while (or never, if the app puts up
        // a save prompt). Drop the tile now and let the next Refresh reconcile.
        foreach (var workspace in Workspaces)
        {
            var tile = workspace.Windows.FirstOrDefault(w => w.Hwnd == hwnd);
            if (tile is not null)
            {
                workspace.Windows.Remove(tile);
                break;
            }
        }
    }

    /// <summary>
    /// Reassigns a window to another space and reflects it in the tiles
    /// immediately. Returns false when the window is already in that space.
    /// </summary>
    public bool MoveWindowToWorkspace(nint hwnd, string targetWorkspaceId)
    {
        WindowOverviewViewModel? tile = null;
        WorkspaceOverviewViewModel? source = null;

        foreach (var workspace in Workspaces)
        {
            var match = workspace.Windows.FirstOrDefault(w => w.Hwnd == hwnd);
            if (match is not null)
            {
                tile = match;
                source = workspace;
                break;
            }
        }

        if (tile is null || source is null || source.WorkspaceId == targetWorkspaceId) return false;

        var target = Workspaces.FirstOrDefault(w => w.WorkspaceId == targetWorkspaceId);
        if (target is null) return false;

        _workspaceManager.AssignWindow(hwnd, targetWorkspaceId);

        source.Windows.Remove(tile);
        tile.WorkspaceId = targetWorkspaceId;
        target.Windows.Add(tile);

        // A tile's preview only works while its space is on screen, and that
        // just changed for this window in both directions — rebuild so the
        // view re-attaches or drops the DWM thumbnail accordingly.
        Refresh();
        return true;
    }

    /// <summary>
    /// Moves a window to a space on another monitor — the cross-pane drop.
    /// The tile disappears from this pane; the pane owning the target monitor
    /// is refreshed by the drag coordinator.
    /// </summary>
    public bool MoveWindowToMonitor(nint hwnd, string targetMonitorId, string targetWorkspaceId)
    {
        StatusMessage = null;

        if (targetMonitorId == _monitorId) return MoveWindowToWorkspace(hwnd, targetWorkspaceId);

        if (_moveWindowToMonitor is null)
        {
            StatusMessage = "Windows cannot be moved between monitors from here.";
            return false;
        }

        if (!_moveWindowToMonitor(hwnd, targetMonitorId, targetWorkspaceId, out var error))
        {
            StatusMessage = error ?? "Could not move the window to that monitor.";
            Refresh();
            return false;
        }

        Refresh();
        return true;
    }

    /// <summary>
    /// Moves a whole space, with every window on it, to another monitor.
    /// Refused when this monitor would be left with no spaces at all.
    /// </summary>
    public bool MoveWorkspaceToMonitor(string workspaceId, string targetMonitorId)
    {
        StatusMessage = null;

        if (targetMonitorId == _monitorId) return false;

        if (_moveWorkspaceToMonitor is null)
        {
            StatusMessage = "Spaces cannot be moved between monitors from here.";
            return false;
        }

        if (!_moveWorkspaceToMonitor(_monitorId, workspaceId, targetMonitorId, out _, out var error))
        {
            StatusMessage = error ?? "Could not move the space to that monitor.";
            Refresh();
            return false;
        }

        Refresh();
        return true;
    }

    /// <summary>
    /// Creates a space and selects it, without switching to it. Reports why in
    /// StatusMessage on failure.
    ///
    /// Deliberately does NOT switch: adding a space from the overview is an
    /// edit of the space list, not a request to leave the overview. Switching
    /// here would hide the current space behind the panes (and run a slide
    /// transition over them), which reads as the overview disappearing. The
    /// user switches by pressing the space they want.
    /// </summary>
    public bool AddWorkspace()
    {
        StatusMessage = null;

        if (_addWorkspace is null)
        {
            StatusMessage = "Spaces cannot be created from here.";
            return false;
        }

        if (!_addWorkspace(_monitorId, out var newWorkspaceId, out var error))
        {
            StatusMessage = error ?? "Could not create the space.";
            Refresh();
            return false;
        }

        Refresh();

        if (newWorkspaceId is not null)
        {
            Select(newWorkspaceId);
        }

        return true;
    }

    /// <summary>
    /// Deletes a space. Its windows are relocated to a neighbouring space by
    /// the workspace manager rather than being left unreachable.
    /// </summary>
    public bool RemoveWorkspace(string workspaceId)
    {
        StatusMessage = null;

        if (_removeWorkspace is null)
        {
            StatusMessage = "Spaces cannot be deleted from here.";
            return false;
        }

        if (!_removeWorkspace(_monitorId, workspaceId, out var error))
        {
            StatusMessage = error ?? "Could not delete the space.";
            Refresh();
            return false;
        }

        Refresh();
        return true;
    }

    public bool RemoveSelectedWorkspace()
    {
        var selected = SelectedWorkspace;
        if (selected is null) return false;
        return RemoveWorkspace(selected.WorkspaceId);
    }

    /// <summary>Commits an inline rename. An unchanged or empty name is a silent no-op.</summary>
    public bool RenameWorkspace(string workspaceId, string newName)
    {
        StatusMessage = null;

        var workspace = Workspaces.FirstOrDefault(w => w.WorkspaceId == workspaceId);
        if (workspace is not null) workspace.IsEditingName = false;

        var trimmed = (newName ?? string.Empty).Trim();
        if (trimmed.Length == 0 || workspace is null || trimmed == workspace.Name)
        {
            workspace?.RevertName();
            return false;
        }

        if (_renameWorkspace is null)
        {
            StatusMessage = "Spaces cannot be renamed from here.";
            return false;
        }

        if (!_renameWorkspace(_monitorId, workspaceId, trimmed, out var error))
        {
            StatusMessage = error ?? "Could not rename the space.";
            Refresh();
            return false;
        }

        Refresh();
        return true;
    }

    public void BeginRename(string workspaceId)
    {
        foreach (var workspace in Workspaces)
        {
            workspace.IsEditingName = workspace.WorkspaceId == workspaceId;
        }
    }

    public void CancelRename()
    {
        foreach (var workspace in Workspaces)
        {
            workspace.IsEditingName = false;
        }
    }
}
