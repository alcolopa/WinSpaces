using WindowsSpaces.Platform.Win32;
using Xunit;

namespace WindowsSpaces.Tests.Integration;

/// <summary>
/// Manual/local only: requires a real Windows session. Not run in CI.
/// Launch WindowsSpaces.TestApp before running so there are known windows
/// to enumerate. Run with: dotnet test --filter Category=Manual
/// </summary>
[Trait("Category", "Manual")]
public class WindowApiTests
{
    [Fact]
    public void EnumerateTopLevelWindows_ReturnsAtLeastOneWindow()
    {
        var api = new WindowApi();

        var windows = api.EnumerateTopLevelWindows();

        Assert.NotEmpty(windows);
    }

    [Fact]
    public void HideThenShow_RoundTripsVisibility()
    {
        var api = new WindowApi();
        var hwnd = api.EnumerateTopLevelWindows().First();

        api.Hide(hwnd);
        var hiddenState = api.GetWindowState(hwnd);

        api.Show(hwnd);
        var shownState = api.GetWindowState(hwnd);

        Assert.False(hiddenState!.IsVisible);
        Assert.True(shownState!.IsVisible);
    }
}
