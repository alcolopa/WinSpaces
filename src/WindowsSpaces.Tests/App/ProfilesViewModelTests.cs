using System.Collections.Generic;
using System.Linq;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.App;

public class ProfilesViewModelTests
{
    private static readonly Monitor MonA = new("MON-A", "\\\\.\\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), IsPrimary: true);
    private static readonly Monitor MonB = new("MON-B", "\\\\.\\DISPLAY2", new System.Drawing.Rectangle(1920, 0, 1920, 1080), IsPrimary: false);

    [Fact]
    public void SaveCurrentAsProfile_AddsNewProfile()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA, MonB });
        var activeWorkspaces = new Dictionary<string, string>
        {
            { "MON-A", "MON-A:2" },
            { "MON-B", "MON-B:1" }
        };
        var vm = new ProfilesViewModel(config, activeWorkspaces);

        vm.SaveCurrentAsProfile("Work");

        Assert.Single(vm.Profiles);
        var profile = vm.Profiles.First();
        Assert.Equal("Work", profile.Name);
        Assert.Equal("MON-A:2", profile.ActiveWorkspaceByMonitor["MON-A"]);
        Assert.Equal("MON-B:1", profile.ActiveWorkspaceByMonitor["MON-B"]);
    }

    [Fact]
    public void SaveCurrentAsProfile_UpdatesExistingProfile()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA, MonB }) with
        {
            Profiles = new[]
            {
                new WorkspaceProfile("Work", new Dictionary<string, string> { { "MON-A", "MON-A:1" } })
            }
        };
        var activeWorkspaces = new Dictionary<string, string>
        {
            { "MON-A", "MON-A:2" }
        };
        var vm = new ProfilesViewModel(config, activeWorkspaces);

        vm.SaveCurrentAsProfile("work"); // case-insensitive update

        Assert.Single(vm.Profiles);
        var profile = vm.Profiles.First();
        Assert.Equal("Work", profile.Name);
        Assert.Equal("MON-A:2", profile.ActiveWorkspaceByMonitor["MON-A"]);
    }

    [Fact]
    public void DeleteProfile_RemovesProfile()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA, MonB }) with
        {
            Profiles = new[]
            {
                new WorkspaceProfile("Work", new Dictionary<string, string> { { "MON-A", "MON-A:1" } }),
                new WorkspaceProfile("Gaming", new Dictionary<string, string> { { "MON-A", "MON-A:2" } })
            },
            ActiveProfileName = "Work"
        };
        var vm = new ProfilesViewModel(config, new Dictionary<string, string>());

        vm.DeleteProfile("Work");

        Assert.Single(vm.Profiles);
        Assert.Equal("Gaming", vm.Profiles.First().Name);
        Assert.Null(vm.ActiveProfileName); // active profile reset if deleted
    }

    [Fact]
    public void SelectProfile_SetsActiveProfile()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA, MonB }) with
        {
            Profiles = new[]
            {
                new WorkspaceProfile("Work", new Dictionary<string, string> { { "MON-A", "MON-A:1" } })
            }
        };
        var vm = new ProfilesViewModel(config, new Dictionary<string, string>());

        vm.SelectProfile("Work");

        Assert.Equal("Work", vm.ActiveProfileName);
    }

    [Fact]
    public void TrySave_WithValidProfiles_ReturnsTrueAndUpdatedConfig()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA, MonB });
        var activeWorkspaces = new Dictionary<string, string>
        {
            { "MON-A", "MON-A:2" }
        };
        var vm = new ProfilesViewModel(config, activeWorkspaces);
        vm.SaveCurrentAsProfile("Work");
        vm.SelectProfile("Work");

        var success = vm.TrySave(out var updated, out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal("Work", updated.ActiveProfileName);
        Assert.Single(updated.ActiveProfiles);
    }
}
