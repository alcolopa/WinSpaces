using WindowsSpaces.Core;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.Fakes;

public sealed class FakeMonitorManager : IMonitorManager
{
    public List<Monitor> Monitors { get; } = new();
    public Dictionary<nint, string> WindowToMonitorId { get; } = new();

    public event EventHandler? MonitorsChanged;

    public IReadOnlyList<Monitor> GetMonitors() => Monitors;

    public Monitor? GetMonitorForWindow(nint hwnd) =>
        WindowToMonitorId.TryGetValue(hwnd, out var monitorId)
            ? Monitors.FirstOrDefault(m => m.Id == monitorId)
            : null;
}
