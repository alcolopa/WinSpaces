using WindowsSpaces.Core;
using WindowsSpaces.Platform.Win32;

namespace WindowsSpaces.App;

/// <summary>
/// Composition root: wires Platform implementations into Core, sets up
/// two workspaces per monitor, registers hotkeys, and starts tracking.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly MonitorApi _monitorApi = new();
    private readonly WindowApi _windowApi = new();
    private readonly WinEventHook _eventSource = new();
    private readonly OperationGuard _guard = new();
    private readonly WindowTracker _tracker;
    private readonly WorkspaceManager _workspaceManager;
    private HotkeyManager? _hotkeys;
    private TrayIcon? _trayIcon;

    public AppHost()
    {
        _tracker = new WindowTracker(_windowApi, _eventSource, _monitorApi, _guard);
        _workspaceManager = new WorkspaceManager(_windowApi, _tracker, _guard);
    }

    public void Start(nint messageWindowHwnd)
    {
        _tracker.Rescan();
        _eventSource.Start();

        foreach (var monitor in _monitorApi.GetMonitors())
        {
            _workspaceManager.SwitchWorkspace(monitor.Id, $"{monitor.Id}:1");
        }

        _hotkeys = new HotkeyManager(messageWindowHwnd);
        RegisterHotkeys();

        _trayIcon = new TrayIcon(messageWindowHwnd);
        _trayIcon.Show();
    }

    private void RegisterHotkeys()
    {
        const int VK_1 = 0x31;
        const int VK_2 = 0x32;

        _hotkeys!.Register(1, ModifierKeys.Control | ModifierKeys.Alt, VK_1, () => SwitchCurrentMonitor(1));
        _hotkeys.Register(2, ModifierKeys.Control | ModifierKeys.Alt, VK_2, () => SwitchCurrentMonitor(2));
        _hotkeys.Register(3, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, VK_1, () => MoveActiveWindow(1));
        _hotkeys.Register(4, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, VK_2, () => MoveActiveWindow(2));
        // Emergency show-all: Ctrl+Alt+Shift+Escape (VK_ESCAPE = 0x1B)
        _hotkeys.Register(5, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x1B, ShowAllWindows);
    }

    private void SwitchCurrentMonitor(int workspaceIndex)
    {
        var foreground = _windowApi.GetForegroundWindow();
        var monitor = _monitorApi.GetMonitorForWindow(foreground);
        if (monitor is null) return;

        _workspaceManager.SwitchWorkspace(monitor.Id, $"{monitor.Id}:{workspaceIndex}");
        _trayIcon?.SetTooltip($"Windows Spaces — {monitor.Id} on space {workspaceIndex}");
    }

    private void MoveActiveWindow(int workspaceIndex)
    {
        var foreground = _windowApi.GetForegroundWindow();
        var monitor = _monitorApi.GetMonitorForWindow(foreground);
        if (monitor is null) return;

        _workspaceManager.AssignWindow(foreground, $"{monitor.Id}:{workspaceIndex}");
    }

    public void ShowAllWindows() => _workspaceManager.ShowAllWindows();

    public void HandleMessage(uint message, nint wParam) => _hotkeys?.HandleMessage(message, wParam);

    public void Dispose()
    {
        _eventSource.Stop();
        _hotkeys?.Dispose();
        _trayIcon?.Dispose();
    }
}
