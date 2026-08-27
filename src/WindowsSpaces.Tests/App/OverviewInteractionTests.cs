using System.Drawing;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.App;

/// <summary>
/// The overview is only useful if its actions actually reach the workspace
/// manager. These cover the interactive paths — switch, focus, close, move,
/// and the add/rename/delete of spaces — through the view model the UI drives.
/// </summary>
public class OverviewInteractionTests
{
    private const string MonitorId = "MON-A";

    private sealed class Harness
    {
        public FakeWindowManager Windows { get; } = new();
        public FakeMonitorManager Monitors { get; } = new();
        public WindowTracker Tracker { get; }
        public WorkspaceManager Manager { get; }

        /// <summary>Stands in for AppHost's config-owning add/remove/rename.</summary>
        public List<WorkspaceDefinition> Spaces { get; } = new();

        public string? LastError { get; set; }

        public Harness(int spaceCount)
        {
            var events = new FakeWindowEventSource();
            var guard = new OperationGuard();
            Tracker = new WindowTracker(Windows, events, Monitors, guard);
            Manager = new WorkspaceManager(Windows, Tracker, guard);

            Monitors.Monitors.Add(new Monitor(MonitorId, "\\\\.\\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true));

            for (var i = 1; i <= spaceCount; i++)
            {
                Spaces.Add(new WorkspaceDefinition($"{MonitorId}:{i}", $"Space {i}", i));
            }

            Manager.SetMonitorWorkspaces(MonitorId, Spaces);
            Manager.SwitchWorkspace(MonitorId, Spaces[0].Id);
        }

        public bool Add(string monitorId, out string? newWorkspaceId, out string? error)
        {
            var index = Spaces.Count == 0 ? 1 : Spaces.Max(s => s.Index) + 1;
            newWorkspaceId = $"{monitorId}:{index}";
            Spaces.Add(new WorkspaceDefinition(newWorkspaceId, $"Space {index}", index));
            Manager.SetMonitorWorkspaces(monitorId, Spaces);
            error = null;
            return true;
        }

        public bool Remove(string monitorId, string workspaceId, out string? error)
        {
            if (Spaces.Count <= 1)
            {
                error = "A monitor must keep at least one space.";
                return false;
            }

            Spaces.RemoveAll(s => s.Id == workspaceId);
            Manager.SetMonitorWorkspaces(monitorId, Spaces);
            error = null;
            return true;
        }

        public bool Rename(string monitorId, string workspaceId, string newName, out string? error)
        {
            for (var i = 0; i < Spaces.Count; i++)
            {
                if (Spaces[i].Id == workspaceId) Spaces[i] = Spaces[i] with { Name = newName };
            }

            Manager.SetMonitorWorkspaces(monitorId, Spaces);
            error = null;
            return true;
        }

        public OverviewViewModel CreateViewModel() =>
            new(MonitorId, Manager, Tracker, Add, Remove, Rename);

        private readonly Dictionary<nint, string> _intendedWorkspaces = new();

        /// <summary>
        /// Rescan() re-derives every tracked window's workspace from scratch,
        /// so the intended assignments have to be re-applied to all seeded
        /// windows after each one — not just the window being added.
        /// </summary>
        public void SeedWindow(nint hwnd, string workspaceId, string title = "Window")
        {
            Windows.Windows[hwnd] = new WindowState
            {
                Hwnd = hwnd,
                ProcessId = (int)hwnd,
                ProcessPath = @"C:\Program Files\Contoso\contoso.exe",
                WindowClass = "Class",
                Title = title,
                MonitorId = MonitorId,
                WorkspaceId = workspaceId,
                IsVisible = true,
                NormalBounds = new Rectangle(0, 0, 100, 100),
                LastUpdated = DateTimeOffset.UtcNow
            };
            Monitors.WindowToMonitorId[hwnd] = MonitorId;
            _intendedWorkspaces[hwnd] = workspaceId;

            Tracker.Rescan();

            foreach (var (handle, intended) in _intendedWorkspaces)
            {
                if (Tracker.TrackedWindows.TryGetValue(handle, out var state))
                {
                    state.WorkspaceId = intended;
                }
            }
        }
    }

    [Fact]
    public void Refresh_NumbersSpacesByPositionAndMarksTheActiveOne()
    {
        var harness = new Harness(3);
        harness.Manager.SwitchWorkspace(MonitorId, $"{MonitorId}:2");

        var vm = harness.CreateViewModel();

        Assert.Equal(new[] { 1, 2, 3 }, vm.Workspaces.Select(w => w.Position));
        Assert.Equal($"{MonitorId}:2", vm.Workspaces.Single(w => w.IsActive).WorkspaceId);
    }

    [Fact]
    public void SwitchToWorkspace_ChangesTheMonitorsActiveSpace()
    {
        var harness = new Harness(2);
        var vm = harness.CreateViewModel();

        Assert.True(vm.SwitchToWorkspace($"{MonitorId}:2"));

        Assert.Equal($"{MonitorId}:2", harness.Manager.GetActiveWorkspace(MonitorId));
        Assert.True(vm.Workspaces.Single(w => w.WorkspaceId == $"{MonitorId}:2").IsActive);
    }

    [Fact]
    public void SwitchToPosition_UsesPositionNotStoredIndex()
    {
        var harness = new Harness(3);
        var vm = harness.CreateViewModel();
        vm.RemoveWorkspace($"{MonitorId}:2");

        // Remaining spaces are indexes 1 and 3, so position 2 is "MON-A:3".
        Assert.True(vm.SwitchToPosition(2));
        Assert.Equal($"{MonitorId}:3", harness.Manager.GetActiveWorkspace(MonitorId));

        Assert.False(vm.SwitchToPosition(3));
    }

    [Fact]
    public void ActivateWindow_FocusesItAndBringsItsSpaceForward()
    {
        var harness = new Harness(2);
        harness.SeedWindow(11, $"{MonitorId}:2");
        var vm = harness.CreateViewModel();

        vm.ActivateWindow(11);

        Assert.Equal($"{MonitorId}:2", harness.Manager.GetActiveWorkspace(MonitorId));
        Assert.Equal((nint)11, harness.Windows.Foreground);
    }

    [Fact]
    public void CloseWindow_RequestsTheCloseAndDropsTheTileImmediately()
    {
        var harness = new Harness(2);
        harness.SeedWindow(12, $"{MonitorId}:1");
        var vm = harness.CreateViewModel();

        vm.CloseWindow(12);

        Assert.Contains((nint)12, harness.Windows.Closed);
        Assert.DoesNotContain(vm.Workspaces.SelectMany(w => w.Windows), w => w.Hwnd == 12);
    }

    [Fact]
    public void MoveWindowToWorkspace_ReassignsAndMovesTheTile()
    {
        var harness = new Harness(2);
        harness.SeedWindow(13, $"{MonitorId}:1");
        var vm = harness.CreateViewModel();

        Assert.True(vm.MoveWindowToWorkspace(13, $"{MonitorId}:2"));

        Assert.Equal($"{MonitorId}:2", harness.Tracker.TrackedWindows[13].WorkspaceId);
        Assert.Empty(vm.Workspaces.Single(w => w.WorkspaceId == $"{MonitorId}:1").Windows);
        Assert.Single(vm.Workspaces.Single(w => w.WorkspaceId == $"{MonitorId}:2").Windows);
    }

    [Fact]
    public void MoveWindowToWorkspace_SameSpace_IsANoOp()
    {
        var harness = new Harness(2);
        harness.SeedWindow(14, $"{MonitorId}:1");
        var vm = harness.CreateViewModel();

        Assert.False(vm.MoveWindowToWorkspace(14, $"{MonitorId}:1"));
    }

    [Fact]
    public void AddWorkspace_AppendsASpaceAndSelectsItWithoutSwitching()
    {
        var harness = new Harness(2);
        var vm = harness.CreateViewModel();

        Assert.True(vm.AddWorkspace());

        Assert.Equal(3, vm.Workspaces.Count);

        // The space on screen is unchanged: adding a space edits the list, it
        // does not leave the overview.
        Assert.Equal($"{MonitorId}:1", harness.Manager.GetActiveWorkspace(MonitorId));
        Assert.False(vm.Workspaces[2].IsActive);
        Assert.True(vm.Workspaces[2].IsSelected);
    }

    [Fact]
    public void RemoveWorkspace_DropsTheColumnAndRelocatesItsWindows()
    {
        var harness = new Harness(3);
        harness.SeedWindow(15, $"{MonitorId}:2");
        var vm = harness.CreateViewModel();

        Assert.True(vm.RemoveWorkspace($"{MonitorId}:2"));

        Assert.Equal(2, vm.Workspaces.Count);
        Assert.Equal($"{MonitorId}:1", harness.Tracker.TrackedWindows[15].WorkspaceId);
        Assert.Single(vm.Workspaces.Single(w => w.WorkspaceId == $"{MonitorId}:1").Windows);
    }

    [Fact]
    public void RemoveWorkspace_LastRemainingSpace_IsRefusedWithAReason()
    {
        var harness = new Harness(1);
        var vm = harness.CreateViewModel();

        Assert.False(vm.RemoveWorkspace($"{MonitorId}:1"));

        Assert.Single(vm.Workspaces);
        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));
    }

