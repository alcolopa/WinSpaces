using System.Drawing;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class WorkspaceManagerConfigTests
{
    [Fact]
    public void RenameWorkspace_ThenGetWorkspaceNames_ReflectsNewName()
    {
        var windowManager = new FakeWindowManager();
        var monitorManager = new FakeMonitorManager();
        var eventSource = new FakeWindowEventSource();
        var guard = new OperationGuard();
        var tracker = new WindowTracker(windowManager, eventSource, monitorManager, guard);
        var manager = new WorkspaceManager(windowManager, tracker, guard);

        manager.RenameWorkspace("MON-1:1", "Development");

        var names = manager.GetWorkspaceNames("MON-1");
        Assert.Equal("Development", names["MON-1:1"]);
    }

    [Fact]
    public void GetWorkspaceNames_UnknownWorkspace_DefaultsToNotPresent()
    {
        var windowManager = new FakeWindowManager();
        var monitorManager = new FakeMonitorManager();
        var eventSource = new FakeWindowEventSource();
        var guard = new OperationGuard();
        var tracker = new WindowTracker(windowManager, eventSource, monitorManager, guard);
        var manager = new WorkspaceManager(windowManager, tracker, guard);

        var names = manager.GetWorkspaceNames("MON-1");

        Assert.Empty(names);
    }
}
