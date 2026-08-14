using WindowsSpaces.Core;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.Core;

public class DomainModelTests
{
    [Fact]
    public void Monitor_WithSameId_AreEqual()
    {
        var a = new Monitor("MON-1", "\\\\.\\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), IsPrimary: true);
        var b = new Monitor("MON-1", "\\\\.\\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), IsPrimary: true);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Workspace_HasMonitorScopedIdentity()
    {
        var ws = new Workspace(Id: "MON-1:1", MonitorId: "MON-1", Name: "Development", Index: 0);

        Assert.Equal("MON-1", ws.MonitorId);
        Assert.Equal("MON-1:1", ws.Id);
    }

    [Fact]
    public void WindowState_TracksAssignmentAndBounds()
    {
        var state = new WindowState
        {
            Hwnd = (nint)12345,
            ProcessId = 999,
            MonitorId = "MON-1",
            WorkspaceId = "MON-1:1",
            IsVisible = true,
            IsMinimized = false,
            IsMaximized = false,
            NormalBounds = new System.Drawing.Rectangle(10, 10, 800, 600),
            LastUpdated = System.DateTimeOffset.UtcNow
        };

        Assert.Equal((nint)12345, state.Hwnd);
        Assert.Equal("MON-1:1", state.WorkspaceId);
        Assert.True(state.IsVisible);
    }
}
