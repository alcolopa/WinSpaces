namespace WindowsSpaces.Core;

public enum HotkeyAction
{
    /// <summary>Switch the monitor under the cursor to the Nth space (1-based position in that monitor's list).</summary>
    SwitchWorkspace,

    /// <summary>Move the focused window to the Nth space on its own monitor.</summary>
    MoveToWorkspace,

    ShowAllWindows,
    ShowOverview,

    /// <summary>Cycle the monitor under the cursor to the next space, wrapping past the last.</summary>
    NextWorkspace,

    /// <summary>Cycle the monitor under the cursor to the previous space, wrapping past the first.</summary>
    PreviousWorkspace,

    /// <summary>Move the focused window to the next space on its monitor and follow it there.</summary>
    MoveToNextWorkspace,

    /// <summary>Move the focused window to the previous space on its monitor and follow it there.</summary>
    MoveToPreviousWorkspace,

    /// <summary>Create a new space on the monitor under the cursor and switch to it.</summary>
    CreateWorkspace,

    /// <summary>Delete the active space on the monitor under the cursor, relocating its windows to a neighbour.</summary>
    CloseWorkspace,

    /// <summary>Move the focused window to the next monitor's currently active space and follow it there.</summary>
    MoveToNextMonitor,

    /// <summary>Move the focused window to the previous monitor's currently active space and follow it there.</summary>
    MoveToPreviousMonitor
}
