using System.Collections.Concurrent;
using System.Drawing;

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
    private sealed record PendingSwitch(string Target, TransitionDirection? DirectionHint);

    private sealed class MonitorTransitionState
    {
        public readonly object Lock = new();
        public PendingSwitch? Pending;
        public bool WorkerRunning;
    }

    private readonly IWindowManager _windowManager;
    private readonly WindowTracker _tracker;
    private readonly OperationGuard _guard;
    private readonly ConcurrentDictionary<string, string> _activeWorkspaceByMonitor = new();
    private readonly ConcurrentDictionary<string, MonitorTransitionState> _transitions = new();

    private readonly IProcessManager _processManager;
    private readonly IWorkspaceTransitionAnimator? _animator;
    private readonly List<WindowProfileState> _pendingRestorations = new();
    private readonly object _restorationsLock = new();

    public WorkspaceManager(
        IWindowManager windowManager,
        WindowTracker tracker,
        OperationGuard guard,
        IProcessManager? processManager = null,
        IWorkspaceTransitionAnimator? animator = null)
    {
        _windowManager = windowManager;
        _tracker = tracker;
        _guard = guard;
        _processManager = processManager ?? new NullProcessManager();
        _animator = animator;
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

    // ---- Live per-monitor workspace list -------------------------------
    //
    // Spaces can be created and deleted while the app runs, so the ordered
    // list of a monitor's spaces is runtime state here rather than something
    // only the persisted config knows. "Next/previous space", "switch to the
    // Nth space", and orphan recovery after a delete all read from this list.

    private readonly ConcurrentDictionary<string, IReadOnlyList<WorkspaceDefinition>> _workspacesByMonitor = new();
    private readonly object _workspaceListLock = new();

    /// <summary>
    /// The monitor's spaces in display order, or an empty list if the monitor
    /// has never been configured.
    /// </summary>
    public IReadOnlyList<WorkspaceDefinition> GetWorkspaces(string monitorId) =>
        _workspacesByMonitor.GetValueOrDefault(monitorId) ?? Array.Empty<WorkspaceDefinition>();

    /// <summary>
    /// Replaces a monitor's ordered space list. Safe to call at any time with
    /// spaces added, removed, renamed or reordered relative to the previous
    /// call — this is the single live-apply path for dynamic space creation
    /// and deletion.
    ///
    /// No window is ever lost by a delete: every window sitting in a space
    /// that no longer exists is relocated to the nearest surviving neighbour
    /// of its old position (preferring the space to its left), and the active
    /// space falls back the same way if the monitor was showing the deleted
    /// space. A list that would leave a monitor with zero spaces is ignored.
    /// </summary>
    public void SetMonitorWorkspaces(string monitorId, IReadOnlyList<WorkspaceDefinition> workspaces)
    {
        if (workspaces is null || workspaces.Count == 0) return;

        string? fallbackForActive = null;
        List<(nint Hwnd, string TargetWorkspaceId)> relocations;

        lock (_workspaceListLock)
        {
            var previous = GetWorkspaces(monitorId);
            var ordered = workspaces.ToList();
            _workspacesByMonitor[monitorId] = ordered;

            foreach (var definition in ordered)
            {
                _workspaceDefinitions[definition.Id] = definition;
                _workspaceNames[definition.Id] = definition.Name;
            }

            var surviving = ordered.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);

            // Drop definitions/names for spaces this monitor no longer has, so
            // a deleted space can't come back as a stale name in the overview
            // or as a live switch target.
            foreach (var stale in previous.Where(p => !surviving.Contains(p.Id)))
            {
                _workspaceDefinitions.TryRemove(stale.Id, out _);
                _workspaceNames.TryRemove(stale.Id, out _);
            }

            relocations = _tracker.TrackedWindows.Values
                .Where(w => w.MonitorId == monitorId)
                .Where(w => w.WorkspaceId is null || !surviving.Contains(w.WorkspaceId))
                .Select(w => (w.Hwnd, ResolveSurvivor(w.WorkspaceId, previous, ordered)))
                .ToList();

            if (_activeWorkspaceByMonitor.TryGetValue(monitorId, out var active) && !surviving.Contains(active))
            {
                fallbackForActive = ResolveSurvivor(active, previous, ordered);
            }
        }

        foreach (var (hwnd, target) in relocations)
        {
            AssignWindow(hwnd, target);
        }

        if (fallbackForActive is not null)
        {
            SwitchWorkspace(monitorId, fallbackForActive);
        }
    }

    /// <summary>
    /// Picks the space a window (or the active selection) should land on when
    /// the space it referred to has been deleted: the nearest surviving space
    /// to the left of its old position, else the nearest to the right, else
    /// the first remaining space.
    /// </summary>
    private static string ResolveSurvivor(
        string? oldWorkspaceId,
        IReadOnlyList<WorkspaceDefinition> previous,
        IReadOnlyList<WorkspaceDefinition> current)
    {
        var oldPosition = oldWorkspaceId is null
            ? -1
            : IndexOfWorkspace(previous, oldWorkspaceId);

        if (oldPosition >= 0)
        {
            var currentIds = current.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);

            for (var i = oldPosition - 1; i >= 0; i--)
            {
                if (currentIds.Contains(previous[i].Id)) return previous[i].Id;
            }

            for (var i = oldPosition + 1; i < previous.Count; i++)
            {
                if (currentIds.Contains(previous[i].Id)) return previous[i].Id;
            }
        }

        return current[0].Id;
    }

    private static int IndexOfWorkspace(IReadOnlyList<WorkspaceDefinition> list, string workspaceId)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Id, workspaceId, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>
    /// The id of the monitor's <paramref name="position"/>th space (1-based
    /// position in the list, not the space's stored Index). Null when the
    /// monitor has no space at that position.
    ///
    /// Position, not Index, is what a "switch to space 3" hotkey means: once
    /// spaces can be deleted, indexes are sparse (deleting space 2 of 3 leaves
    /// indexes 1 and 3) and index-based lookup would target a space that no
    /// longer exists.
    /// </summary>
    public string? GetWorkspaceIdAt(string monitorId, int position)
    {
        var workspaces = GetWorkspaces(monitorId);
        if (workspaces.Count == 0)
        {
            // No live list for this monitor (unit tests, or a monitor that has
            // not been configured yet): fall back to the id convention.
            return position >= 1 ? $"{monitorId}:{position}" : null;
        }

        if (position < 1 || position > workspaces.Count) return null;
        return workspaces[position - 1].Id;
    }

    /// <summary>
    /// The id of the space <paramref name="delta"/> positions away from the
    /// monitor's active space, wrapping around both ends. Null when the
    /// monitor has fewer than two spaces.
    /// </summary>
    public string? GetRelativeWorkspaceId(string monitorId, int delta)
    {
        var workspaces = GetWorkspaces(monitorId);
        if (workspaces.Count < 2) return null;

        var active = GetActiveWorkspace(monitorId);
        var currentPosition = active is null ? 0 : IndexOfWorkspace(workspaces, active);
        if (currentPosition < 0) currentPosition = 0;

        var count = workspaces.Count;
        var next = ((currentPosition + delta) % count + count) % count;
        return workspaces[next].Id;
    }

    /// <summary>
    /// Switches the monitor to the space <paramref name="delta"/> positions
    /// away from its active one, wrapping around. No-op on a monitor with
    /// fewer than two spaces.
    /// </summary>
    public void SwitchRelative(string monitorId, int delta)
    {
        var target = GetRelativeWorkspaceId(monitorId, delta);
        if (target is null) return;

        // The caller knows which way the user asked to go, which positions
        // alone cannot tell us at the wrap-around: last -> first is "next"
        // even though the target sits to the left of the current space.
        SwitchWorkspace(monitorId, target, delta >= 0 ? TransitionDirection.Next : TransitionDirection.Previous);
    }

    /// <summary>
    /// Whether the monitor can currently show this space. A monitor with no
    /// configured list (unit tests, a monitor seen before its config loads)
    /// accepts anything, so this only ever rejects genuinely stale ids.
    /// </summary>
    private bool IsKnownWorkspace(string monitorId, string workspaceId)
    {
        var workspaces = GetWorkspaces(monitorId);
        if (workspaces.Count == 0) return true;
        return IndexOfWorkspace(workspaces, workspaceId) >= 0;
    }

    /// <summary>
    /// Requests a switch of the given monitor to the target workspace. If a
    /// transition for this monitor is already executing, this call only
    /// updates the pending target and returns immediately — the in-flight
    /// transition's worker picks up the latest pending target once it
    /// finishes, draining until no new target has arrived.
    /// </summary>
    public void SwitchWorkspace(string monitorId, string targetWorkspaceId)
        => SwitchWorkspace(monitorId, targetWorkspaceId, directionHint: null);

    /// <inheritdoc cref="SwitchWorkspace(string, string)"/>
    /// <param name="directionHint">
    /// Which way the switch should appear to move when the caller already
    /// knows (next/previous navigation). Null derives the direction from the
    /// two spaces' positions instead.
    /// </param>
    public void SwitchWorkspace(string monitorId, string targetWorkspaceId, TransitionDirection? directionHint)
    {
        // Once spaces can be deleted, a stale target (an old hotkey, a
        // profile, a CLI call naming a space that no longer exists) would
        // otherwise hide every window on the monitor and show none of them
        // back — the monitor would go blank with no way to recover but
        // Show All Windows. Refuse the switch instead.
        if (!IsKnownWorkspace(monitorId, targetWorkspaceId)) return;

        var state = _transitions.GetOrAdd(monitorId, _ => new MonitorTransitionState());

        lock (state.Lock)
        {
            state.Pending = new PendingSwitch(targetWorkspaceId, directionHint);
            if (state.WorkerRunning)
            {
                return;
            }
            state.WorkerRunning = true;
        }

        while (true)
        {
            PendingSwitch pending;
            lock (state.Lock)
            {
                if (state.Pending is null)
                {
                    state.WorkerRunning = false;
                    return;
                }
                pending = state.Pending;
                state.Pending = null;
            }

            ApplyWorkspaceSwitch(monitorId, pending.Target, pending.DirectionHint);
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

    private void ApplyWorkspaceSwitch(string monitorId, string targetWorkspaceId, TransitionDirection? directionHint = null)
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

        var outgoing = windowsOnMonitor.Where(w => w.WorkspaceId != targetWorkspaceId).ToList();
        var incoming = windowsOnMonitor.Where(w => w.WorkspaceId == targetWorkspaceId).ToList();

        var shown = 0;
        void ShowIncoming()
        {
            // Guarded against a second call: the animator's failure paths are
            // allowed to be belt-and-braces, but showing a window twice would
            // re-raise it over whatever the user has since focused.
            if (Interlocked.Exchange(ref shown, 1) == 1) return;

            foreach (var window in incoming)
            {
                using (_guard.Suppress(window.Hwnd))
                {
                    _windowManager.Move(window.Hwnd, window.NormalBounds);
                    _windowManager.Show(window.Hwnd);
                }
            }
        }

        var hidden = 0;
        void HideOutgoing()
        {
            if (Interlocked.Exchange(ref hidden, 1) == 1) return;

            foreach (var window in outgoing)
            {
                using (_guard.Suppress(window.Hwnd))
                {
                    _windowManager.Hide(window.Hwnd);
                }
            }
        }

        var animator = _animator;
        if (animator is null)
        {
            ShowIncoming();
            HideOutgoing();
        }
        else
        {
            var transition = new WorkspaceTransition(
                monitorId,
                current,
                targetWorkspaceId,
                directionHint ?? DeriveDirection(monitorId, current, targetWorkspaceId),
                outgoing.Select(w => w.Hwnd).ToList(),
                incoming.Select(w => w.Hwnd).ToList(),
                ShowIncoming,
                HideOutgoing);

            try
            {
                animator.Animate(transition);
            }
            catch
            {
                // An animation is cosmetic; a half-applied switch is not. If
                // the animator throws, apply the switch here — both callbacks
                // are idempotent, so doing it twice costs nothing and leaving
                // it undone would strand the monitor mid-switch.
                ShowIncoming();
                HideOutgoing();
            }
        }

        if (_workspaceDefinitions.TryGetValue(targetWorkspaceId, out var targetDef))
        {
            ExecuteCommandAsync(targetDef.EnterCommand);
        }

        _activeWorkspaceByMonitor[monitorId] = targetWorkspaceId;
    }

    /// <summary>
    /// Which way a switch appears to move, from the two spaces' positions in
    /// the monitor's list. <see cref="TransitionDirection.None"/> when there
    /// is nothing to move away from (first switch after startup) or the
    /// positions cannot be resolved.
    /// </summary>
    private TransitionDirection DeriveDirection(string monitorId, string? fromWorkspaceId, string toWorkspaceId)
    {
        if (fromWorkspaceId is null) return TransitionDirection.None;

        var workspaces = GetWorkspaces(monitorId);
        var from = IndexOfWorkspace(workspaces, fromWorkspaceId);
        var to = IndexOfWorkspace(workspaces, toWorkspaceId);
        if (from < 0 || to < 0 || from == to) return TransitionDirection.None;

        return to > from ? TransitionDirection.Next : TransitionDirection.Previous;
    }

    /// <summary>
    /// Collapses a monitor to a single space holding every window currently
    /// on it. This is the startup reconciliation: the app has just started,
    /// so what is on screen is the truth and any space list left over from a
    /// previous session is not. Nothing is hidden and nothing is moved — the
    /// screen looks exactly as it did a moment before.
    /// </summary>
    public void ResetToSingleWorkspace(string monitorId, WorkspaceDefinition workspace)
    {
        lock (_workspaceListLock)
        {
            var previous = GetWorkspaces(monitorId);
            _workspacesByMonitor[monitorId] = new[] { workspace };

            foreach (var stale in previous.Where(p => p.Id != workspace.Id))
            {
                _workspaceDefinitions.TryRemove(stale.Id, out _);
                _workspaceNames.TryRemove(stale.Id, out _);
            }

            _workspaceDefinitions[workspace.Id] = workspace;
            _workspaceNames[workspace.Id] = workspace.Name;
        }

        foreach (var window in _tracker.TrackedWindows.Values.Where(w => w.MonitorId == monitorId))
        {
            window.WorkspaceId = workspace.Id;
        }

        // Set active directly rather than through SwitchWorkspace: there is
        // no previous space to transition away from, and going through the
        // switch path would hide-and-show every window on the monitor (and
        // animate it) for a state it is already in.
        _activeWorkspaceByMonitor[monitorId] = workspace.Id;
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

        // Assigning to a space that does not exist would hide the window with
        // no space able to bring it back — a permanently lost window, which
        // the reliability rules rank as the worst outcome. Leave it where it is.
        if (monitorId is not null && !IsKnownWorkspace(monitorId, targetWorkspaceId)) return;

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
    /// Moves a window to a space on a <em>different</em> monitor, keeping its
    /// relative position and size on the new screen.
    ///
    /// Both the assignment and the physical move happen here: the window has
    /// to actually sit on the target monitor, or the tracker's next rescan
    /// would see it on its old monitor and reset it to that monitor's first
    /// space (see <see cref="WindowTracker"/>). The move is applied to
    /// <see cref="WindowState.NormalBounds"/> too, since that is what a later
    /// workspace switch restores the window to.
    ///
    /// Monitor rectangles are passed in rather than resolved here — Core has
    /// no access to Win32, and the caller already holds the monitor list.
    /// </summary>
    /// <returns>False when the window is unknown, or the target space does not exist on the target monitor.</returns>
    public bool MoveWindowToMonitor(
        nint hwnd,
        string targetMonitorId,
        string targetWorkspaceId,
        Rectangle sourceMonitorBounds,
        Rectangle targetMonitorBounds)
    {
        if (!_tracker.TrackedWindows.TryGetValue(hwnd, out var state)) return false;

        // Same guard as AssignWindow: hiding a window into a space that does
        // not exist would leave nothing able to bring it back.
        if (!IsKnownWorkspace(targetMonitorId, targetWorkspaceId)) return false;

        var bounds = MonitorGeometry.MapBetweenMonitors(state.NormalBounds, sourceMonitorBounds, targetMonitorBounds);

        state.MonitorId = targetMonitorId;
        state.WorkspaceId = targetWorkspaceId;
        state.NormalBounds = bounds;

        var isTargetActive = GetActiveWorkspace(targetMonitorId) == targetWorkspaceId;

        using (_guard.Suppress(hwnd))
        {
            _windowManager.Move(hwnd, bounds);

            if (isTargetActive)
            {
                _windowManager.Show(hwnd);
            }
            else
            {
                _windowManager.Hide(hwnd);
            }
        }

        return true;
    }

    /// <summary>
    /// Moves every window sitting in one monitor's space to a space on
    /// another monitor. This is the window half of "drag a space from monitor
    /// 1 to monitor 2" — the space list itself is owned by the config, so the
    /// caller creates the destination space first and deletes the source one
    /// afterwards, once this has emptied it.
    /// </summary>
    /// <returns>How many windows were moved.</returns>
    public int MoveWorkspaceWindowsToMonitor(
        string sourceMonitorId,
        string sourceWorkspaceId,
        string targetMonitorId,
        string targetWorkspaceId,
        Rectangle sourceMonitorBounds,
        Rectangle targetMonitorBounds)
    {
        var windows = _tracker.TrackedWindows.Values
            .Where(w => w.MonitorId == sourceMonitorId && w.WorkspaceId == sourceWorkspaceId)
            .Select(w => w.Hwnd)
            .ToList();

        var moved = 0;
        foreach (var hwnd in windows)
        {
            if (MoveWindowToMonitor(hwnd, targetMonitorId, targetWorkspaceId, sourceMonitorBounds, targetMonitorBounds))
            {
                moved++;
            }
        }

        return moved;
    }

    /// <summary>
    /// Switches to the window's space if its monitor is not already showing
    /// it, then brings the window to the front. This is what activating a
    /// window from the overview does — UI never touches Win32 itself.
    /// </summary>
    public void ActivateWindow(nint hwnd)
    {
        if (!_tracker.TrackedWindows.TryGetValue(hwnd, out var state)) return;

        if (state.MonitorId is not null && state.WorkspaceId is not null &&
            GetActiveWorkspace(state.MonitorId) != state.WorkspaceId)
        {
            SwitchWorkspace(state.MonitorId, state.WorkspaceId);
        }

        _windowManager.SetForeground(hwnd);
    }

    /// <summary>
    /// Asks a tracked window to close. A hidden window (one sitting in an
    /// inactive space) is shown first: an app that puts up a "save changes?"
    /// prompt would otherwise block on a dialog the user cannot see.
    /// </summary>
    public void CloseWindow(nint hwnd)
    {
        if (!_tracker.TrackedWindows.TryGetValue(hwnd, out var state)) return;

        if (!state.IsVisible)
        {
            using (_guard.Suppress(hwnd))
            {
                _windowManager.Show(hwnd);
            }
        }

        _windowManager.Close(hwnd);
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
