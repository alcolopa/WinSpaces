using System;
using System.Linq;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.App;

public class ShortcutSettingsViewModelTests
{
    private static readonly Monitor MonA = new("MON-A", "\\\\.\\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), IsPrimary: true);

    [Fact]
    public void Rebind_ChangesTheMatchingBinding()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new ShortcutSettingsViewModel(config);

        vm.Rebind(HotkeyAction.SwitchWorkspace, workspaceIndex: 1, ModifierKeys.Control, virtualKey: 0x39);

        var binding = vm.Bindings.Single(b => b.Action == HotkeyAction.SwitchWorkspace && b.WorkspaceIndex == 1);
        Assert.Equal(ModifierKeys.Control, binding.Modifiers);
        Assert.Equal(0x39, binding.VirtualKey);
    }

    [Fact]
    public void TrySave_ConflictingRebind_ReturnsFalseWithError()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new ShortcutSettingsViewModel(config);

        vm.Rebind(HotkeyAction.SwitchWorkspace, 1, ModifierKeys.Control | ModifierKeys.Alt, 0x32);

        var saved = vm.TrySave(out _, out var error);

        Assert.False(saved);
        Assert.NotNull(error);
    }

    [Fact]
    public void TrySave_NonConflictingRebind_ReturnsTrue()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new ShortcutSettingsViewModel(config);

        vm.Rebind(HotkeyAction.SwitchWorkspace, 1, ModifierKeys.Control, 0x39);

        var saved = vm.TrySave(out var updated, out var error);

        Assert.True(saved);
        Assert.Null(error);
        Assert.Contains(updated.Hotkeys, b => b.Action == HotkeyAction.SwitchWorkspace && b.WorkspaceIndex == 1 && b.VirtualKey == 0x39);
    }

    [Fact]
    public void Rebind_UnmatchedActionWorkspaceIndexPair_ThrowsArgumentException()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new ShortcutSettingsViewModel(config);

        Assert.Throws<ArgumentException>(() =>
            vm.Rebind(HotkeyAction.SwitchWorkspace, workspaceIndex: 9, ModifierKeys.Control, virtualKey: 0x39));
    }
}
