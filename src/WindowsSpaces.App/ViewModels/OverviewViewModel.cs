using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.ViewModels;

public sealed class WindowOverviewViewModel
{
    public nint Hwnd { get; }
    public string Title { get; }
    public string ProcessPath { get; }
    public string WindowClass { get; }

    public WindowOverviewViewModel(WindowState state)
    {
        Hwnd = state.Hwnd;
        Title = string.IsNullOrEmpty(state.Title) ? "Application" : state.Title;
        ProcessPath = state.ProcessPath ?? string.Empty;
        WindowClass = state.WindowClass ?? string.Empty;
    }
}

public sealed class WorkspaceOverviewViewModel
{
    public string WorkspaceId { get; }
    public string Name { get; }
    public bool IsActive { get; }
    public ObservableCollection<WindowOverviewViewModel> Windows { get; }

    public Microsoft.UI.Xaml.Visibility ActiveIndicatorVisibility => IsActive ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility ActiveBorderVisibility => IsActive ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public WorkspaceOverviewViewModel(string workspaceId, string name, bool isActive, IEnumerable<WindowOverviewViewModel> windows)
    {
        WorkspaceId = workspaceId;
        Name = name;
        IsActive = isActive;
        Windows = new ObservableCollection<WindowOverviewViewModel>(windows);
    }
}

public sealed class OverviewViewModel
{
    private readonly string _monitorId;
    private readonly WorkspaceManager _workspaceManager;
    private readonly WindowTracker _windowTracker;
    private readonly List<WorkspaceOverviewViewModel> _workspaces = new();

    public OverviewViewModel(string monitorId, WorkspaceManager workspaceManager, WindowTracker windowTracker, AppConfiguration config)
    {
        _monitorId = monitorId;
        _workspaceManager = workspaceManager;
        _windowTracker = windowTracker;

        LoadWorkspaces(config);
    }

    public string MonitorId => _monitorId;
    public IReadOnlyList<WorkspaceOverviewViewModel> Workspaces => _workspaces;

    private void LoadWorkspaces(AppConfiguration config)
    {
        var monitorConfig = config.Monitors.FirstOrDefault(m => m.MonitorId == _monitorId);
        if (monitorConfig is null) return;

        var activeWorkspaceId = _workspaceManager.GetActiveWorkspace(_monitorId);
        var windowsByWorkspace = _windowTracker.TrackedWindows.Values
            .Where(w => w.MonitorId == _monitorId)
            .GroupBy(w => w.WorkspaceId)
            .ToDictionary(g => g.Key ?? string.Empty, g => g.Select(w => new WindowOverviewViewModel(w)).ToList());

        foreach (var wsDef in monitorConfig.Workspaces)
        {
            var wsWindows = windowsByWorkspace.GetValueOrDefault(wsDef.Id) ?? new List<WindowOverviewViewModel>();
            var isActive = wsDef.Id == activeWorkspaceId;
            _workspaces.Add(new WorkspaceOverviewViewModel(wsDef.Id, wsDef.Name, isActive, wsWindows));
        }
    }

    public void MoveWindowToWorkspace(nint hwnd, string targetWorkspaceId)
    {
        WindowOverviewViewModel? windowVm = null;
        WorkspaceOverviewViewModel? sourceWs = null;

        foreach (var ws in _workspaces)
        {
            var match = ws.Windows.FirstOrDefault(w => w.Hwnd == hwnd);
            if (match is not null)
            {
                windowVm = match;
                sourceWs = ws;
                break;
            }
        }

        if (windowVm is not null && sourceWs is not null && sourceWs.WorkspaceId != targetWorkspaceId)
        {
            var targetWs = _workspaces.FirstOrDefault(w => w.WorkspaceId == targetWorkspaceId);
            if (targetWs is not null)
            {
                sourceWs.Windows.Remove(windowVm);
                targetWs.Windows.Add(windowVm);
                _workspaceManager.AssignWindow(hwnd, targetWorkspaceId);
            }
        }
    }
}
