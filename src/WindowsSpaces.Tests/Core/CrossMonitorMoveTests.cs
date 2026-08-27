using System.Drawing;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.Core;

/// <summary>
/// Covers moving a window — or a whole space's worth of windows — from one
/// monitor to another, which is what dragging a tile or a space card onto
/// another monitor's overview pane does.
/// </summary>
public class CrossMonitorMoveTests
{
    private const string MonitorA = "MON-A";
    private const string MonitorB = "MON-B";

    private static readonly Rectangle BoundsA = new(0, 0, 1920, 1080);
    private static readonly Rectangle BoundsB = new(1920, 0, 1920, 1080);

    private sealed record Harness(
        FakeWindowManager Windows,
        FakeMonitorManager Monitors,
        WindowTracker Tracker,
        WorkspaceManager Manager);

    private static Harness CreateHarness()
    {
        var windows = new FakeWindowManager();
        var monitors = new FakeMonitorManager();
        var events = new FakeWindowEventSource();
        var guard = new OperationGuard();
        var tracker = new WindowTracker(windows, events, monitors, guard);
        var manager = new WorkspaceManager(windows, tracker, guard);

        monitors.Monitors.Add(new Monitor(MonitorA, "\\\\.\\DISPLAY1", BoundsA, IsPrimary: true));
        monitors.Monitors.Add(new Monitor(MonitorB, "\\\\.\\DISPLAY2", BoundsB, IsPrimary: false));

        manager.SetMonitorWorkspaces(MonitorA, Spaces(MonitorA, 1, 2));
        manager.SetMonitorWorkspaces(MonitorB, Spaces(MonitorB, 1, 2));
        manager.SwitchWorkspace(MonitorA, $"{MonitorA}:1");
        manager.SwitchWorkspace(MonitorB, $"{MonitorB}:1");

        return new Harness(windows, monitors, tracker, manager);
    }

    private static WorkspaceDefinition[] Spaces(string monitorId, params int[] indexes) =>
        indexes.Select(i => new WorkspaceDefinition($"{monitorId}:{i}", $"Space {i}", i)).ToArray();

    // xUnit builds a fresh instance of the class per test, so this stays
    // scoped to one test.
    private readonly Dictionary<nint, (string MonitorId, string WorkspaceId, Rectangle Bounds)> _intended = new();

    /// <summary>
    /// Rescan() re-tracks every window from scratch and puts each one on its
    /// monitor's first space, so the intended assignments are re-applied to
    /// all seeded windows after each one.
    /// </summary>
    private void SeedWindow(Harness harness, nint hwnd, string monitorId, string workspaceId, Rectangle bounds)
    {
        harness.Windows.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd,
            ProcessId = (int)hwnd,
            ProcessPath = $"app{hwnd}.exe",
            WindowClass = "Class",
            Title = $"Window {hwnd}",
            MonitorId = monitorId,
            WorkspaceId = workspaceId,
            IsVisible = true,
            NormalBounds = bounds,
            LastUpdated = DateTimeOffset.UtcNow
        };
        harness.Monitors.WindowToMonitorId[hwnd] = monitorId;
        _intended[hwnd] = (monitorId, workspaceId, bounds);

        harness.Tracker.Rescan();

