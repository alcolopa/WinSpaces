using WindowsSpaces.Platform.Win32;
using Xunit;

namespace WindowsSpaces.Tests.Integration;

/// <summary>
/// Manual/local only: requires a real Windows session with attached
/// monitors. Not run in CI. Run with:
/// dotnet test --filter Category=Manual
/// </summary>
[Trait("Category", "Manual")]
public class MonitorApiTests
{
    [Fact]
    public void GetMonitors_ReturnsAtLeastOneMonitorWithNonEmptyId()
    {
        var api = new MonitorApi();

        var monitors = api.GetMonitors();

        Assert.NotEmpty(monitors);
        Assert.All(monitors, m => Assert.False(string.IsNullOrWhiteSpace(m.Id)));
    }

    [Fact]
    public void GetMonitors_OnMultiMonitorSetup_ReturnsDistinctIds()
    {
        var api = new MonitorApi();

        var monitors = api.GetMonitors();
        if (monitors.Count < 2)
        {
            return; // skip on single-monitor dev machines
        }

        var ids = monitors.Select(m => m.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    /// <summary>
    /// Workspace-switch hotkeys target the monitor under the pointer, so this
    /// lookup has to agree with the monitor whose bounds actually contain the
    /// cursor — on every monitor, not just the primary.
    /// </summary>
    [Fact]
    public void GetMonitorUnderCursor_ReturnsTheMonitorWhoseBoundsContainTheCursor()
    {
        var api = new MonitorApi();
        var monitors = api.GetMonitors();
        var originalCursor = GetCursor();

        try
        {
            foreach (var expected in monitors)
            {
                var centre = new System.Drawing.Point(
                    expected.Bounds.Left + expected.Bounds.Width / 2,
                    expected.Bounds.Top + expected.Bounds.Height / 2);

                Assert.True(SetCursorPos(centre.X, centre.Y), "SetCursorPos failed");

                var actual = api.GetMonitorUnderCursor();

                Assert.NotNull(actual);
                Assert.Equal(expected.Id, actual!.Id);
            }
        }
        finally
        {
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private static System.Drawing.Point GetCursor()
    {
        GetCursorPos(out var x, out var y);
        return new System.Drawing.Point(x, y);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    private static void GetCursorPos(out int x, out int y)
    {
        GetCursorPos(out CursorPoint p);
        x = p.X;
        y = p.Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }
}
