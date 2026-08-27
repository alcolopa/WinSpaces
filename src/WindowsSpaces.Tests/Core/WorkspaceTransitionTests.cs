using System.Drawing;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.Core;

/// <summary>
/// Covers the startup reconciliation (<see cref="WorkspaceManager.ResetToSingleWorkspace"/>)
/// and the animator hand-off: which way a switch is reported to move, and the
/// guarantee that the switch itself lands however the animator behaves.
/// </summary>
public class WorkspaceTransitionTests
{
    private const string MonitorId = "MON-1";

    private sealed class RecordingAnimator : IWorkspaceTransitionAnimator
    {
        private readonly Action<WorkspaceTransition>? _behaviour;

        public RecordingAnimator(Action<WorkspaceTransition>? behaviour = null) => _behaviour = behaviour;

        public List<WorkspaceTransition> Transitions { get; } = new();

        public void Animate(WorkspaceTransition transition)
        {
            Transitions.Add(transition);

            if (_behaviour is null)
            {
                transition.ShowIncoming();
                transition.HideOutgoing();
                return;
            }

            _behaviour(transition);
        }
    }

    private sealed class Harness
    {
        public required FakeWindowManager Windows { get; init; }
        public required FakeMonitorManager Monitors { get; init; }
        public required WindowTracker Tracker { get; init; }
        public required WorkspaceManager Manager { get; init; }

        private readonly Dictionary<nint, string> _intended = new();

        public void SeedWindow(nint hwnd, string workspaceId)
        {
            Windows.Windows[hwnd] = new WindowState
            {
                Hwnd = hwnd,
                ProcessId = (int)hwnd,
                ProcessPath = $"app{hwnd}.exe",
                WindowClass = "Class",
                Title = $"Window {hwnd}",
                MonitorId = MonitorId,
                WorkspaceId = workspaceId,
                IsVisible = true,
                NormalBounds = new Rectangle(0, 0, 100, 100),
                LastUpdated = DateTimeOffset.UtcNow
            };
            Monitors.WindowToMonitorId[hwnd] = MonitorId;
            _intended[hwnd] = workspaceId;

            Tracker.Rescan();

            foreach (var (handle, intended) in _intended)
            {
                if (Tracker.TrackedWindows.TryGetValue(handle, out var state)) state.WorkspaceId = intended;
            }
        }
    }

    private static Harness CreateHarness(IWorkspaceTransitionAnimator? animator = null)
    {
        var windows = new FakeWindowManager();
        var monitors = new FakeMonitorManager();
        var events = new FakeWindowEventSource();
        var guard = new OperationGuard();
        var tracker = new WindowTracker(windows, events, monitors, guard);
        var manager = new WorkspaceManager(windows, tracker, guard, processManager: null, animator: animator);

        monitors.Monitors.Add(new Monitor(MonitorId, @"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true));

        return new Harness { Windows = windows, Monitors = monitors, Tracker = tracker, Manager = manager };
    }

    private static WorkspaceDefinition[] Spaces(params int[] indexes) =>
        indexes.Select(i => new WorkspaceDefinition($"{MonitorId}:{i}", $"Space {i}", i)).ToArray();

    private static string Space(int index) => $"{MonitorId}:{index}";

    // ---- Startup reconciliation ----------------------------------------

    [Fact]
    public void ResetToSingleWorkspace_LeavesTheMonitorWithExactlyOneSpace()
    {
        var harness = CreateHarness();
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2, 3));

        harness.Manager.ResetToSingleWorkspace(MonitorId, Spaces(1)[0]);

        var remaining = harness.Manager.GetWorkspaces(MonitorId);
        Assert.Equal(new[] { Space(1) }, remaining.Select(w => w.Id));
        Assert.Equal(Space(1), harness.Manager.GetActiveWorkspace(MonitorId));
    }

