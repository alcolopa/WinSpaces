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

    private readonly IProcessManager _processManager;
    private readonly List<WindowProfileState> _pendingRestorations = new();
    private readonly object _restorationsLock = new();

    public WorkspaceManager(IWindowManager windowManager, WindowTracker tracker, OperationGuard guard, IProcessManager? processManager = null)
    {
        _windowManager = windowManager;
        _tracker = tracker;
        _guard = guard;
        _processManager = processManager ?? new NullProcessManager();
    }

    public bool TryConsumeRestoration(string processPath, string windowClass, string title, out WindowProfileState? restoration)
    {
        lock (_restorationsLock)
        {
            var match = _pendingRestorations.FirstOrDefault(r => 
                string.Equals(r.ProcessPath, processPath, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(r.WindowClass) || string.Equals(r.WindowClass, windowClass, StringComparison.Ordinal)) &&
                (string.IsNullOrEmpty(r.Title) || title.Contains(r.Title, StringComparison.OrdinalIgnoreCase)));

            if (match is not null)
            {
                _pendingRestorations.Remove(match);
                restoration = match;
                return true;
            }

            match = _pendingRestorations.FirstOrDefault(r => 
                string.Equals(r.ProcessPath, processPath, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                _pendingRestorations.Remove(match);
                restoration = match;
                return true;
            }

            restoration = null;
            return false;
        }
    }

    private sealed class NullProcessManager : IProcessManager
    {
        public void Launch(string processPath, string? arguments) {}
        public string? GetCommandLine(int processId) => null;
    }

    public string? GetActiveWorkspace(string monitorId) =>
        _activeWorkspaceByMonitor.GetValueOrDefault(monitorId);

    public IReadOnlyDictionary<string, string> GetActiveWorkspaces() =>
        _activeWorkspaceByMonitor.ToDictionary(kv => kv.Key, kv => kv.Value);

    private readonly ConcurrentDictionary<string, string> _workspaceNames = new();
    private readonly ConcurrentDictionary<string, WorkspaceDefinition> _workspaceDefinitions = new();

    public void SetWorkspaceDefinition(WorkspaceDefinition definition) => _workspaceDefinitions[definition.Id] = definition;

    public void RenameWorkspace(string workspaceId, string name)
    {
        _workspaceNames[workspaceId] = name;
        if (_workspaceDefinitions.TryGetValue(workspaceId, out var existing))
        {
            _workspaceDefinitions[workspaceId] = existing with { Name = name };
        }
        else
        {
            _workspaceDefinitions[workspaceId] = new WorkspaceDefinition(workspaceId, name, 0);
        }
    }

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

    /// <summary>
    /// Applies a workspace profile, switching the active workspace of each monitor to the workspace defined in the profile.
    /// </summary>
    public void ApplyProfile(WorkspaceProfile profile)
    {
        ExecuteCommandAsync(profile.EnterCommand);

        foreach (var kv in profile.ActiveWorkspaceByMonitor)
        {
            SwitchWorkspace(kv.Key, kv.Value);
        }

        if (profile.Windows is null || profile.Windows.Count == 0) return;

        lock (_restorationsLock)
        {
            _pendingRestorations.Clear();
        }

        var tracked = _tracker.TrackedWindows.Values.ToList();

        foreach (var savedWin in profile.Windows)
        {
            var running = tracked.FirstOrDefault(w => 
                string.Equals(w.ProcessPath, savedWin.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(savedWin.WindowClass) || string.Equals(w.WindowClass, savedWin.WindowClass, StringComparison.Ordinal)));

            if (running is not null)
            {
                tracked.Remove(running);
                AssignWindow(running.Hwnd, savedWin.WorkspaceId);
                
                using (_guard.Suppress(running.Hwnd))
                {
                    _windowManager.Move(running.Hwnd, savedWin.NormalBounds);
                }
            }
            else
            {
                lock (_restorationsLock)
                {
                    _pendingRestorations.Add(savedWin);
                }
                _processManager.Launch(savedWin.ProcessPath, savedWin.CommandLineArguments);
            }
        }
    }

    private void ApplyWorkspaceSwitch(string monitorId, string targetWorkspaceId)
    {
        if (_activeWorkspaceByMonitor.TryGetValue(monitorId, out var current) && current == targetWorkspaceId)
        {
            return;
        }

        if (current is not null && _workspaceDefinitions.TryGetValue(current, out var currentDef))
        {
            ExecuteCommandAsync(currentDef.ExitCommand);
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

        if (_workspaceDefinitions.TryGetValue(targetWorkspaceId, out var targetDef))
        {
            ExecuteCommandAsync(targetDef.EnterCommand);
        }

        _activeWorkspaceByMonitor[monitorId] = targetWorkspaceId;
    }

    private static void ExecuteCommandAsync(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {command}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                process.Start();
            }
            catch
            {
                // Fail-safe: ignore automation command errors
            }
        });
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
