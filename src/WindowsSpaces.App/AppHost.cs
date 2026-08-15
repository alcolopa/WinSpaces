using WindowsSpaces.Core;
using WindowsSpaces.Persistence;
using WindowsSpaces.Platform.Win32;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.App;

/// <summary>
/// Composition root: wires Platform implementations into Core, loads the
/// persisted (or default) AppConfiguration, sets up workspaces per monitor,
/// registers configured hotkeys, and starts tracking.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly MonitorApi _monitorApi = new();
    private readonly WindowApi _windowApi = new();
    private readonly WinEventHook _eventSource = new();
    private readonly OperationGuard _guard = new();
    private readonly WindowTracker _tracker;
    private readonly WorkspaceManager _workspaceManager;
    private readonly IConfigurationStore _configStore;
    private HotkeyManager? _hotkeys;
    private TrayIcon? _trayIcon;
    private AppConfiguration _config = null!;
    private nint _messageWindowHwnd;

    public AppHost() : this(new JsonConfigurationStore(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsSpaces", "config.json")))
    {
    }

    public AppHost(IConfigurationStore configStore)
    {
        _configStore = configStore;
        _tracker = new WindowTracker(_windowApi, _eventSource, _monitorApi, _guard);
        _workspaceManager = new WorkspaceManager(_windowApi, _tracker, _guard);
    }

    public void Start(nint messageWindowHwnd)
    {
        _messageWindowHwnd = messageWindowHwnd;
        _tracker.Rescan();
        _eventSource.Start();

        var monitors = _monitorApi.GetMonitors();
        _config = LoadOrDefaultConfiguration(monitors);

        foreach (var monitorConfig in _config.Monitors)
        {
            foreach (var workspace in monitorConfig.Workspaces)
            {
                _workspaceManager.RenameWorkspace(workspace.Id, workspace.Name);
            }
            _workspaceManager.SwitchWorkspace(monitorConfig.MonitorId, monitorConfig.Workspaces[0].Id);
        }

        _hotkeys = new HotkeyManager(messageWindowHwnd);
        RegisterHotkeys(_config.Hotkeys);

        _trayIcon = new TrayIcon(messageWindowHwnd);
        _trayIcon.MenuItemInvoked += OnTrayMenuItemInvoked;
        _trayIcon.Show();
    }

    private void OnTrayMenuItemInvoked(object? sender, TrayMenuCommand command)
    {
        switch (command)
        {
            case TrayMenuCommand.ShowAllWindows:
                ShowAllWindows();
                break;
            case TrayMenuCommand.Exit:
                Environment.Exit(0);
                break;
            case TrayMenuCommand.Settings:
            case TrayMenuCommand.Shortcuts:
            case TrayMenuCommand.Diagnostics:
                // Wired to open the corresponding window in Task 8-10.
                break;
        }
    }

    /// <summary>
    /// Combines saved config with fresh defaults for any monitor missing
    /// from it (new/reconnected monitor never seen before), so a partial
    /// or missing config never leaves a monitor unconfigured.
    /// </summary>
    private AppConfiguration LoadOrDefaultConfiguration(IReadOnlyList<Monitor> monitors)
    {
        var saved = _configStore.Load();
        var defaults = AppConfiguration.CreateDefault(monitors);

        if (saved is null) return defaults;

        var savedMonitorIds = saved.Monitors.Select(m => m.MonitorId).ToHashSet();
        var missingMonitors = defaults.Monitors.Where(m => !savedMonitorIds.Contains(m.MonitorId));

        return saved with { Monitors = saved.Monitors.Concat(missingMonitors).ToList() };
    }

    /// <summary>
    /// Applies a Settings/Shortcuts save: renames workspaces live and
    /// re-registers hotkeys. Adding/removing workspaces for a monitor is
    /// not applied live — the caller's UI must tell the user to restart.
    /// </summary>
    public void ApplyConfiguration(AppConfiguration config)
    {
        _config = config;
        _configStore.Save(config);

        foreach (var monitorConfig in config.Monitors)
        {
            foreach (var workspace in monitorConfig.Workspaces)
            {
                _workspaceManager.RenameWorkspace(workspace.Id, workspace.Name);
            }
        }

        _hotkeys?.Dispose();
        _hotkeys = new HotkeyManager(_messageWindowHwnd);
        RegisterHotkeys(config.Hotkeys);
    }

    public AppConfiguration GetConfiguration() => _config;

    public DiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        var windows = _tracker.TrackedWindows.Values
            .Select(w => new WindowSnapshot(w.Hwnd, w.ProcessId, w.MonitorId, w.WorkspaceId, w.IsVisible, w.IsMinimized, w.IsMaximized))
            .ToList();

        var monitors = _monitorApi.GetMonitors()
            .Select(m => new MonitorSnapshot(m.Id, _workspaceManager.GetActiveWorkspace(m.Id)))
            .ToList();

        return new DiagnosticsSnapshot(windows, monitors);
    }

    private void RegisterHotkeys(IReadOnlyList<HotkeyBinding> bindings)
    {
        var id = 1;
        foreach (var binding in bindings)
        {
            var boundId = id++;
            Action callback = binding.Action switch
            {
                HotkeyAction.SwitchWorkspace => () => SwitchCurrentMonitor(binding.WorkspaceIndex),
                HotkeyAction.MoveToWorkspace => () => MoveActiveWindow(binding.WorkspaceIndex),
                HotkeyAction.ShowAllWindows => ShowAllWindows,
                _ => throw new InvalidOperationException($"Unhandled hotkey action {binding.Action}")
            };
            _hotkeys!.Register(boundId, binding.Modifiers, binding.VirtualKey, callback);
        }
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

    public void HandleTrayMessage(uint message, nint wParam, nint lParam) => _trayIcon?.HandleMessage(message, wParam, lParam);

    public void Dispose()
    {
        _eventSource.Stop();
        _hotkeys?.Dispose();
        _trayIcon?.Dispose();
    }
}
