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
        var config = TestConfigurations.WithTwoSpaces(MonA);
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
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);

        vm.RemoveWorkspace("MON-A", "MON-A:2");

        var monitor = vm.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.Single(monitor.Workspaces);
        Assert.Equal("MON-A:1", monitor.Workspaces[0].Id);
    }

    [Fact]
    public void RenameWorkspace_ChangesName()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);

        vm.RenameWorkspace("MON-A", "MON-A:1", "Development");

        var monitor = vm.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.Equal("Development", monitor.Workspaces[0].Name);
    }

    [Fact]
    public void TrySave_ValidState_ReturnsTrueWithUpdatedConfig()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
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
        var config = TestConfigurations.WithTwoSpaces(MonA);
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
        var config = TestConfigurations.WithTwoSpaces(MonA);
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
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);

        Assert.Throws<ArgumentException>(() => vm.AddWorkspace("MON-UNKNOWN"));
    }

    [Fact]
    public void RemoveWorkspace_UnknownMonitorId_ThrowsArgumentException()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);

        Assert.Throws<ArgumentException>(() => vm.RemoveWorkspace("MON-UNKNOWN", "MON-A:1"));
    }

    [Fact]
    public void RenameWorkspace_UnknownMonitorId_ThrowsArgumentException()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);

        Assert.Throws<ArgumentException>(() => vm.RenameWorkspace("MON-UNKNOWN", "MON-A:1", "New Name"));
    }

    [Fact]
    public void RemoveWorkspace_UnknownWorkspaceId_NoOps()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);

        vm.RemoveWorkspace("MON-A", "MON-A:DOES-NOT-EXIST");

        var monitor = vm.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.Equal(2, monitor.Workspaces.Count);
    }

    [Fact]
    public void RenameWorkspace_UnknownWorkspaceId_NoOps()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);

        vm.RenameWorkspace("MON-A", "MON-A:DOES-NOT-EXIST", "New Name");

        var monitor = vm.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.DoesNotContain(monitor.Workspaces, w => w.Name == "New Name");
        Assert.Equal("Space 1", monitor.Workspaces[0].Name);
        Assert.Equal("Space 2", monitor.Workspaces[1].Name);
    }

    [Fact]
    public void Rebind_ChangesHotkeyBinding()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);

        vm.Rebind(HotkeyAction.SwitchWorkspace, workspaceIndex: 1, ModifierKeys.Alt, virtualKey: 0x39);

        var binding = vm.Bindings.Single(b => b.Action == HotkeyAction.SwitchWorkspace && b.WorkspaceIndex == 1);
        Assert.Equal(ModifierKeys.Alt, binding.Modifiers);
        Assert.Equal(0x39, binding.VirtualKey);
    }

    [Fact]
    public void ValidateHotkeys_DetectsConflict()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);

        // collides with the default SwitchWorkspace-2 binding (Ctrl+Alt+2)
        vm.Rebind(HotkeyAction.SwitchWorkspace, 1, ModifierKeys.Control | ModifierKeys.Alt, 0x32);

        var item = vm.HotkeyItems.First(h => h.Action == HotkeyAction.SwitchWorkspace && h.WorkspaceIndex == 1);
        Assert.True(item.HasConflict);
        Assert.NotNull(item.ConflictMessage);
    }

    [Fact]
    public void ResetHotkeysToDefault_RestoresDefaults()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);

        vm.Rebind(HotkeyAction.SwitchWorkspace, 1, ModifierKeys.Alt | ModifierKeys.Shift, 0x41);
        vm.ResetHotkeysToDefault();

        var binding = vm.Bindings.Single(b => b.Action == HotkeyAction.SwitchWorkspace && b.WorkspaceIndex == 1);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, binding.Modifiers);
        Assert.Equal(0x31, binding.VirtualKey);
    }

    [Fact]
    public void AddWorkspace_AddsHotkeysForNewIndex()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);

        vm.AddWorkspace("MON-A");

        Assert.Contains(vm.HotkeyItems, h => h.Action == HotkeyAction.SwitchWorkspace && h.WorkspaceIndex == 3);
        Assert.Contains(vm.HotkeyItems, h => h.Action == HotkeyAction.MoveToWorkspace && h.WorkspaceIndex == 3);
    }

    // ---- Per-space shortcut grouping -------------------------------------
    //
    // The Shortcuts page shows one row per per-space action (Switch/Move to
    // Space), not one per space number — HotkeyItems still holds the full
    // flat list underneath for persistence and conflict-checking.

    [Fact]
    public void DisplayedHotkeyItems_ShowsOneRowPerPerSpaceAction_NotOnePerSpaceNumber()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);

        Assert.Equal(2, vm.HotkeyItems.Count(h => h.Action == HotkeyAction.SwitchWorkspace));
        Assert.Single(vm.DisplayedHotkeyItems, h => h.Action == HotkeyAction.SwitchWorkspace);
        Assert.Equal(2, vm.HotkeyItems.Count(h => h.Action == HotkeyAction.MoveToWorkspace));
        Assert.Single(vm.DisplayedHotkeyItems, h => h.Action == HotkeyAction.MoveToWorkspace);
    }

    [Fact]
    public void DisplayedHotkeyItems_RepresentativeIsTheLowestSpaceNumber()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);

        var shown = vm.DisplayedHotkeyItems.Single(h => h.Action == HotkeyAction.SwitchWorkspace);
        Assert.Equal(1, shown.WorkspaceIndex);
        Assert.True(shown.RepresentsDigitGroup);
    }

    [Fact]
    public void ClosingTheGroupRepresentativeEditor_PropagatesModifiersToEverySpaceNumber()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);
        var representative = vm.DisplayedHotkeyItems.Single(h => h.Action == HotkeyAction.SwitchWorkspace);

        representative.IsEditing = true;
        representative.Modifiers = ModifierKeys.Alt | ModifierKeys.Shift;
        representative.IsEditing = false;

        var other = vm.HotkeyItems.Single(h => h.Action == HotkeyAction.SwitchWorkspace && h.WorkspaceIndex == 2);
        Assert.Equal(ModifierKeys.Alt | ModifierKeys.Shift, other.Modifiers);
        // Each space keeps its own digit key — only the combo is shared.
        Assert.Equal(0x32, other.VirtualKey);
    }

    [Fact]
    public void AddWorkspace_NewSpaceNumberInheritsTheGroupsCurrentModifiers()
    {
        var config = TestConfigurations.WithTwoSpaces(MonA);
        var vm = new SettingsViewModel(config);
        var representative = vm.DisplayedHotkeyItems.Single(h => h.Action == HotkeyAction.SwitchWorkspace);
        representative.IsEditing = true;
        representative.Modifiers = ModifierKeys.Alt | ModifierKeys.Shift;
        representative.IsEditing = false;

        vm.AddWorkspace("MON-A");

        var third = vm.HotkeyItems.Single(h => h.Action == HotkeyAction.SwitchWorkspace && h.WorkspaceIndex == 3);
        Assert.Equal(ModifierKeys.Alt | ModifierKeys.Shift, third.Modifiers);
    }

    // ---- Live apply (no Save button) ------------------------------------

    private static int CountChanges(SettingsViewModel vm, Action edit)
    {
        var changes = 0;
        vm.Changed += (_, _) => changes++;
        edit();
        return changes;
    }

    [Fact]
    public void RenamingASpace_ReportsAChange()
    {
        var vm = new SettingsViewModel(TestConfigurations.WithTwoSpaces(MonA));

        var changes = CountChanges(vm, () => vm.RenameWorkspace("MON-A", "MON-A:1", "Development"));

        Assert.True(changes > 0);
    }

    [Fact]
    public void AddingASpace_ReportsAChange()
    {
        var vm = new SettingsViewModel(TestConfigurations.WithTwoSpaces(MonA));

        var changes = CountChanges(vm, () => vm.AddWorkspace("MON-A"));

        Assert.True(changes > 0);
    }

    [Fact]
    public void RemovingASpace_ReportsAChange()
    {
        var vm = new SettingsViewModel(TestConfigurations.WithTwoSpaces(MonA));

        var changes = CountChanges(vm, () => vm.RemoveWorkspace("MON-A", "MON-A:2"));

        Assert.True(changes > 0);
    }

    [Fact]
    public void TogglingTransitions_ReportsAChange()
    {
        var vm = new SettingsViewModel(TestConfigurations.WithTwoSpaces(MonA));

        var changes = CountChanges(vm, () => vm.EnableTransitions = !vm.EnableTransitions);

        Assert.True(changes > 0);
    }

    [Fact]
    public void ASpaceRenamedToItsOwnName_ReportsNothing()
    {
        var vm = new SettingsViewModel(TestConfigurations.WithTwoSpaces(MonA));
        var name = vm.Monitors.Single().Workspaces[0].Name;

        var changes = CountChanges(vm, () => vm.RenameWorkspace("MON-A", "MON-A:1", name));

        Assert.Equal(0, changes);
    }

    [Fact]
    public void AShortcutBeingEdited_ReportsNothingUntilTheEditorCloses()
    {
        // A combination applied mid-edit would be registered system-wide,
        // taking that key away from every other app until the user finished
        // typing the one they actually wanted.
        var vm = new SettingsViewModel(TestConfigurations.WithTwoSpaces(MonA));
        var hotkey = vm.HotkeyItems.First();
        hotkey.IsEditing = true;

        var duringEdit = CountChanges(vm, () =>
        {
            hotkey.Modifiers = ModifierKeys.Alt | ModifierKeys.Shift;
            hotkey.VirtualKey = 0x41;
        });

        Assert.Equal(0, duringEdit);
    }

    [Fact]
    public void ClosingAShortcutEditor_ReportsTheChange()
    {
        var vm = new SettingsViewModel(TestConfigurations.WithTwoSpaces(MonA));
        var hotkey = vm.HotkeyItems.First();
        hotkey.IsEditing = true;
        hotkey.Modifiers = ModifierKeys.Alt | ModifierKeys.Shift;
        hotkey.VirtualKey = 0x41;

        var changes = CountChanges(vm, () => hotkey.IsEditing = false);

        Assert.True(changes > 0);
    }
}
