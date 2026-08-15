using System;
using System.Drawing;
using System.Linq;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.App;

public class SettingsViewModelTests
{
    private static readonly Monitor MonA = new("MON-A", "\\\\.\\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true);

    [Fact]
    public void AddWorkspace_AppendsWithNextIndexAndDefaultName()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);

        vm.AddWorkspace("MON-A");

        var monitor = vm.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.Equal(3, monitor.Workspaces.Count);
        Assert.Equal("MON-A:3", monitor.Workspaces[2].Id);
        Assert.Equal("Space 3", monitor.Workspaces[2].Name);
    }

    [Fact]
    public void RemoveWorkspace_RemovesIt()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);

        vm.RemoveWorkspace("MON-A", "MON-A:2");

        var monitor = vm.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.Single(monitor.Workspaces);
        Assert.Equal("MON-A:1", monitor.Workspaces[0].Id);
    }

    [Fact]
    public void RenameWorkspace_ChangesName()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);

        vm.RenameWorkspace("MON-A", "MON-A:1", "Development");

        var monitor = vm.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.Equal("Development", monitor.Workspaces[0].Name);
    }

    [Fact]
    public void TrySave_ValidState_ReturnsTrueWithUpdatedConfig()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);
        vm.RenameWorkspace("MON-A", "MON-A:1", "Development");

        var saved = vm.TrySave(out var updated, out var error);

        Assert.True(saved);
        Assert.Null(error);
        Assert.Equal("Development", updated.Monitors.Single().Workspaces[0].Name);
    }

    [Fact]
    public void TrySave_DuplicateNames_ReturnsFalseWithError()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);
        vm.RenameWorkspace("MON-A", "MON-A:1", "Same");
        vm.RenameWorkspace("MON-A", "MON-A:2", "Same");

        var saved = vm.TrySave(out _, out var error);

        Assert.False(saved);
        Assert.NotNull(error);
    }

    [Fact]
    public void RemoveWorkspace_LastOneOnMonitor_TrySaveFails()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);
        vm.RemoveWorkspace("MON-A", "MON-A:1");
        vm.RemoveWorkspace("MON-A", "MON-A:2");

        var saved = vm.TrySave(out _, out var error);

        Assert.False(saved);
        Assert.NotNull(error);
    }

    [Fact]
    public void AddWorkspace_UnknownMonitorId_ThrowsArgumentException()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);

        Assert.Throws<ArgumentException>(() => vm.AddWorkspace("MON-UNKNOWN"));
    }

    [Fact]
    public void RemoveWorkspace_UnknownMonitorId_ThrowsArgumentException()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);

        Assert.Throws<ArgumentException>(() => vm.RemoveWorkspace("MON-UNKNOWN", "MON-A:1"));
    }

    [Fact]
    public void RenameWorkspace_UnknownMonitorId_ThrowsArgumentException()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);

        Assert.Throws<ArgumentException>(() => vm.RenameWorkspace("MON-UNKNOWN", "MON-A:1", "New Name"));
    }

    [Fact]
    public void RemoveWorkspace_UnknownWorkspaceId_NoOps()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);

        vm.RemoveWorkspace("MON-A", "MON-A:DOES-NOT-EXIST");

        var monitor = vm.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.Equal(2, monitor.Workspaces.Count);
    }

    [Fact]
    public void RenameWorkspace_UnknownWorkspaceId_NoOps()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);

        vm.RenameWorkspace("MON-A", "MON-A:DOES-NOT-EXIST", "New Name");

        var monitor = vm.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.DoesNotContain(monitor.Workspaces, w => w.Name == "New Name");
        Assert.Equal("Space 1", monitor.Workspaces[0].Name);
        Assert.Equal("Space 2", monitor.Workspaces[1].Name);
    }
}
