using WindowsSpaces.Core;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class HotkeyBindingTests
{
    [Fact]
    public void ConflictsWith_SameModifiersAndKey_ReturnsTrue()
    {
        var a = new HotkeyBinding(HotkeyAction.SwitchWorkspace, WorkspaceIndex: 1, ModifierKeys.Control | ModifierKeys.Alt, VirtualKey: 0x31);
        var b = new HotkeyBinding(HotkeyAction.MoveToWorkspace, WorkspaceIndex: 2, ModifierKeys.Control | ModifierKeys.Alt, VirtualKey: 0x31);

        Assert.True(a.ConflictsWith(b));
    }

    [Fact]
    public void ConflictsWith_DifferentKey_ReturnsFalse()
    {
        var a = new HotkeyBinding(HotkeyAction.SwitchWorkspace, WorkspaceIndex: 1, ModifierKeys.Control | ModifierKeys.Alt, VirtualKey: 0x31);
        var b = new HotkeyBinding(HotkeyAction.SwitchWorkspace, WorkspaceIndex: 2, ModifierKeys.Control | ModifierKeys.Alt, VirtualKey: 0x32);

        Assert.False(a.ConflictsWith(b));
    }

    [Fact]
    public void ConflictsWith_DifferentModifiers_ReturnsFalse()
    {
        var a = new HotkeyBinding(HotkeyAction.SwitchWorkspace, WorkspaceIndex: 1, ModifierKeys.Control | ModifierKeys.Alt, VirtualKey: 0x31);
        var b = new HotkeyBinding(HotkeyAction.SwitchWorkspace, WorkspaceIndex: 1, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, VirtualKey: 0x31);

        Assert.False(a.ConflictsWith(b));
    }
}
