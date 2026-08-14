using System.Collections.Concurrent;

namespace WindowsSpaces.Core;

/// <summary>
/// Owns per-monitor active-workspace state and the hide/show/move switching
/// algorithm. Each monitor has its own transition lock; a rapid sequence of
/// switch requests for the same monitor collapses to the latest target
/// ("latest request wins") rather than executing every intermediate step.
/// </summary>
public sealed class WorkspaceManager
{
    private readonly IWindowManager _windowManager;
    private readonly WindowTracker _tracker;
    private readonly OperationGuard _guard = new();
    private readonly ConcurrentDictionary<string, string> _activeWorkspaceByMonitor = new();
    private readonly ConcurrentDictionary<string, object> _monitorLocks = new();

    public WorkspaceManager(IWindowManager windowManager, WindowTracker tracker)
    {
        _windowManager = windowManager;
        _tracker = tracker;
    }

    public string? GetActiveWorkspace(string monitorId) =>
        _activeWorkspaceByMonitor.GetValueOrDefault(monitorId);

    /// <summary>
    /// Switches the given monitor to the target workspace. Safe to call
    /// rapidly and repeatedly for the same monitor: only the latest call's
    /// target is honored once the lock for that monitor is acquired.
    /// </summary>
    public void SwitchWorkspace(string monitorId, string targetWorkspaceId)
    {
        var monitorLock = _monitorLocks.GetOrAdd(monitorId, _ => new object());

        lock (monitorLock)
        {
            // Re-check: if a queued caller already moved us to this target
            // (or past it) under the lock, there is nothing left to do.
            if (_activeWorkspaceByMonitor.TryGetValue(monitorId, out var currentBeforeWait) &&
                currentBeforeWait == targetWorkspaceId)
            {
                return;
            }

            var windowsOnMonitor = _tracker.TrackedWindows.Values
                .Where(w => w.MonitorId == monitorId)
                .ToList();

            // Hide everything on this monitor not in the target workspace.
            foreach (var window in windowsOnMonitor.Where(w => w.WorkspaceId != targetWorkspaceId))
            {
                using (_guard.Suppress(window.Hwnd))
                {
                    _windowManager.Hide(window.Hwnd);
                }
            }

            // Show everything in the target workspace on this monitor.
            foreach (var window in windowsOnMonitor.Where(w => w.WorkspaceId == targetWorkspaceId))
            {
                using (_guard.Suppress(window.Hwnd))
                {
                    _windowManager.Move(window.Hwnd, window.NormalBounds);
                    _windowManager.Show(window.Hwnd);
                }
            }

            _activeWorkspaceByMonitor[monitorId] = targetWorkspaceId;
        }
    }

    /// <summary>
    /// Reassigns a window to a different workspace. If the window's monitor
    /// is currently showing that workspace, the window becomes visible;
    /// otherwise it is hidden until that workspace becomes active.
    /// </summary>
    public void AssignWindow(nint hwnd, string targetWorkspaceId)
    {
        var state = _windowManager.GetWindowState(hwnd);
        if (state is null) return;

        var monitorId = state.MonitorId;
        state.WorkspaceId = targetWorkspaceId;

        if (monitorId is null) return;

        var isTargetActive = GetActiveWorkspace(monitorId) == targetWorkspaceId;
        using (_guard.Suppress(hwnd))
        {
            if (isTargetActive)
            {
                _windowManager.Show(hwnd);
            }
            else
            {
                _windowManager.Hide(hwnd);
            }
        }
    }

    /// <summary>
    /// Emergency recovery: shows every tracked window regardless of
    /// workspace assignment, without altering assignments.
    /// </summary>
    public void ShowAllWindows()
    {
        foreach (var window in _tracker.TrackedWindows.Values)
        {
            using (_guard.Suppress(window.Hwnd))
            {
                _windowManager.Show(window.Hwnd);
            }
        }
    }
}