    [Fact]
    public void ResetToSingleWorkspace_CollectsEveryWindowOnTheMonitorIntoThatSpace()
    {
        var harness = CreateHarness();
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2, 3));
        harness.SeedWindow(1, Space(1));
        harness.SeedWindow(2, Space(2));
        harness.SeedWindow(3, Space(3));

        harness.Manager.ResetToSingleWorkspace(MonitorId, Spaces(1)[0]);

        Assert.All(harness.Tracker.TrackedWindows.Values, w => Assert.Equal(Space(1), w.WorkspaceId));
    }

    [Fact]
    public void ResetToSingleWorkspace_TouchesNoWindow()
    {
        // The app has only just started: everything on screen is already
        // exactly as the user left it, so reconciling must be pure
        // bookkeeping. Hiding or moving anything here would be the app
        // rearranging the desktop at launch.
        var harness = CreateHarness();
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));
        harness.SeedWindow(1, Space(1));
        harness.SeedWindow(2, Space(2));
        harness.Windows.Operations.Clear();

        harness.Manager.ResetToSingleWorkspace(MonitorId, Spaces(1)[0]);

        Assert.Empty(harness.Windows.Operations);
    }

    [Fact]
    public void ResetToSingleWorkspace_LeavesOtherMonitorsAlone()
    {
        const string other = "MON-2";
        var harness = CreateHarness();
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));
        harness.Manager.SetMonitorWorkspaces(other, new[]
        {
            new WorkspaceDefinition($"{other}:1", "Space 1", 1),
            new WorkspaceDefinition($"{other}:2", "Space 2", 2)
        });

        harness.Manager.ResetToSingleWorkspace(MonitorId, Spaces(1)[0]);

        Assert.Equal(2, harness.Manager.GetWorkspaces(other).Count);
    }

    // ---- Direction ------------------------------------------------------

    [Fact]
    public void SwitchToALaterSpace_MovesNext()
    {
        var animator = new RecordingAnimator();
        var harness = CreateHarness(animator);
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2, 3));
        harness.Manager.SwitchWorkspace(MonitorId, Space(1));

        harness.Manager.SwitchWorkspace(MonitorId, Space(3));

        Assert.Equal(TransitionDirection.Next, animator.Transitions.Last().Direction);
    }

    [Fact]
    public void SwitchToAnEarlierSpace_MovesPrevious()
    {
        var animator = new RecordingAnimator();
        var harness = CreateHarness(animator);
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2, 3));
        harness.Manager.SwitchWorkspace(MonitorId, Space(3));

        harness.Manager.SwitchWorkspace(MonitorId, Space(1));

        Assert.Equal(TransitionDirection.Previous, animator.Transitions.Last().Direction);
    }

    [Fact]
    public void NextFromTheLastSpace_StillMovesNext_DespiteWrappingLeft()
    {
        // Positions alone would read last -> first as a backwards move. The
        // user pressed "next space", and that is what it has to look like.
        var animator = new RecordingAnimator();
        var harness = CreateHarness(animator);
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2, 3));
        harness.Manager.SwitchWorkspace(MonitorId, Space(3));

        harness.Manager.SwitchRelative(MonitorId, 1);

        Assert.Equal(Space(1), harness.Manager.GetActiveWorkspace(MonitorId));
        Assert.Equal(TransitionDirection.Next, animator.Transitions.Last().Direction);
    }

    [Fact]
    public void PreviousFromTheFirstSpace_StillMovesPrevious_DespiteWrappingRight()
    {
        var animator = new RecordingAnimator();
        var harness = CreateHarness(animator);
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2, 3));
        harness.Manager.SwitchWorkspace(MonitorId, Space(1));

        harness.Manager.SwitchRelative(MonitorId, -1);

        Assert.Equal(Space(3), harness.Manager.GetActiveWorkspace(MonitorId));
        Assert.Equal(TransitionDirection.Previous, animator.Transitions.Last().Direction);
    }

    [Fact]
    public void TheFirstSwitchAfterStartup_HasNoDirection()
    {
        var animator = new RecordingAnimator();
        var harness = CreateHarness(animator);
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));

        harness.Manager.SwitchWorkspace(MonitorId, Space(1));

        Assert.Equal(TransitionDirection.None, Assert.Single(animator.Transitions).Direction);
    }

    // ---- The switch lands whatever the animator does ---------------------

    [Fact]
    public void TheAnimatorSeesBothWindowSets()
    {
        var animator = new RecordingAnimator();
        var harness = CreateHarness(animator);
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));
        harness.Manager.SwitchWorkspace(MonitorId, Space(1));
        harness.SeedWindow(1, Space(1));
        harness.SeedWindow(2, Space(2));

        harness.Manager.SwitchWorkspace(MonitorId, Space(2));

        var transition = animator.Transitions.Last();
        Assert.Equal(new nint[] { 1 }, transition.OutgoingWindows);
        Assert.Equal(new nint[] { 2 }, transition.IncomingWindows);
    }

    [Fact]
    public void AnAnimatorThatDoesNothing_StillLeavesTheSwitchApplied()
    {
        var animator = new RecordingAnimator(_ => { });
        var harness = CreateHarness(animator);
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));
        harness.Manager.SwitchWorkspace(MonitorId, Space(1));
        harness.SeedWindow(1, Space(1));
        harness.SeedWindow(2, Space(2));

        harness.Manager.SwitchWorkspace(MonitorId, Space(2));

        // Nothing forces a silent animator's hand mid-switch, but the active
        // space must still have moved — the windows follow when it next
        // applies, and Show All Windows remains the recovery path.
        Assert.Equal(Space(2), harness.Manager.GetActiveWorkspace(MonitorId));
    }

    [Fact]
    public void AnAnimatorThatThrows_StillLeavesTheSwitchApplied()
    {
        var animator = new RecordingAnimator(_ => throw new InvalidOperationException("overlay failed"));
        var harness = CreateHarness(animator);
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));
        harness.Manager.SwitchWorkspace(MonitorId, Space(1));
        harness.SeedWindow(1, Space(1));
        harness.SeedWindow(2, Space(2));

        harness.Manager.SwitchWorkspace(MonitorId, Space(2));

        Assert.Equal(Space(2), harness.Manager.GetActiveWorkspace(MonitorId));
        Assert.False(harness.Windows.Windows[1].IsVisible);
        Assert.True(harness.Windows.Windows[2].IsVisible);
    }

    [Fact]
    public void CallbacksRunAtMostOnce_EvenIfTheAnimatorRepeatsThem()
    {
        var animator = new RecordingAnimator(t =>
        {
            t.ShowIncoming();
            t.HideOutgoing();
            t.ShowIncoming();
            t.HideOutgoing();
        });
        var harness = CreateHarness(animator);
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));
        harness.Manager.SwitchWorkspace(MonitorId, Space(1));
        harness.SeedWindow(1, Space(1));
        harness.SeedWindow(2, Space(2));
        harness.Windows.Operations.Clear();

        harness.Manager.SwitchWorkspace(MonitorId, Space(2));

        var operations = harness.Windows.Operations.ToList();
        Assert.Equal(1, operations.Count(op => op == ((nint)1, "Hide")));
        Assert.Equal(1, operations.Count(op => op == ((nint)2, "Show")));
    }

    [Fact]
    public void IncomingWindowsAreShownBeforeOutgoingOnesAreHidden()
    {
        // The slide mirrors live windows, and a hidden window has nothing to
        // mirror — so both sets have to overlap on screen for a moment.
        var order = new List<string>();
        var animator = new RecordingAnimator(t =>
        {
            t.ShowIncoming();
            order.Add("shown");
            t.HideOutgoing();
            order.Add("hidden");
        });
        var harness = CreateHarness(animator);
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));
        harness.Manager.SwitchWorkspace(MonitorId, Space(1));
        harness.SeedWindow(1, Space(1));
        harness.SeedWindow(2, Space(2));
        harness.Windows.Operations.Clear();
        order.Clear();

        harness.Manager.SwitchWorkspace(MonitorId, Space(2));

        Assert.Equal(new[] { "shown", "hidden" }, order);
        Assert.True(harness.Windows.Windows[2].IsVisible);
        Assert.False(harness.Windows.Windows[1].IsVisible);
    }

    [Fact]
    public void WithNoAnimator_TheSwitchStillApplies()
    {
        var harness = CreateHarness();
        harness.Manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));
        harness.Manager.SwitchWorkspace(MonitorId, Space(1));
        harness.SeedWindow(1, Space(1));
        harness.SeedWindow(2, Space(2));

        harness.Manager.SwitchWorkspace(MonitorId, Space(2));

        Assert.False(harness.Windows.Windows[1].IsVisible);
        Assert.True(harness.Windows.Windows[2].IsVisible);
    }
}
