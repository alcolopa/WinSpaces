using WindowsSpaces.Core;
using WindowsSpaces.Platform.Win32;
using Xunit;

namespace WindowsSpaces.Tests.Integration;

/// <summary>
/// End-to-end acceptance pass for the Phase 0 spike (parent spec AC-001,
/// AC-002, AC-003, AC-007), run against real Win32 windows and real
/// monitors. Manual/local only: requires WindowsSpaces.TestApp already
/// running and 2+ real monitors attached. Not run in CI.
/// Run with: dotnet test --filter Category=Manual
/// </summary>
[Trait("Category", "Manual")]
public class WorkspaceManagerAcceptanceTests
{
    private static (nint hwnd, string title)[] FindTestAppWindows(WindowApi windowApi)
    {
        var titles = new[] { "SpacesTest-Normal-1", "SpacesTest-Normal-2" };
        var found = new List<(nint, string)>();

        foreach (var hwnd in windowApi.EnumerateTopLevelWindows())
        {
            var length = 256;
            var sb = new System.Text.StringBuilder(length);
            GetWindowTextRaw(hwnd, sb, length);
            var title = sb.ToString();
            if (titles.Contains(title))
            {
                found.Add((hwnd, title));
            }
        }

        return found.ToArray();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetWindowText")]
    private static extern int GetWindowTextRaw(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [Fact]
    public void EndToEnd_IndependentSwitchingAndEmergencyRecovery_AcrossRealMonitorsAndWindows()
    {
        var monitorApi = new MonitorApi();
        var windowApi = new WindowApi();

        var monitors = monitorApi.GetMonitors();
        if (monitors.Count < 2)
        {
            return; // requires 2+ real monitors; skip on single-monitor dev machines
        }

        var testWindows = FindTestAppWindows(windowApi);
        Assert.True(testWindows.Length >= 2,
            "This test requires WindowsSpaces.TestApp to be running with its SpacesTest-Normal-1/2 windows visible.");

        var monitorA = monitors[0];
        var monitorB = monitors[1];

        // Place one test window on each physical monitor.
        var (hwndOnA, _) = testWindows[0];
        var (hwndOnB, _) = testWindows[1];
        windowApi.Move(hwndOnA, new System.Drawing.Rectangle(monitorA.Bounds.X + 50, monitorA.Bounds.Y + 50, 400, 300));
        windowApi.Move(hwndOnB, new System.Drawing.Rectangle(monitorB.Bounds.X + 50, monitorB.Bounds.Y + 50, 400, 300));

        var eventSource = new WinEventHook();
        var tracker = new WindowTracker(windowApi, eventSource);
        tracker.Rescan();

        // Assign monitor/workspace by hand (the real app does this via
        // WindowTracker + monitor-from-window lookups over time; here we
        // set it directly since this test only needs to prove the
        // switching mechanism, not the full assignment pipeline).
        var stateA = tracker.TrackedWindows[hwndOnA];
        stateA.MonitorId = monitorA.Id;
        stateA.WorkspaceId = $"{monitorA.Id}:1";
        var stateB = tracker.TrackedWindows[hwndOnB];
        stateB.MonitorId = monitorB.Id;
        stateB.WorkspaceId = $"{monitorB.Id}:1";

        var workspaceManager = new WorkspaceManager(windowApi, tracker);
        workspaceManager.SwitchWorkspace(monitorA.Id, $"{monitorA.Id}:1");
        workspaceManager.SwitchWorkspace(monitorB.Id, $"{monitorB.Id}:1");

        // AC-001: switching Monitor A to workspace 2 hides its window;
        // Monitor B's window and active workspace are untouched.
        workspaceManager.SwitchWorkspace(monitorA.Id, $"{monitorA.Id}:2");
        Assert.False(windowApi.GetWindowState(hwndOnA)!.IsVisible);
        Assert.True(windowApi.GetWindowState(hwndOnB)!.IsVisible);
        Assert.Equal($"{monitorA.Id}:2", workspaceManager.GetActiveWorkspace(monitorA.Id));
        Assert.Equal($"{monitorB.Id}:1", workspaceManager.GetActiveWorkspace(monitorB.Id));

        // AC-002: switching Monitor B independently does not affect Monitor A.
        workspaceManager.SwitchWorkspace(monitorB.Id, $"{monitorB.Id}:2");
        Assert.False(windowApi.GetWindowState(hwndOnB)!.IsVisible);
        Assert.Equal($"{monitorA.Id}:2", workspaceManager.GetActiveWorkspace(monitorA.Id));

        // AC-003: moving hwndOnA back to workspace 1 and switching there shows it again.
        workspaceManager.AssignWindow(hwndOnA, $"{monitorA.Id}:1");
        workspaceManager.SwitchWorkspace(monitorA.Id, $"{monitorA.Id}:1");
        Assert.True(windowApi.GetWindowState(hwndOnA)!.IsVisible);

        // AC-007: emergency show-all reveals every tracked window regardless of workspace.
        workspaceManager.SwitchWorkspace(monitorA.Id, $"{monitorA.Id}:2"); // hide hwndOnA again
        Assert.False(windowApi.GetWindowState(hwndOnA)!.IsVisible);
        workspaceManager.ShowAllWindows();
        Assert.True(windowApi.GetWindowState(hwndOnA)!.IsVisible);
        Assert.True(windowApi.GetWindowState(hwndOnB)!.IsVisible);
    }
}
