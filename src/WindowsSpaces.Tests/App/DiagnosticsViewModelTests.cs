using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;
using Xunit;

namespace WindowsSpaces.Tests.App;

public class DiagnosticsViewModelTests
{
    [Fact]
    public void UpdateSnapshot_ReplacesWindowsAndMonitors()
    {
        var vm = new DiagnosticsViewModel();
        var snapshot = new DiagnosticsSnapshot(
            new[] { new WindowSnapshot((nint)1, 100, "MON-A", "MON-A:1", true, false, false) },
            new[] { new MonitorSnapshot("MON-A", "MON-A:1") });

        vm.UpdateSnapshot(snapshot);

        Assert.Single(vm.Windows);
        Assert.Single(vm.Monitors);
        Assert.Equal("MON-A:1", vm.Windows[0].WorkspaceId);
    }

    [Fact]
    public void InitialState_IsEmpty()
    {
        var vm = new DiagnosticsViewModel();

        Assert.Empty(vm.Windows);
        Assert.Empty(vm.Monitors);
    }
}