    [Fact]
    public void SingleSpace_HidesTheDeleteAffordance()
    {
        var harness = new Harness(1);
        var vm = harness.CreateViewModel();

        Assert.False(vm.Workspaces[0].CanRemove);
    }

    [Fact]
    public void RenameWorkspace_UpdatesTheCardTitle()
    {
        var harness = new Harness(2);
        var vm = harness.CreateViewModel();

        Assert.True(vm.RenameWorkspace($"{MonitorId}:1", "  Email  "));

        Assert.Equal("Email", vm.Workspaces[0].Name);
    }

    [Fact]
    public void RenameWorkspace_EmptyName_IsRejectedWithoutChangingAnything()
    {
        var harness = new Harness(2);
        var vm = harness.CreateViewModel();

        Assert.False(vm.RenameWorkspace($"{MonitorId}:1", "   "));

        Assert.Equal("Space 1", vm.Workspaces[0].Name);
    }

    [Fact]
    public void MoveSelection_WrapsAroundBothEnds()
    {
        var harness = new Harness(3);
        var vm = harness.CreateViewModel();
        vm.Select($"{MonitorId}:1");

        vm.MoveSelection(-1);
        Assert.Equal($"{MonitorId}:3", vm.SelectedWorkspace?.WorkspaceId);

        vm.MoveSelection(1);
        Assert.Equal($"{MonitorId}:1", vm.SelectedWorkspace?.WorkspaceId);
    }

