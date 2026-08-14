using System.Drawing;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class WorkspaceManagerTests
{
    private static (WorkspaceManager mgr, FakeWindowManager wm, WindowTracker tracker) Build(
        params (nint hwnd, string monitorId, string workspaceId)[] windows)
    {
        var wm = new FakeWindowManager();
        foreach (var (hwnd, monitorId, workspaceId) in windows)
        {
            wm.Windows[hwnd] = new WindowState
            {
                Hwnd = hwnd,
                ProcessId = 1,
                MonitorId = monitorId,
                WorkspaceId = workspaceId,
                IsVisible = true,
                NormalBounds = new Rectangle(0, 0, 100, 100),
                LastUpdated = DateTimeOffset.UtcNow
            };
        }
        var events = new FakeWindowEventSource();
        var tracker = new WindowTracker(wm, events);
        tracker.Rescan();
        var mgr = new WorkspaceManager(wm, tracker);

        // Mirror AppHost.Start()'s bootstrap: each monitor's active workspace
        // starts as whatever workspace its initial windows are already in.
        foreach (var monitorId in windows.Select(w => w.monitorId).Distinct())
        {
            var initialWorkspaceId = windows.First(w => w.monitorId == monitorId).workspaceId;
            mgr.SwitchWorkspace(monitorId, initialWorkspaceId);
        }
        wm.Operations.Clear();

        return (mgr, wm, tracker);
    }

    [Fact]
    public void SwitchWorkspace_OnMonitorA_DoesNotAffectMonitorB()
    {
        var hwndA1 = (nint)1;
        var hwndB1 = (nint)2;
        var (mgr, wm, _) = Build(
            (hwndA1, "MON-A", "MON-A:1"),
            (hwndB1, "MON-B", "MON-B:1"));

        mgr.SwitchWorkspace("MON-A", "MON-A:2");

        Assert.Equal("MON-A:2", mgr.GetActiveWorkspace("MON-A"));
        Assert.Equal("MON-B:1", mgr.GetActiveWorkspace("MON-B"));
        Assert.DoesNotContain(wm.Operations, op => op.Hwnd == hwndB1);
    }

    [Fact]
    public void SwitchWorkspace_HidesWindowsNotInTargetWorkspace_ShowsWindowsInTargetWorkspace()
    {
        var hwndDev = (nint)1;
        var hwndResearch = (nint)2;
        var (mgr, wm, _) = Build(
            (hwndDev, "MON-A", "MON-A:1"),
            (hwndResearch, "MON-A", "MON-A:2"));

        mgr.SwitchWorkspace("MON-A", "MON-A:2");

        Assert.Contains(wm.Operations, op => op.Hwnd == hwndDev && op.Op == "Hide");
        Assert.Contains(wm.Operations, op => op.Hwnd == hwndResearch && op.Op == "Show");
    }

    [Fact]
    public void RapidSwitching_CollapsesToLatestTarget()
    {
        var (mgr, wm, _) = Build();

        mgr.SwitchWorkspace("MON-A", "MON-A:2");
        mgr.SwitchWorkspace("MON-A", "MON-A:3");
        mgr.SwitchWorkspace("MON-A", "MON-A:2");

        Assert.Equal("MON-A:2", mgr.GetActiveWorkspace("MON-A"));
    }

    [Fact]
    public void AssignWindow_MovesWindowToNewWorkspace()
    {
        var hwnd = (nint)1;
        var (mgr, wm, _) = Build((hwnd, "MON-A", "MON-A:1"));

        mgr.AssignWindow(hwnd, "MON-A:2");

        Assert.Equal("MON-A:2", wm.Windows[hwnd].WorkspaceId);
    }

    [Fact]
    public void ShowAllWindows_ShowsEveryTrackedWindow_RegardlessOfWorkspace()
    {
        var hwnd1 = (nint)1;
        var hwnd2 = (nint)2;
        var (mgr, wm, _) = Build(
            (hwnd1, "MON-A", "MON-A:1"),
            (hwnd2, "MON-A", "MON-A:2"));
        mgr.SwitchWorkspace("MON-A", "MON-A:1");
        wm.Operations.Clear();

        mgr.ShowAllWindows();

        Assert.Contains(wm.Operations, op => op.Hwnd == hwnd1 && op.Op == "Show");
        Assert.Contains(wm.Operations, op => op.Hwnd == hwnd2 && op.Op == "Show");
    }

    [Fact]
    public void OperationGuard_SuppressesReentrantEventsDuringTransition()
    {
        var guard = new OperationGuard();
        Assert.False(guard.IsSuppressed((nint)1));

        using (guard.Suppress((nint)1))
        {
            Assert.True(guard.IsSuppressed((nint)1));
        }

        Assert.False(guard.IsSuppressed((nint)1));
    }
}
