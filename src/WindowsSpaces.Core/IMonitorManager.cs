namespace WindowsSpaces.Core;

public interface IMonitorManager
{
    IReadOnlyList<Monitor> GetMonitors();
    Monitor? GetMonitorForWindow(nint hwnd);
    event EventHandler? MonitorsChanged;
}
