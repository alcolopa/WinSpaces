using System.Drawing;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.Core;

public class WindowTrackerTests
{
    private static (WindowTracker tracker, FakeWindowManager wm, FakeWindowEventSource events, FakeMonitorManager monitors, OperationGuard guard) Build()
    {
        var wm = new FakeWindowManager();
        var events = new FakeWindowEventSource();
        var monitors = new FakeMonitorManager();
        var guard = new OperationGuard();
        var tracker = new WindowTracker(wm, events, monitors, guard);
        return (tracker, wm, events, monitors, guard);
    }

    [Fact]
    public void HandleEvent_Created_AddsWindowFromWindowManager()
    {
        var (tracker, wm, events, _, _) = Build();
        var hwnd = (nint)1;
        wm.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd,
            ProcessId = 100,
            IsVisible = true,
            NormalBounds = new Rectangle(0, 0, 100, 100),
            LastUpdated = DateTimeOffset.UtcNow
        };

        events.Raise(new WindowEvent(WindowEventKind.Created, hwnd, DateTimeOffset.UtcNow));

        Assert.True(tracker.TrackedWindows.ContainsKey(hwnd));
    }

    [Fact]
    public void HandleEvent_Destroyed_RemovesWindow()
    {
        var (tracker, wm, events, _, _) = Build();
        var hwnd = (nint)1;
        wm.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd, ProcessId = 100, IsVisible = true,
            NormalBounds = new Rectangle(0, 0, 100, 100), LastUpdated = DateTimeOffset.UtcNow
        };
        events.Raise(new WindowEvent(WindowEventKind.Created, hwnd, DateTimeOffset.UtcNow));

        wm.Windows.Remove(hwnd);
        events.Raise(new WindowEvent(WindowEventKind.Destroyed, hwnd, DateTimeOffset.UtcNow));

        Assert.False(tracker.TrackedWindows.ContainsKey(hwnd));
    }

    [Fact]
    public void Rescan_PopulatesFromWindowManagerSnapshot()
    {
        var (tracker, wm, _, _, _) = Build();
        var hwnd = (nint)7;
        wm.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd, ProcessId = 5, IsVisible = true,
            NormalBounds = new Rectangle(1, 1, 1, 1), LastUpdated = DateTimeOffset.UtcNow
        };

        tracker.Rescan();

        Assert.Single(tracker.TrackedWindows);
    }

    [Fact]
    public void Rescan_AssignsWindowToItsCurrentMonitorsFirstWorkspace()
    {
        var (tracker, wm, _, monitors, _) = Build();
        var hwnd = (nint)7;
        wm.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd, ProcessId = 5, IsVisible = true,
            NormalBounds = new Rectangle(1, 1, 1, 1), LastUpdated = DateTimeOffset.UtcNow
        };
        monitors.Monitors.Add(new Monitor("MON-A", "\\\\.\\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true));
        monitors.WindowToMonitorId[hwnd] = "MON-A";

        tracker.Rescan();

        var state = tracker.TrackedWindows[hwnd];
        Assert.Equal("MON-A", state.MonitorId);
        Assert.Equal("MON-A:1", state.WorkspaceId);
    }

    [Fact]
    public void HandleEvent_ForTrackedWindow_PreservesExistingAssignmentAcrossRefresh()
    {
        var (tracker, wm, events, monitors, _) = Build();
        var hwnd = (nint)7;
        wm.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd, ProcessId = 5, IsVisible = true,
            NormalBounds = new Rectangle(1, 1, 1, 1), LastUpdated = DateTimeOffset.UtcNow
        };
        monitors.Monitors.Add(new Monitor("MON-A", "\\\\.\\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true));
        monitors.WindowToMonitorId[hwnd] = "MON-A";
        tracker.Rescan();

        // Simulate the window's workspace being explicitly reassigned by
        // WorkspaceManager, then a routine WinEvent (e.g. a location
        // change) arriving afterward.
        tracker.TrackedWindows[hwnd].WorkspaceId = "MON-A:2";
        wm.Windows[hwnd].NormalBounds = new Rectangle(9, 9, 9, 9);
        events.Raise(new WindowEvent(WindowEventKind.LocationChanged, hwnd, DateTimeOffset.UtcNow));

        var state = tracker.TrackedWindows[hwnd];
        Assert.Equal("MON-A:2", state.WorkspaceId);
        Assert.Equal(new Rectangle(9, 9, 9, 9), state.NormalBounds);
    }

    [Fact]
    public void HandleEvent_WindowMovedToDifferentMonitor_ReassignsToThatMonitorsFirstWorkspace()
    {
        var (tracker, wm, events, monitors, _) = Build();
        var hwnd = (nint)7;
        wm.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd, ProcessId = 5, IsVisible = true,
            NormalBounds = new Rectangle(1, 1, 1, 1), LastUpdated = DateTimeOffset.UtcNow
        };
        monitors.Monitors.Add(new Monitor("MON-A", "\\\\.\\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true));
        monitors.Monitors.Add(new Monitor("MON-B", "\\\\.\\DISPLAY2", new Rectangle(1920, 0, 1080, 1920), IsPrimary: false));
        monitors.WindowToMonitorId[hwnd] = "MON-A";
        tracker.Rescan();
        tracker.TrackedWindows[hwnd].WorkspaceId = "MON-A:2";

        // User drags the window to Monitor B.
        monitors.WindowToMonitorId[hwnd] = "MON-B";
        events.Raise(new WindowEvent(WindowEventKind.LocationChanged, hwnd, DateTimeOffset.UtcNow));

        var state = tracker.TrackedWindows[hwnd];
        Assert.Equal("MON-B", state.MonitorId);
        Assert.Equal("MON-B:1", state.WorkspaceId);
    }

    [Fact]
    public void HandleEvent_ForSuppressedWindow_RefreshesOnlyVolatileFields_KeepsAssignment()
    {
        var (tracker, wm, events, monitors, guard) = Build();
        var hwnd = (nint)7;
        wm.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd, ProcessId = 5, IsVisible = true,
            NormalBounds = new Rectangle(1, 1, 1, 1), LastUpdated = DateTimeOffset.UtcNow
        };
        monitors.Monitors.Add(new Monitor("MON-A", "\\\\.\\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true));
        monitors.Monitors.Add(new Monitor("MON-B", "\\\\.\\DISPLAY2", new Rectangle(1920, 0, 1080, 1920), IsPrimary: false));
        monitors.WindowToMonitorId[hwnd] = "MON-A";
        tracker.Rescan();
        tracker.TrackedWindows[hwnd].WorkspaceId = "MON-A:2";

        // Our own operation (e.g. WorkspaceManager.Hide) is in flight, and
        // Win32 happens to report the window as being over Monitor B
        // during the move — this must NOT reassign the window.
        monitors.WindowToMonitorId[hwnd] = "MON-B";
        wm.Windows[hwnd].IsVisible = false;
        using (guard.Suppress(hwnd))
        {
            events.Raise(new WindowEvent(WindowEventKind.LocationChanged, hwnd, DateTimeOffset.UtcNow));
        }

        var state = tracker.TrackedWindows[hwnd];
        Assert.Equal("MON-A", state.MonitorId);
        Assert.Equal("MON-A:2", state.WorkspaceId);
        Assert.False(state.IsVisible);
    }
}
