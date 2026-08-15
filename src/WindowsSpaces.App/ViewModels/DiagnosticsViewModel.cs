using WindowsSpaces.Core;

namespace WindowsSpaces.App.ViewModels;

public sealed class DiagnosticsViewModel
{
    public IReadOnlyList<WindowSnapshot> Windows { get; private set; } = Array.Empty<WindowSnapshot>();
    public IReadOnlyList<MonitorSnapshot> Monitors { get; private set; } = Array.Empty<MonitorSnapshot>();

    public void UpdateSnapshot(DiagnosticsSnapshot snapshot)
    {
        Windows = snapshot.Windows;
        Monitors = snapshot.Monitors;
    }
}
