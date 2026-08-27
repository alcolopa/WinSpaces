namespace WindowsSpaces.Core;

public interface IMonitorManager
{
    IReadOnlyList<Monitor> GetMonitors();
    Monitor? GetMonitorForWindow(nint hwnd);

    /// <summary>
    /// The monitor the mouse pointer is currently on. This — not the
    /// foreground window's monitor — is what "current monitor" means for a
    /// workspace-switch hotkey: switching hides every window on the target
    /// monitor, so focus jumps somewhere unpredictable straight afterwards and
    /// the *next* press would otherwise land on a different monitor.
    /// </summary>
    Monitor? GetMonitorUnderCursor();
    event EventHandler? MonitorsChanged;
}
