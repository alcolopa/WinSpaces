using System.Drawing;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.App;

/// <summary>
/// Covers the overview's cross-monitor drops: dragging a window tile onto
/// another monitor's pane, and dragging a whole space card onto it. The view
/// model must route both to AppHost rather than touching windows itself, and
/// must surface a refusal instead of silently doing nothing.
/// </summary>
public class OverviewCrossMonitorTests
{
    private const string MonitorA = "MON-A";
    private const string MonitorB = "MON-B";

    private sealed class Harness
    {
        public FakeWindowManager Windows { get; } = new();
        public FakeMonitorManager Monitors { get; } = new();
        public WindowTracker Tracker { get; }
        public WorkspaceManager Manager { get; }

        public List<(nint Hwnd, string MonitorId, string WorkspaceId)> WindowMoves { get; } = new();
        public List<(string Source, string WorkspaceId, string Target)> WorkspaceMoves { get; } = new();

        /// <summary>When set, both handlers refuse with this message — standing in for a refusal from AppHost.</summary>
        public string? RefuseWith { get; set; }

        public Harness()
        {
            var events = new FakeWindowEventSource();
            var guard = new OperationGuard();
            Tracker = new WindowTracker(Windows, events, Monitors, guard);
            Manager = new WorkspaceManager(Windows, Tracker, guard);

            Monitors.Monitors.Add(new Monitor(MonitorA, "\\\\.\\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true));
            Monitors.Monitors.Add(new Monitor(MonitorB, "\\\\.\\DISPLAY2", new Rectangle(1920, 0, 1920, 1080), IsPrimary: false));

            Manager.SetMonitorWorkspaces(MonitorA, Spaces(MonitorA, 1, 2));
            Manager.SetMonitorWorkspaces(MonitorB, Spaces(MonitorB, 1, 2));
            Manager.SwitchWorkspace(MonitorA, $"{MonitorA}:1");
            Manager.SwitchWorkspace(MonitorB, $"{MonitorB}:1");
        }

        private static WorkspaceDefinition[] Spaces(string monitorId, params int[] indexes) =>
            indexes.Select(i => new WorkspaceDefinition($"{monitorId}:{i}", $"Space {i}", i)).ToArray();

        public bool MoveWindow(nint hwnd, string targetMonitorId, string targetWorkspaceId, out string? error)
        {
            if (RefuseWith is not null)
            {
                error = RefuseWith;
                return false;
            }

            WindowMoves.Add((hwnd, targetMonitorId, targetWorkspaceId));
            error = null;
            return true;
        }

        public bool MoveWorkspace(string sourceMonitorId, string workspaceId, string targetMonitorId, out string? newWorkspaceId, out string? error)
        {
            if (RefuseWith is not null)
            {
                newWorkspaceId = null;
                error = RefuseWith;
                return false;
            }

            WorkspaceMoves.Add((sourceMonitorId, workspaceId, targetMonitorId));
            newWorkspaceId = $"{targetMonitorId}:9";
            error = null;
            return true;
        }

        public OverviewViewModel CreateViewModel(bool withMoveHandlers = true) =>
            withMoveHandlers
                ? new OverviewViewModel(MonitorA, Manager, Tracker, null, null, null, MoveWindow, MoveWorkspace)
                : new OverviewViewModel(MonitorA, Manager, Tracker);

        public void SeedWindow(nint hwnd, string monitorId, string workspaceId)
        {
            Windows.Windows[hwnd] = new WindowState
            {
                Hwnd = hwnd,
                ProcessId = (int)hwnd,
                ProcessPath = $"app{hwnd}.exe",
                WindowClass = "Class",
                Title = $"Window {hwnd}",
                MonitorId = monitorId,
                WorkspaceId = workspaceId,
                IsVisible = true,
                NormalBounds = new Rectangle(0, 0, 400, 300),
                LastUpdated = DateTimeOffset.UtcNow
            };
            Monitors.WindowToMonitorId[hwnd] = monitorId;

            Tracker.Rescan();

            var state = Tracker.TrackedWindows[hwnd];
            state.MonitorId = monitorId;
            state.WorkspaceId = workspaceId;
        }
    }

    [Fact]
    public void MoveWindowToMonitor_RoutesTheDropToTheHost()
    {
        var harness = new Harness();
        harness.SeedWindow(1, MonitorA, $"{MonitorA}:1");
        var viewModel = harness.CreateViewModel();

        Assert.True(viewModel.MoveWindowToMonitor(1, MonitorB, $"{MonitorB}:2"));

        Assert.Equal((1, MonitorB, $"{MonitorB}:2"), Assert.Single(harness.WindowMoves));
    }

    [Fact]
    public void MoveWindowToMonitor_DropOnOwnMonitorIsJustASpaceChange()
    {
        var harness = new Harness();
        harness.SeedWindow(1, MonitorA, $"{MonitorA}:1");
        var viewModel = harness.CreateViewModel();

        Assert.True(viewModel.MoveWindowToMonitor(1, MonitorA, $"{MonitorA}:2"));

        // Handled locally by the plain assignment path, not as a monitor move.
        Assert.Empty(harness.WindowMoves);
        Assert.Equal($"{MonitorA}:2", harness.Tracker.TrackedWindows[1].WorkspaceId);
    }

    [Fact]
    public void MoveWindowToMonitor_ReportsARefusal()
    {
        var harness = new Harness { RefuseWith = "That monitor is no longer connected." };
        harness.SeedWindow(1, MonitorA, $"{MonitorA}:1");
        var viewModel = harness.CreateViewModel();

        Assert.False(viewModel.MoveWindowToMonitor(1, MonitorB, $"{MonitorB}:1"));
        Assert.Equal("That monitor is no longer connected.", viewModel.StatusMessage);
    }

    [Fact]
    public void MoveWindowToMonitor_ReportsWhenTheHostCannotDoIt()
    {
        var harness = new Harness();
        harness.SeedWindow(1, MonitorA, $"{MonitorA}:1");
        var viewModel = harness.CreateViewModel(withMoveHandlers: false);

        Assert.False(viewModel.MoveWindowToMonitor(1, MonitorB, $"{MonitorB}:1"));
        Assert.False(string.IsNullOrEmpty(viewModel.StatusMessage));
    }

    [Fact]
    public void MoveWorkspaceToMonitor_RoutesTheDropToTheHost()
    {
        var harness = new Harness();
        var viewModel = harness.CreateViewModel();

        Assert.True(viewModel.MoveWorkspaceToMonitor($"{MonitorA}:2", MonitorB));

        Assert.Equal((MonitorA, $"{MonitorA}:2", MonitorB), Assert.Single(harness.WorkspaceMoves));
    }

    [Fact]
    public void MoveWorkspaceToMonitor_DropOnOwnMonitorDoesNothing()
    {
        var harness = new Harness();
        var viewModel = harness.CreateViewModel();

        // The strip does not reorder, so a space dropped back on its own
        // monitor is a no-op rather than an error the user has to read.
        Assert.False(viewModel.MoveWorkspaceToMonitor($"{MonitorA}:2", MonitorA));
        Assert.Empty(harness.WorkspaceMoves);
        Assert.Null(viewModel.StatusMessage);
    }

    [Fact]
    public void MoveWorkspaceToMonitor_ReportsARefusal()
    {
        var harness = new Harness { RefuseWith = "A monitor must keep at least one space." };
        var viewModel = harness.CreateViewModel();

        Assert.False(viewModel.MoveWorkspaceToMonitor($"{MonitorA}:1", MonitorB));
        Assert.Equal("A monitor must keep at least one space.", viewModel.StatusMessage);
    }
}