        foreach (var (handle, intended) in _intended)
        {
            if (!harness.Tracker.TrackedWindows.TryGetValue(handle, out var state)) continue;

            state.MonitorId = intended.MonitorId;
            state.WorkspaceId = intended.WorkspaceId;
            state.NormalBounds = intended.Bounds;
        }
    }

    [Fact]
    public void MoveWindowToMonitor_ReassignsMonitorAndWorkspace()
    {
        var harness = CreateHarness();
        SeedWindow(harness, 1, MonitorA, $"{MonitorA}:1", new Rectangle(100, 100, 800, 600));

        var moved = harness.Manager.MoveWindowToMonitor(1, MonitorB, $"{MonitorB}:1", BoundsA, BoundsB);

        Assert.True(moved);
        var state = harness.Tracker.TrackedWindows[1];
        Assert.Equal(MonitorB, state.MonitorId);
        Assert.Equal($"{MonitorB}:1", state.WorkspaceId);
    }

    [Fact]
    public void MoveWindowToMonitor_RepositionsOntoTheTargetMonitor()
    {
        var harness = CreateHarness();
        SeedWindow(harness, 1, MonitorA, $"{MonitorA}:1", new Rectangle(100, 100, 800, 600));

        harness.Manager.MoveWindowToMonitor(1, MonitorB, $"{MonitorB}:1", BoundsA, BoundsB);

        // Same size and offset, on the other screen: the two monitors are the
        // same resolution, so only the origin shifts.
        var expected = new Rectangle(2020, 100, 800, 600);
        Assert.Equal(expected, harness.Tracker.TrackedWindows[1].NormalBounds);
        Assert.Equal(expected, harness.Windows.Windows[1].NormalBounds);
        Assert.Contains((1, "Move"), harness.Windows.Operations);
    }

    [Fact]
    public void MoveWindowToMonitor_ShowsWhenTargetSpaceIsOnScreen()
    {
        var harness = CreateHarness();
        SeedWindow(harness, 1, MonitorA, $"{MonitorA}:1", new Rectangle(0, 0, 400, 300));

        harness.Manager.MoveWindowToMonitor(1, MonitorB, $"{MonitorB}:1", BoundsA, BoundsB);

        Assert.True(harness.Windows.Windows[1].IsVisible);
    }

    [Fact]
    public void MoveWindowToMonitor_HidesWhenTargetSpaceIsNotOnScreen()
    {
        var harness = CreateHarness();
        SeedWindow(harness, 1, MonitorA, $"{MonitorA}:1", new Rectangle(0, 0, 400, 300));

        harness.Manager.MoveWindowToMonitor(1, MonitorB, $"{MonitorB}:2", BoundsA, BoundsB);

        Assert.False(harness.Windows.Windows[1].IsVisible);
        Assert.Equal($"{MonitorB}:2", harness.Tracker.TrackedWindows[1].WorkspaceId);
    }

    [Fact]
    public void MoveWindowToMonitor_RefusesASpaceTheTargetMonitorDoesNotHave()
    {
        var harness = CreateHarness();
        SeedWindow(harness, 1, MonitorA, $"{MonitorA}:1", new Rectangle(0, 0, 400, 300));

        // Assigning to a non-existent space would hide the window with no
        // space able to bring it back — the worst outcome in the reliability
        // rules, so the move is refused outright.
        var moved = harness.Manager.MoveWindowToMonitor(1, MonitorB, $"{MonitorB}:9", BoundsA, BoundsB);

        Assert.False(moved);
        var state = harness.Tracker.TrackedWindows[1];
        Assert.Equal(MonitorA, state.MonitorId);
        Assert.Equal($"{MonitorA}:1", state.WorkspaceId);
        Assert.True(harness.Windows.Windows[1].IsVisible);
    }

    [Fact]
    public void MoveWindowToMonitor_IgnoresAnUnknownWindow()
    {
        var harness = CreateHarness();

        Assert.False(harness.Manager.MoveWindowToMonitor(42, MonitorB, $"{MonitorB}:1", BoundsA, BoundsB));
    }

    [Fact]
    public void MoveWorkspaceWindowsToMonitor_MovesOnlyThatSpacesWindows()
    {
        var harness = CreateHarness();
        SeedWindow(harness, 1, MonitorA, $"{MonitorA}:2", new Rectangle(0, 0, 400, 300));
        SeedWindow(harness, 2, MonitorA, $"{MonitorA}:2", new Rectangle(400, 0, 400, 300));
        SeedWindow(harness, 3, MonitorA, $"{MonitorA}:1", new Rectangle(0, 300, 400, 300));
        SeedWindow(harness, 4, MonitorB, $"{MonitorB}:1", new Rectangle(1920, 0, 400, 300));

        var moved = harness.Manager.MoveWorkspaceWindowsToMonitor(
            MonitorA, $"{MonitorA}:2", MonitorB, $"{MonitorB}:2", BoundsA, BoundsB);

        Assert.Equal(2, moved);
        Assert.Equal(MonitorB, harness.Tracker.TrackedWindows[1].MonitorId);
        Assert.Equal($"{MonitorB}:2", harness.Tracker.TrackedWindows[1].WorkspaceId);
        Assert.Equal(MonitorB, harness.Tracker.TrackedWindows[2].MonitorId);

        // The other space on the source monitor, and the target monitor's own
        // windows, are untouched.
        Assert.Equal(MonitorA, harness.Tracker.TrackedWindows[3].MonitorId);
        Assert.Equal($"{MonitorA}:1", harness.Tracker.TrackedWindows[3].WorkspaceId);
        Assert.Equal($"{MonitorB}:1", harness.Tracker.TrackedWindows[4].WorkspaceId);
    }

    [Fact]
    public void MoveWorkspaceWindowsToMonitor_LeavesTheSourceSpaceEmpty()
    {
        var harness = CreateHarness();
        SeedWindow(harness, 1, MonitorA, $"{MonitorA}:2", new Rectangle(0, 0, 400, 300));

        harness.Manager.MoveWorkspaceWindowsToMonitor(
            MonitorA, $"{MonitorA}:2", MonitorB, $"{MonitorB}:2", BoundsA, BoundsB);

        Assert.DoesNotContain(
            harness.Tracker.TrackedWindows.Values,
            w => w.MonitorId == MonitorA && w.WorkspaceId == $"{MonitorA}:2");
    }
}
