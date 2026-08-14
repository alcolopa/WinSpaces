using System.Collections.Concurrent;

namespace WindowsSpaces.Core;

/// <summary>
/// Maintains the live WindowState collection from a stream of WindowEvents.
/// Event handling is synchronous and cheap by design — callers on Win32
/// WinEvent hooks must enqueue events elsewhere and dispatch them here
/// off the hook callback thread.
///
/// New windows are assigned to their current monitor's first workspace on
/// discovery. Once assigned, MonitorId/WorkspaceId survive later refresh
/// events — only IsVisible/IsMinimized/IsMaximized/NormalBounds are
/// refreshed from Win32 — unless the window has genuinely moved to a
/// different monitor, in which case it is reassigned to that monitor's
/// first workspace. Events for a window currently suppressed by the
/// OperationGuard (i.e. this process's own Hide/Show/Move calls) only
/// refresh the volatile fields and never touch monitor/workspace
/// assignment, so our own operations can't be misread as the user moving
/// a window to a new monitor.
/// </summary>
public sealed class WindowTracker
{
    private readonly IWindowManager _windowManager;
    private readonly IMonitorManager _monitorManager;
    private readonly OperationGuard _guard;
    private readonly ConcurrentDictionary<nint, WindowState> _tracked = new();

    public WindowTracker(IWindowManager windowManager, IWindowEventSource eventSource, IMonitorManager monitorManager, OperationGuard guard)
    {
        _windowManager = windowManager;
        _monitorManager = monitorManager;
        _guard = guard;
        eventSource.WindowEvent += (_, evt) => HandleEvent(evt);
    }

    public IReadOnlyDictionary<nint, WindowState> TrackedWindows => _tracked;

    public void Rescan()
    {
        _tracked.Clear();
        foreach (var hwnd in _windowManager.EnumerateTopLevelWindows())
        {
            TrackAsNewWindow(hwnd);
        }
    }

    public void HandleEvent(WindowEvent evt)
    {
        if (evt.Kind == WindowEventKind.Destroyed)
        {
            _tracked.TryRemove(evt.Hwnd, out _);
            return;
        }

        if (_guard.IsSuppressed(evt.Hwnd))
        {
            RefreshVolatileFieldsOnly(evt.Hwnd);
            return;
        }

        if (_tracked.ContainsKey(evt.Hwnd))
        {
            RefreshPreservingAssignment(evt.Hwnd);
        }
        else
        {
            TrackAsNewWindow(evt.Hwnd);
        }
    }

    private void TrackAsNewWindow(nint hwnd)
    {
        var state = _windowManager.GetWindowState(hwnd);
        if (state is null) return;

        var monitor = _monitorManager.GetMonitorForWindow(hwnd);
        if (monitor is not null)
        {
            state.MonitorId = monitor.Id;
            state.WorkspaceId = $"{monitor.Id}:1";
        }

        _tracked[hwnd] = state;
    }

    private void RefreshVolatileFieldsOnly(nint hwnd)
    {
        if (!_tracked.TryGetValue(hwnd, out var existing)) return;

        var fresh = _windowManager.GetWindowState(hwnd);
        if (fresh is null) return;

        existing.IsVisible = fresh.IsVisible;
        existing.IsMinimized = fresh.IsMinimized;
        existing.IsMaximized = fresh.IsMaximized;
        existing.NormalBounds = fresh.NormalBounds;
        existing.LastUpdated = fresh.LastUpdated;
    }

    private void RefreshPreservingAssignment(nint hwnd)
    {
        if (!_tracked.TryGetValue(hwnd, out var existing)) return;

        var fresh = _windowManager.GetWindowState(hwnd);
        if (fresh is null) return;

        var currentMonitor = _monitorManager.GetMonitorForWindow(hwnd);
        if (currentMonitor is not null && currentMonitor.Id != existing.MonitorId)
        {
            // The window moved to a different monitor since we last saw it;
            // it starts on that monitor's first workspace.
            fresh.MonitorId = currentMonitor.Id;
            fresh.WorkspaceId = $"{currentMonitor.Id}:1";
        }
        else
        {
            fresh.MonitorId = existing.MonitorId;
            fresh.WorkspaceId = existing.WorkspaceId;
        }

        _tracked[hwnd] = fresh;
    }
}
