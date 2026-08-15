namespace WindowsSpaces.Core;

/// <summary>
/// One configured hotkey. WorkspaceIndex is 1-based and only meaningful for
/// SwitchWorkspace/MoveToWorkspace (must be 0 for ShowAllWindows).
/// </summary>
public sealed record HotkeyBinding(
    HotkeyAction Action,
    int WorkspaceIndex,
    ModifierKeys Modifiers,
    int VirtualKey)
{
    public bool ConflictsWith(HotkeyBinding other) =>
        Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;
}
