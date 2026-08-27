using WindowsSpaces.Core;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class MonitorNamingTests
{
    [Fact]
    public void ToDisplayName_StripsTheDeviceNamespacePrefix()
    {
        Assert.Equal("DISPLAY1", MonitorNaming.ToDisplayName(@"\\.\DISPLAY1"));
    }

    [Fact]
    public void ToDisplayName_LeavesAnIdWithoutThePrefixAlone()
    {
        Assert.Equal("MON-1", MonitorNaming.ToDisplayName("MON-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToDisplayName_IsEmptyForAMissingId(string? monitorId)
    {
        Assert.Equal(string.Empty, MonitorNaming.ToDisplayName(monitorId));
    }

    [Fact]
    public void ToDisplayName_FallsBackToTheIdWhenStrippingWouldLeaveNothing()
    {
        // Not a real device name, but display code must never end up with a
        // blank label it cannot tell one monitor from another by.
        Assert.Equal(@"\\.\", MonitorNaming.ToDisplayName(@"\\.\"));
    }
}
