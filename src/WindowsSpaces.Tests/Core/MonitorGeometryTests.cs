using System.Drawing;
using WindowsSpaces.Core;
using Xunit;

namespace WindowsSpaces.Tests.Core;

/// <summary>
/// Covers where a window lands when it is dragged onto another monitor.
/// </summary>
public class MonitorGeometryTests
{
    [Fact]
    public void MapBetweenMonitors_KeepsOffsetOnSameSizedMonitors()
    {
        var from = new Rectangle(0, 0, 1920, 1080);
        var to = new Rectangle(1920, 0, 1920, 1080);

        var mapped = MonitorGeometry.MapBetweenMonitors(new Rectangle(100, 50, 800, 600), from, to);

        Assert.Equal(new Rectangle(2020, 50, 800, 600), mapped);
    }

    [Fact]
    public void MapBetweenMonitors_ScalesPositionAndSizeToTheTargetResolution()
    {
        var from = new Rectangle(0, 0, 1920, 1080);
        var to = new Rectangle(0, 0, 3840, 2160);

        // The left half of the source monitor stays the left half of the target.
        var mapped = MonitorGeometry.MapBetweenMonitors(new Rectangle(0, 0, 960, 1080), from, to);

        Assert.Equal(new Rectangle(0, 0, 1920, 2160), mapped);
    }

    [Fact]
    public void MapBetweenMonitors_HandlesNegativeMonitorOrigins()
    {
        var from = new Rectangle(0, 0, 1920, 1080);
        var to = new Rectangle(-1920, -200, 1920, 1080);

        var mapped = MonitorGeometry.MapBetweenMonitors(new Rectangle(10, 20, 400, 300), from, to);

        Assert.Equal(new Rectangle(-1910, -180, 400, 300), mapped);
    }

    [Fact]
    public void MapBetweenMonitors_ClampsAWindowThatWouldLandOffTheTargetEdge()
    {
        var from = new Rectangle(0, 0, 1920, 1080);
        var to = new Rectangle(0, 0, 1280, 720);

        // Sitting hard against the source monitor's bottom-right corner.
        var mapped = MonitorGeometry.MapBetweenMonitors(new Rectangle(1420, 780, 500, 300), from, to);

        Assert.True(mapped.Right <= to.Right, $"{mapped} escapes the right edge of {to}");
        Assert.True(mapped.Bottom <= to.Bottom, $"{mapped} escapes the bottom edge of {to}");
        Assert.True(mapped.X >= to.X && mapped.Y >= to.Y);
    }

    [Fact]
    public void MapBetweenMonitors_CapsAWindowLargerThanTheTargetMonitor()
    {
        var from = new Rectangle(0, 0, 3840, 2160);
        var to = new Rectangle(0, 0, 1280, 720);

        var mapped = MonitorGeometry.MapBetweenMonitors(new Rectangle(0, 0, 3840, 2160), from, to);

        Assert.Equal(to, mapped);
    }

    [Fact]
    public void MapBetweenMonitors_LeavesBoundsAloneWhenTheMonitorsAreIdentical()
    {
        var monitor = new Rectangle(0, 0, 1920, 1080);
        var bounds = new Rectangle(30, 40, 500, 400);

        Assert.Equal(bounds, MonitorGeometry.MapBetweenMonitors(bounds, monitor, monitor));
    }

    [Fact]
    public void MapBetweenMonitors_LeavesBoundsAloneWhenAMonitorRectangleIsDegenerate()
    {
        var bounds = new Rectangle(30, 40, 500, 400);

        Assert.Equal(bounds, MonitorGeometry.MapBetweenMonitors(bounds, Rectangle.Empty, new Rectangle(0, 0, 1920, 1080)));
    }
}
