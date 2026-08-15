using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.App;

public class OverviewViewModelTests
{
    private static readonly Monitor MonA = new("MON-A", "\\\\.\\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true);
    private static readonly Monitor MonB = new("MON-B", "\\\\.\\DISPLAY2", new Rectangle(1920, 0, 1920, 1080), IsPrimary: false);

    [Fact]
    public void Constructor_PopulatesWorkspacesAndWindowRepresentations()
    {
        var wm = new FakeWindowManager();
        var monitors = new FakeMonitorManager();
        var events = new FakeWindowEventSource();
        var guard = new OperationGuard();
        var tracker = new WindowTracker(wm, events, monitors, guard);
        var manager = new WorkspaceManager(wm, tracker, guard);

        monitors.Monitors.Add(MonA);
        monitors.Monitors.Add(MonB);

        // Seed 2 windows on MON-A, one on Space 1 and one on Space 2
        var hwnd1 = (nint)101;
        wm.Windows[hwnd1] = new WindowState
        {
            Hwnd = hwnd1, ProcessId = 10, ProcessPath = "a.exe", WindowClass = "ClassA", Title = "Window A1",
            MonitorId = "MON-A", WorkspaceId = "MON-A:1", IsVisible = true, NormalBounds = new Rectangle(0, 0, 100, 100), LastUpdated = DateTimeOffset.UtcNow
        };
        var hwnd2 = (nint)102;
        wm.Windows[hwnd2] = new WindowState
        {
            Hwnd = hwnd2, ProcessId = 11, ProcessPath = "b.exe", WindowClass = "ClassB", Title = "Window A2",
            MonitorId = "MON-A", WorkspaceId = "MON-A:2", IsVisible = false, NormalBounds = new Rectangle(0, 0, 100, 100), LastUpdated = DateTimeOffset.UtcNow
        };

        tracker.Rescan();

        var config = AppConfiguration.CreateDefault(new[] { MonA, MonB });
        manager.RenameWorkspace("MON-A:1", "Space 1");
        manager.RenameWorkspace("MON-A:2", "Space 2");
        manager.SwitchWorkspace("MON-A", "MON-A:1");

        var vm = new OverviewViewModel("MON-A", manager, tracker, config);

        Assert.Equal("MON-A", vm.MonitorId);
        Assert.Equal(2, vm.Workspaces.Count);

        var ws1 = vm.Workspaces.First(w => w.WorkspaceId == "MON-A:1");
        Assert.True(ws1.IsActive);
        Assert.Single(ws1.Windows);
        Assert.Equal("Window A1", ws1.Windows[0].Title);

        var ws2 = vm.Workspaces.First(w => w.WorkspaceId == "MON-A:2");
        Assert.False(ws2.IsActive);
        Assert.Single(ws2.Windows);
        Assert.Equal("Window A2", ws2.Windows[0].Title);
    }

    [Fact]
    public void MoveWindowToWorkspace_UpdatesViewModelCollectionsAndTriggersAssignWindow()
    {
        var wm = new FakeWindowManager();
        var monitors = new FakeMonitorManager();
        var events = new FakeWindowEventSource();
        var guard = new OperationGuard();
        var tracker = new WindowTracker(wm, events, monitors, guard);
        var manager = new WorkspaceManager(wm, tracker, guard);

        monitors.Monitors.Add(MonA);

        var hwnd = (nint)201;
        wm.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd, ProcessId = 20, ProcessPath = "test.exe", WindowClass = "TestClass", Title = "Test Window",
            MonitorId = "MON-A", WorkspaceId = "MON-A:1", IsVisible = true, NormalBounds = new Rectangle(0, 0, 100, 100), LastUpdated = DateTimeOffset.UtcNow
        };

        tracker.Rescan();

        var config = AppConfiguration.CreateDefault(new[] { MonA });
        manager.RenameWorkspace("MON-A:1", "Space 1");
        manager.RenameWorkspace("MON-A:2", "Space 2");
        manager.SwitchWorkspace("MON-A", "MON-A:1");

        var vm = new OverviewViewModel("MON-A", manager, tracker, config);

        // Move from MON-A:1 to MON-A:2
        vm.MoveWindowToWorkspace(hwnd, "MON-A:2");

        var ws1 = vm.Workspaces.First(w => w.WorkspaceId == "MON-A:1");
        Assert.Empty(ws1.Windows);

        var ws2 = vm.Workspaces.First(w => w.WorkspaceId == "MON-A:2");
        Assert.Single(ws2.Windows);
        Assert.Equal("Test Window", ws2.Windows[0].Title);

        // Verify live window state was reassigned
        Assert.Equal("MON-A:2", tracker.TrackedWindows[hwnd].WorkspaceId);
    }
}