    [Fact]
    public void Refresh_PreservesTheKeyboardSelection()
    {
        var harness = new Harness(3);
        var vm = harness.CreateViewModel();
        vm.Select($"{MonitorId}:3");

        vm.Refresh();

        Assert.Equal($"{MonitorId}:3", vm.SelectedWorkspace?.WorkspaceId);
    }

    [Fact]
    public void OnlyWindowsInTheOnScreenSpaceOfferALivePreview()
    {
        var harness = new Harness(2);
        harness.SeedWindow(16, $"{MonitorId}:1");
        harness.SeedWindow(17, $"{MonitorId}:2");
        var vm = harness.CreateViewModel();

        var onScreen = vm.Workspaces.Single(w => w.WorkspaceId == $"{MonitorId}:1").Windows.Single();
        var hidden = vm.Workspaces.Single(w => w.WorkspaceId == $"{MonitorId}:2").Windows.Single();

        Assert.True(onScreen.CanPreview);
        Assert.False(hidden.CanPreview);
    }

    [Fact]
    public void WindowTile_FallsBackToTheProcessNameWhenTheTitleIsBlank()
    {
        var harness = new Harness(1);
        harness.SeedWindow(18, $"{MonitorId}:1", title: "   ");
        var vm = harness.CreateViewModel();

        var tile = vm.Workspaces[0].Windows.Single();

        Assert.Equal("contoso", tile.Title);
        Assert.Equal("C", tile.Initial);
    }
}
