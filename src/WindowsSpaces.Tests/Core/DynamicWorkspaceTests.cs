using System.Drawing;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.Core;

/// <summary>
/// Covers spaces being created and deleted while the app runs, and the
/// relative ("next/previous space") navigation that depends on the live list.
/// </summary>
public class DynamicWorkspaceTests
{
    private const string MonitorId = "MON-1";

    private static (FakeWindowManager Windows, WindowTracker Tracker, WorkspaceManager Manager, FakeMonitorManager Monitors)
        CreateHarness()
    {
        var windows = new FakeWindowManager();
        var monitors = new FakeMonitorManager();
        var events = new FakeWindowEventSource();
        var guard = new OperationGuard();
        var tracker = new WindowTracker(windows, events, monitors, guard);
        var manager = new WorkspaceManager(windows, tracker, guard);

        monitors.Monitors.Add(new Monitor(MonitorId, "\\\\.\\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true));

        return (windows, tracker, manager, monitors);
    }

    private static WorkspaceDefinition[] Spaces(params int[] indexes) =>
        indexes.Select(i => new WorkspaceDefinition($"{MonitorId}:{i}", $"Space {i}", i)).ToArray();

    // xUnit builds a fresh instance of the class per test, so this stays
    // scoped to one test.
    private readonly Dictionary<nint, string> _intendedWorkspaces = new();

    /// <summary>
    /// Rescan() re-derives every tracked window's workspace, so the intended
    /// assignments are re-applied to all seeded windows after each one.
    /// </summary>
    private void SeedWindow(FakeWindowManager windows, FakeMonitorManager monitors, WindowTracker tracker, nint hwnd, string workspaceId)
    {
        windows.Windows[hwnd] = new WindowState
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
        monitors.WindowToMonitorId[hwnd] = MonitorId;
        _intendedWorkspaces[hwnd] = workspaceId;

        tracker.Rescan();

        foreach (var (handle, intended) in _intendedWorkspaces)
        {
            if (tracker.TrackedWindows.TryGetValue(handle, out var state))
            {
                state.WorkspaceId = intended;
            }
        }
    }

    [Fact]
    public void SetMonitorWorkspaces_AddingASpace_MakesItSwitchable()
    {
        var (_, _, manager, _) = CreateHarness();
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));

        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2, 3));

        Assert.Equal(3, manager.GetWorkspaces(MonitorId).Count);

        manager.SwitchWorkspace(MonitorId, $"{MonitorId}:3");
        Assert.Equal($"{MonitorId}:3", manager.GetActiveWorkspace(MonitorId));
    }

    [Fact]
    public void SwitchWorkspace_UnknownWorkspace_IsRefusedRatherThanBlankingTheMonitor()
    {
        var (windows, tracker, manager, monitors) = CreateHarness();
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));
        SeedWindow(windows, monitors, tracker, 101, $"{MonitorId}:1");
        manager.SwitchWorkspace(MonitorId, $"{MonitorId}:1");

        manager.SwitchWorkspace(MonitorId, $"{MonitorId}:9");

        Assert.Equal($"{MonitorId}:1", manager.GetActiveWorkspace(MonitorId));
        Assert.True(windows.Windows[101].IsVisible);
    }

    [Fact]
    public void SetMonitorWorkspaces_DeletingASpace_MovesItsWindowsToTheSpaceOnItsLeft()
    {
        var (windows, tracker, manager, monitors) = CreateHarness();
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2, 3));
        SeedWindow(windows, monitors, tracker, 201, $"{MonitorId}:2");
        manager.SwitchWorkspace(MonitorId, $"{MonitorId}:1");

        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 3));

        Assert.Equal($"{MonitorId}:1", tracker.TrackedWindows[201].WorkspaceId);
    }

    [Fact]
    public void SetMonitorWorkspaces_DeletingTheFirstSpace_MovesItsWindowsToTheSpaceOnItsRight()
    {
        var (windows, tracker, manager, monitors) = CreateHarness();
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2, 3));
        SeedWindow(windows, monitors, tracker, 202, $"{MonitorId}:1");
        manager.SwitchWorkspace(MonitorId, $"{MonitorId}:3");

        manager.SetMonitorWorkspaces(MonitorId, Spaces(2, 3));

        Assert.Equal($"{MonitorId}:2", tracker.TrackedWindows[202].WorkspaceId);
    }

    [Fact]
    public void SetMonitorWorkspaces_DeletingTheActiveSpace_ShowsANeighbourInstead()
    {
        var (windows, tracker, manager, monitors) = CreateHarness();
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2, 3));
        SeedWindow(windows, monitors, tracker, 301, $"{MonitorId}:1");
        SeedWindow(windows, monitors, tracker, 302, $"{MonitorId}:2");
        manager.SwitchWorkspace(MonitorId, $"{MonitorId}:2");

        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 3));

        Assert.Equal($"{MonitorId}:1", manager.GetActiveWorkspace(MonitorId));

        // Both the window that already lived on space 1 and the one relocated
        // off the deleted space are on screen, not stranded hidden.
        Assert.True(windows.Windows[301].IsVisible);
        Assert.True(windows.Windows[302].IsVisible);
    }

    [Fact]
    public void SetMonitorWorkspaces_EmptyList_IsIgnoredSoAMonitorAlwaysHasASpace()
    {
        var (_, _, manager, _) = CreateHarness();
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));

        manager.SetMonitorWorkspaces(MonitorId, Array.Empty<WorkspaceDefinition>());

        Assert.Equal(2, manager.GetWorkspaces(MonitorId).Count);
    }

    [Fact]
    public void GetWorkspaceIdAt_ResolvesByPositionNotByStoredIndex()
    {
        var (_, _, manager, _) = CreateHarness();

        // Space 2 was deleted, so the surviving indexes are sparse.
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 3, 4));

        Assert.Equal($"{MonitorId}:1", manager.GetWorkspaceIdAt(MonitorId, 1));
        Assert.Equal($"{MonitorId}:3", manager.GetWorkspaceIdAt(MonitorId, 2));
        Assert.Equal($"{MonitorId}:4", manager.GetWorkspaceIdAt(MonitorId, 3));
        Assert.Null(manager.GetWorkspaceIdAt(MonitorId, 4));
    }

    [Fact]
    public void GetWorkspaceIdAt_UnconfiguredMonitor_FallsBackToTheIdConvention()
    {
        var (_, _, manager, _) = CreateHarness();

        Assert.Equal($"{MonitorId}:2", manager.GetWorkspaceIdAt(MonitorId, 2));
    }

    [Fact]
    public void SwitchRelative_CyclesForwardAndWrapsPastTheLastSpace()
    {
        var (_, _, manager, _) = CreateHarness();
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2, 3));
        manager.SwitchWorkspace(MonitorId, $"{MonitorId}:1");

        manager.SwitchRelative(MonitorId, 1);
        Assert.Equal($"{MonitorId}:2", manager.GetActiveWorkspace(MonitorId));

        manager.SwitchRelative(MonitorId, 1);
        Assert.Equal($"{MonitorId}:3", manager.GetActiveWorkspace(MonitorId));

        manager.SwitchRelative(MonitorId, 1);
        Assert.Equal($"{MonitorId}:1", manager.GetActiveWorkspace(MonitorId));
    }

    [Fact]
    public void SwitchRelative_CyclesBackwardAndWrapsPastTheFirstSpace()
    {
        var (_, _, manager, _) = CreateHarness();
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2, 3));
        manager.SwitchWorkspace(MonitorId, $"{MonitorId}:1");

        manager.SwitchRelative(MonitorId, -1);
        Assert.Equal($"{MonitorId}:3", manager.GetActiveWorkspace(MonitorId));

        manager.SwitchRelative(MonitorId, -1);
        Assert.Equal($"{MonitorId}:2", manager.GetActiveWorkspace(MonitorId));
    }

    [Fact]
    public void SwitchRelative_SkipsDeletedSpacesBecauseItWalksTheLiveList()
    {
        var (_, _, manager, _) = CreateHarness();
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2, 3));
        manager.SwitchWorkspace(MonitorId, $"{MonitorId}:1");

        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 3));

        manager.SwitchRelative(MonitorId, 1);
        Assert.Equal($"{MonitorId}:3", manager.GetActiveWorkspace(MonitorId));
    }

    [Fact]
    public void GetRelativeWorkspaceId_SingleSpace_HasNoNeighbour()
    {
        var (_, _, manager, _) = CreateHarness();
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1));
        manager.SwitchWorkspace(MonitorId, $"{MonitorId}:1");

        Assert.Null(manager.GetRelativeWorkspaceId(MonitorId, 1));
        Assert.Null(manager.GetRelativeWorkspaceId(MonitorId, -1));
    }

    [Fact]
    public void AssignWindow_UnknownWorkspace_LeavesTheWindowVisibleRatherThanLosingIt()
    {
        var (windows, tracker, manager, monitors) = CreateHarness();
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));
        SeedWindow(windows, monitors, tracker, 401, $"{MonitorId}:1");
        manager.SwitchWorkspace(MonitorId, $"{MonitorId}:1");

        manager.AssignWindow(401, $"{MonitorId}:7");

        Assert.Equal($"{MonitorId}:1", tracker.TrackedWindows[401].WorkspaceId);
        Assert.True(windows.Windows[401].IsVisible);
    }

    [Fact]
    public void ActivateWindow_InAnotherSpace_SwitchesToThatSpaceFirst()
    {
        var (windows, tracker, manager, monitors) = CreateHarness();
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));
        SeedWindow(windows, monitors, tracker, 501, $"{MonitorId}:2");
        manager.SwitchWorkspace(MonitorId, $"{MonitorId}:1");

        manager.ActivateWindow(501);

        Assert.Equal($"{MonitorId}:2", manager.GetActiveWorkspace(MonitorId));
        Assert.Equal((nint)501, windows.Foreground);
    }

    [Fact]
    public void CloseWindow_HiddenWindow_IsShownFirstSoItsPromptsAreVisible()
    {
        var (windows, tracker, manager, monitors) = CreateHarness();
        manager.SetMonitorWorkspaces(MonitorId, Spaces(1, 2));
        SeedWindow(windows, monitors, tracker, 601, $"{MonitorId}:2");
        manager.SwitchWorkspace(MonitorId, $"{MonitorId}:1");
        Assert.False(windows.Windows[601].IsVisible);

        manager.CloseWindow(601);

        Assert.Contains((nint)601, windows.Closed);
        Assert.True(windows.Windows[601].IsVisible);
    }
}
