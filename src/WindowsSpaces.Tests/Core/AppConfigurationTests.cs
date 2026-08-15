using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using WindowsSpaces.Core;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.Core;

public class AppConfigurationTests
{
    private static readonly Monitor MonA = new("MON-A", "\\\\.\\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true);
    private static readonly Monitor MonB = new("MON-B", "\\\\.\\DISPLAY2", new Rectangle(1920, 0, 1920, 1080), IsPrimary: false);

    [Fact]
    public void CreateDefault_GivesTwoWorkspacesPerMonitor_NamedSpace1AndSpace2()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA, MonB });

        Assert.Equal(2, config.Monitors.Count);
        var monA = config.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.Equal(new[] { "Space 1", "Space 2" }, monA.Workspaces.Select(w => w.Name));
        Assert.Equal(new[] { "MON-A:1", "MON-A:2" }, monA.Workspaces.Select(w => w.Id));
    }

    [Fact]
    public void CreateDefault_GivesTheFiveExistingHotkeyBindings()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });

        Assert.Equal(5, config.Hotkeys.Count);
        Assert.Contains(config.Hotkeys, h => h.Action == HotkeyAction.SwitchWorkspace && h.WorkspaceIndex == 1
            && h.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && h.VirtualKey == 0x31);
        Assert.Contains(config.Hotkeys, h => h.Action == HotkeyAction.SwitchWorkspace && h.WorkspaceIndex == 2
            && h.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && h.VirtualKey == 0x32);
        Assert.Contains(config.Hotkeys, h => h.Action == HotkeyAction.MoveToWorkspace && h.WorkspaceIndex == 1
            && h.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift) && h.VirtualKey == 0x31);
        Assert.Contains(config.Hotkeys, h => h.Action == HotkeyAction.MoveToWorkspace && h.WorkspaceIndex == 2
            && h.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift) && h.VirtualKey == 0x32);
        Assert.Contains(config.Hotkeys, h => h.Action == HotkeyAction.ShowAllWindows
            && h.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift) && h.VirtualKey == 0x1B);
    }

    [Fact]
    public void Validate_EmptyWorkspaceName_Fails()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with
        {
            Monitors = new[]
            {
                new MonitorWorkspaceConfig("MON-A", new[]
                {
                    new WorkspaceDefinition("MON-A:1", "", 1)
                })
            }
        };

        Assert.False(config.Validate(out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_DuplicateWorkspaceNameOnSameMonitor_Fails()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with
        {
            Monitors = new[]
            {
                new MonitorWorkspaceConfig("MON-A", new[]
                {
                    new WorkspaceDefinition("MON-A:1", "Dup", 1),
                    new WorkspaceDefinition("MON-A:2", "Dup", 2)
                })
            }
        };

        Assert.False(config.Validate(out _));
    }

    [Fact]
    public void Validate_TenWorkspacesOnOneMonitor_Fails()
    {
        var workspaces = Enumerable.Range(1, 10)
            .Select(i => new WorkspaceDefinition($"MON-A:{i}", $"Space {i}", i))
            .ToArray();
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with
        {
            Monitors = new[] { new MonitorWorkspaceConfig("MON-A", workspaces) }
        };

        Assert.False(config.Validate(out _));
    }

    [Fact]
    public void Validate_ZeroWorkspacesOnOneMonitor_Fails()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with
        {
            Monitors = new[] { new MonitorWorkspaceConfig("MON-A", System.Array.Empty<WorkspaceDefinition>()) }
        };

        Assert.False(config.Validate(out _));
    }

    [Fact]
    public void Validate_ConflictingHotkeys_Fails()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with
        {
            Hotkeys = new[]
            {
                new HotkeyBinding(HotkeyAction.SwitchWorkspace, 1, ModifierKeys.Control, 0x31),
                new HotkeyBinding(HotkeyAction.MoveToWorkspace, 1, ModifierKeys.Control, 0x31)
            }
        };

        Assert.False(config.Validate(out _));
    }

    [Fact]
    public void Validate_DefaultConfig_Passes()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA, MonB });

        Assert.True(config.Validate(out var error));
        Assert.Null(error);
    }
}
