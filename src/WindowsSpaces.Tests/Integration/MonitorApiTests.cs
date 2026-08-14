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
}
