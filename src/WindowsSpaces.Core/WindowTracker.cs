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
    public delegate bool TryConsumeRestorationDelegate(string processPath, string windowClass, string title, out WindowProfileState? restoration);
    private readonly Func<IReadOnlyList<ApplicationRule>> _getRules;
    private readonly Func<string, string?> _getActiveWorkspace;
    private readonly TryConsumeRestorationDelegate _tryConsumeRestoration;
    private readonly ConcurrentDictionary<nint, WindowState> _tracked = new();

    public WindowTracker(
        IWindowManager windowManager,
        IWindowEventSource eventSource,
        IMonitorManager monitorManager,
        OperationGuard guard,
        Func<IReadOnlyList<ApplicationRule>>? getRules = null,
        Func<string, string?>? getActiveWorkspace = null,
        TryConsumeRestorationDelegate? tryConsumeRestoration = null)
    {
        _windowManager = windowManager;
        _monitorManager = monitorManager;
        _guard = guard;
        _getRules = getRules ?? (() => Array.Empty<ApplicationRule>());
        _getActiveWorkspace = getActiveWorkspace ?? (_ => null);
        _tryConsumeRestoration = tryConsumeRestoration ?? ((string p, string c, string t, out WindowProfileState? r) => { r = null; return false; });
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

        if (_tryConsumeRestoration(state.ProcessPath ?? string.Empty, state.WindowClass ?? string.Empty, state.Title ?? string.Empty, out var restoration) &&
            restoration is not null)
        {
            state.MonitorId = restoration.MonitorId;
            state.WorkspaceId = restoration.WorkspaceId;
            state.NormalBounds = restoration.NormalBounds;
            state.IsMaximized = restoration.IsMaximized;
            state.IsMinimized = restoration.IsMinimized;

            _tracked[hwnd] = state;

            using (_guard.Suppress(hwnd))
            {
                _windowManager.Move(hwnd, restoration.NormalBounds);
                var activeWorkspace = _getActiveWorkspace(restoration.MonitorId);
                if (activeWorkspace == restoration.WorkspaceId)
                {
                    _windowManager.Show(hwnd);
                }
                else
                {
                    _windowManager.Hide(hwnd);
                }
            }
            return;
        }

        var monitor = _monitorManager.GetMonitorForWindow(hwnd);
        if (monitor is not null)
        {
            var activeWorkspace = _getActiveWorkspace(monitor.Id);
            state.MonitorId = monitor.Id;
            state.WorkspaceId = activeWorkspace ?? $"{monitor.Id}:1";

            // Apply rules
            var rules = _getRules();
            foreach (var rule in rules)
            {
                if (rule.Matches(state.ProcessPath, state.WindowClass, state.Title))
                {
                    string targetMonitorId = rule.TargetMonitorId;
                    if (string.Equals(targetMonitorId, "Active", StringComparison.OrdinalIgnoreCase))
                    {
                        targetMonitorId = monitor.Id;
                    }

                    state.MonitorId = targetMonitorId;
                    state.WorkspaceId = $"{targetMonitorId}:{rule.TargetWorkspaceIndex}";

                    // If the target workspace is not active, hide it immediately
                    var targetActiveWorkspace = _getActiveWorkspace(targetMonitorId);
                    if (targetActiveWorkspace is not null && targetActiveWorkspace != state.WorkspaceId)
                    {
                        using (_guard.Suppress(hwnd))
                        {
                            _windowManager.Hide(hwnd);
                        }
                    }
                    break; // use first matching rule
                }
            }
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
