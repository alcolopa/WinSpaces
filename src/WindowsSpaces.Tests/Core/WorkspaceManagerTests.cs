using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
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
        var monitors = new FakeMonitorManager();
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
        var guard = new OperationGuard();
        var tracker = new WindowTracker(wm, events, monitors, guard);
        tracker.Rescan();
        // Rescan re-derives MonitorId/WorkspaceId from FakeMonitorManager
        // (empty here), which would wipe the explicit assignments above.
        // Restore them directly since this suite is testing WorkspaceManager,
        // not WindowTracker's assignment logic (see WindowTrackerTests for that).
        foreach (var (hwnd, monitorId, workspaceId) in windows)
        {
            tracker.TrackedWindows[hwnd].MonitorId = monitorId;
            tracker.TrackedWindows[hwnd].WorkspaceId = workspaceId;
        }
        var mgr = new WorkspaceManager(wm, tracker, guard);

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
    public void RapidSequentialSwitching_EndsAtLatestTarget()
    {
        var (mgr, wm, _) = Build();

        mgr.SwitchWorkspace("MON-A", "MON-A:2");
        mgr.SwitchWorkspace("MON-A", "MON-A:3");
        mgr.SwitchWorkspace("MON-A", "MON-A:2");

        Assert.Equal("MON-A:2", mgr.GetActiveWorkspace("MON-A"));
    }

    [Fact]
    public void ConcurrentSwitchWorkspace_ForSameMonitor_CollapsesToLatestTarget_WithoutExecutingEveryIntermediateTransition()
    {
        var hwnd = (nint)1;
        var (mgr, wm, _) = Build((hwnd, "MON-A", "MON-A:1"));
        wm.HideGate = new ManualResetEventSlim(false);

        // First switch blocks inside Hide(), simulating an in-flight transition.
        var inFlight = Task.Run(() => mgr.SwitchWorkspace("MON-A", "MON-A:2"));
        Assert.True(wm.HideEntered.Wait(TimeSpan.FromSeconds(2)), "first transition never entered Hide()");

        // Two more requests arrive while the first is still executing.
        // Per "latest request wins", these must NOT each run their own
        // full transition — they should collapse to a single follow-up
        // execution of the last target.
        mgr.SwitchWorkspace("MON-A", "MON-A:3");
        mgr.SwitchWorkspace("MON-A", "MON-A:2");

        wm.HideGate.Set();
#pragma warning disable xUnit1031 // Deliberately blocking: proving the queue collapses concurrent requests requires a real background transition to synchronize with.
        Assert.True(inFlight.Wait(TimeSpan.FromSeconds(2)), "in-flight transition never completed");
#pragma warning restore xUnit1031
        Assert.True(SpinWait.SpinUntil(() => mgr.GetActiveWorkspace("MON-A") == "MON-A:2", TimeSpan.FromSeconds(2)));

        // Without collapsing, three independent executions (:2, :3, :2)
        // would each call Hide(hwnd) again since hwnd's own WorkspaceId
        // never changes ("MON-A:1" the whole time, so it never matches
        // any of these targets) — that would total 3 Hide calls. With
        // collapsing, only the single real execution (to :2) runs; the
        // redundant follow-up execution short-circuits on the
        // already-at-target check before calling Hide again.
        var hideCount = wm.Operations.Count(op => op.Hwnd == hwnd && op.Op == "Hide");
        Assert.Equal(1, hideCount);
        Assert.Equal("MON-A:2", mgr.GetActiveWorkspace("MON-A"));
    }

    [Fact]
    public void AssignWindow_MovesWindowToNewWorkspace()
    {
        var hwnd = (nint)1;
        var (mgr, wm, tracker) = Build((hwnd, "MON-A", "MON-A:1"));

        mgr.AssignWindow(hwnd, "MON-A:2");

        Assert.Equal("MON-A:2", tracker.TrackedWindows[hwnd].WorkspaceId);
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

    [Fact]
    public void ApplyProfile_SwitchesActiveWorkspacesAcrossAllMonitors()
    {
        var hwndA = (nint)1;
        var hwndB = (nint)2;
        var (mgr, wm, _) = Build(
            (hwndA, "MON-A", "MON-A:1"),
            (hwndB, "MON-B", "MON-B:1"));

        var profile = new WorkspaceProfile("TestProfile", new Dictionary<string, string>
        {
            { "MON-A", "MON-A:2" },
            { "MON-B", "MON-B:2" }
        });

        mgr.ApplyProfile(profile);

        Assert.Equal("MON-A:2", mgr.GetActiveWorkspace("MON-A"));
        Assert.Equal("MON-B:2", mgr.GetActiveWorkspace("MON-B"));
    }
}
