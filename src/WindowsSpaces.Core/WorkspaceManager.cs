using System.Collections.Concurrent;

namespace WindowsSpaces.Core;

/// <summary>
/// Owns per-monitor active-workspace state and the hide/show/move switching
/// algorithm. Each monitor has its own transition worker; at most one
/// transition executes per monitor at a time. A call that arrives while a
/// transition for that monitor is already running does not queue a second
/// execution — it overwrites the pending target, so a burst of calls
/// (1 -> 2 -> 3 -> 2) collapses to a single execution of the last target
/// once the in-flight one finishes ("latest request wins").
/// </summary>
public sealed class WorkspaceManager
{
    private sealed class MonitorTransitionState
    {
        public readonly object Lock = new();
        public string? PendingTarget;
        public bool WorkerRunning;
    }

    private readonly IWindowManager _windowManager;
    private readonly WindowTracker _tracker;
    private readonly OperationGuard _guard;
    private readonly ConcurrentDictionary<string, string> _activeWorkspaceByMonitor = new();
    private readonly ConcurrentDictionary<string, MonitorTransitionState> _transitions = new();

    public WorkspaceManager(IWindowManager windowManager, WindowTracker tracker, OperationGuard guard)
    {
        _windowManager = windowManager;
        _tracker = tracker;
        _guard = guard;
    }

    public string? GetActiveWorkspace(string monitorId) =>
        _activeWorkspaceByMonitor.GetValueOrDefault(monitorId);

    private readonly ConcurrentDictionary<string, string> _workspaceNames = new();

    public void RenameWorkspace(string workspaceId, string name) => _workspaceNames[workspaceId] = name;

    /// <summary>Names for all known workspaces on a monitor, keyed by workspace id. Only includes workspaces that have been named via RenameWorkspace.</summary>
    public IReadOnlyDictionary<string, string> GetWorkspaceNames(string monitorId) =>
        _workspaceNames
            .Where(kv => kv.Key.StartsWith(monitorId + ":", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>
    /// Requests a switch of the given monitor to the target workspace. If a
    /// transition for this monitor is already executing, this call only
    /// updates the pending target and returns immediately — the in-flight
    /// transition's worker picks up the latest pending target once it
    /// finishes, draining until no new target has arrived.
    /// </summary>
    public void SwitchWorkspace(string monitorId, string targetWorkspaceId)
    {
        var state = _transitions.GetOrAdd(monitorId, _ => new MonitorTransitionState());

        lock (state.Lock)
        {
            state.PendingTarget = targetWorkspaceId;
            if (state.WorkerRunning)
            {
                return;
            }
            state.WorkerRunning = true;
        }

        while (true)
        {
            string target;
            lock (state.Lock)
            {
                if (state.PendingTarget is null)
                {
                    state.WorkerRunning = false;
                    return;
                }
                target = state.PendingTarget;
                state.PendingTarget = null;
            }

            ApplyWorkspaceSwitch(monitorId, target);
        }
    }

    private void ApplyWorkspaceSwitch(string monitorId, string targetWorkspaceId)
    {
        if (_activeWorkspaceByMonitor.TryGetValue(monitorId, out var current) && current == targetWorkspaceId)
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

    /// <summary>
    /// Reassigns a window to a different workspace. If the window's monitor
    /// is currently showing that workspace, the window becomes visible;
    /// otherwise it is hidden until that workspace becomes active.
    /// </summary>
    public void AssignWindow(nint hwnd, string targetWorkspaceId)
    {
        if (!_tracker.TrackedWindows.TryGetValue(hwnd, out var state)) return;

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
