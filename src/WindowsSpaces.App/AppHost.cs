using WindowsSpaces.App.Views;
using WindowsSpaces.Core;
using WindowsSpaces.Persistence;
using WindowsSpaces.Platform;
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
    private IpcServer? _ipcServer;
    private readonly string _configFilePath;
    private FileSystemWatcher? _configWatcher;
    private readonly List<OverviewWindow> _overviewWindows = new();

    public AppHost() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsSpaces", "config.json"))
    {
    }

    public AppHost(string configFilePath) : this(new JsonConfigurationStore(configFilePath), configFilePath)
    {
    }

    public WorkspaceManager WorkspaceManager => _workspaceManager;
    public WindowTracker WindowTracker => _tracker;
    public AppConfiguration Config => _config;

    public AppHost(IConfigurationStore configStore, string configFilePath)
    {
        _configStore = configStore;
        _configFilePath = configFilePath;
        _tracker = new WindowTracker(_windowApi, _eventSource, _monitorApi, _guard,
            getRules: () => _config?.ActiveRules ?? Array.Empty<ApplicationRule>(),
            getActiveWorkspace: monitorId => _workspaceManager?.GetActiveWorkspace(monitorId),
            tryConsumeRestoration: (string path, string winClass, string title, out WindowProfileState? restoration) =>
            {
                if (_workspaceManager is not null)
                {
                    return _workspaceManager.TryConsumeRestoration(path, winClass, title, out restoration);
                }
                restoration = null;
                return false;
            });
        _workspaceManager = new WorkspaceManager(_windowApi, _tracker, _guard, new ProcessManager());
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
                _workspaceManager.SetWorkspaceDefinition(workspace);
                _workspaceManager.RenameWorkspace(workspace.Id, workspace.Name);
            }
            _workspaceManager.SwitchWorkspace(monitorConfig.MonitorId, monitorConfig.Workspaces[0].Id);
        }

        _hotkeys = new HotkeyManager(messageWindowHwnd);
        RegisterHotkeys(_hotkeys, _config.Hotkeys);

        _trayIcon = new TrayIcon(messageWindowHwnd);
        _trayIcon.MenuItemInvoked += OnTrayMenuItemInvoked;
        _trayIcon.Show();

        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ipcServer = new IpcServer(this, dispatcherQueue);
        _ipcServer.Start();

        InitializeConfigWatcher();
    }

    private void InitializeConfigWatcher()
    {
        var directory = Path.GetDirectoryName(_configFilePath);
        if (string.IsNullOrEmpty(directory)) return;

        Directory.CreateDirectory(directory);
        _configWatcher = new FileSystemWatcher(directory, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _configWatcher.Changed += OnConfigChanged;
    }

    private void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Thread.Sleep(100);

                var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                if (dispatcherQueue is not null)
                {
                    dispatcherQueue.TryEnqueue(() =>
                    {
                        ReloadConfiguration(out _);
                    });
                }
                else
                {
                    ReloadConfiguration(out _);
                }
                return;
            }
            catch (IOException)
            {
                // Locked, wait and retry
            }
        }
    }

    private void OnTrayMenuItemInvoked(object? sender, TrayMenuCommand command)
    {
        switch (command)
        {
            case TrayMenuCommand.ShowAllWindows:
                ShowAllWindows();
                break;
            case TrayMenuCommand.Exit:
                Dispose();
                Environment.Exit(0);
                break;
            case TrayMenuCommand.Settings:
                // GetConfiguration (not a captured snapshot) so a window opened
                // after another one saved starts from the current config rather
                // than clobbering it on save.
                new SettingsWindow(GetConfiguration, ApplyConfiguration).Activate();
                break;
            case TrayMenuCommand.Shortcuts:
                new ShortcutSettingsWindow(GetConfiguration, ApplyConfiguration).Activate();
                break;
            case TrayMenuCommand.Rules:
                new RulesWindow(GetConfiguration, ApplyConfiguration).Activate();
                break;
            case TrayMenuCommand.Profiles:
                new ProfilesWindow(GetConfiguration, ApplyConfiguration, _workspaceManager.GetActiveWorkspaces()).Activate();
                break;
            case TrayMenuCommand.Diagnostics:
                new DiagnosticsWindow(GetDiagnosticsSnapshot).Activate();
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
    ///
    /// Never throws. Returns false with a user-displayable <paramref name="error"/>
    /// when the new hotkeys cannot be registered with the OS (another process
    /// already owns the combination — something AppConfiguration.Validate()
    /// cannot detect, as it only checks intra-app conflicts) or when the
    /// config cannot be persisted. On hotkey failure the PREVIOUS hotkeys are
    /// restored and nothing is persisted, so a failed save leaves the running
    /// app exactly as it was.
    /// </summary>
    public bool ApplyConfiguration(AppConfiguration config, out string? error)
    {
        var previousConfig = _config;

        if (!TryReplaceHotkeys(config.Hotkeys, previousConfig?.Hotkeys, out error))
        {
            return false;
        }

        _config = config;

        foreach (var monitorConfig in config.Monitors)
        {
            foreach (var workspace in monitorConfig.Workspaces)
            {
                _workspaceManager.SetWorkspaceDefinition(workspace);
                _workspaceManager.RenameWorkspace(workspace.Id, workspace.Name);
            }
        }

        if (!string.IsNullOrEmpty(config.ActiveProfileName) && 
            (previousConfig == null || config.ActiveProfileName != previousConfig.ActiveProfileName))
        {
            var profile = config.ActiveProfiles.FirstOrDefault(p => string.Equals(p.Name, config.ActiveProfileName, StringComparison.OrdinalIgnoreCase));
            if (profile != null)
            {
                _workspaceManager.ApplyProfile(profile);
            }
        }

        try
        {
            if (_configWatcher is not null)
            {
                _configWatcher.EnableRaisingEvents = false;
            }

            _configStore.Save(config);

            if (_configWatcher is not null)
            {
                System.Threading.Tasks.Task.Delay(100).ContinueWith(_ =>
                {
                    if (_configWatcher is not null)
                    {
                        _configWatcher.EnableRaisingEvents = true;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            // Save() carries no "never throws" contract (unlike Load()), and an
            // I/O/ACL failure here must not escape into the WinUI click handler.
            // The config is already live for this session; only persistence failed.
            error = $"Settings were applied for this session but could not be saved: {ex.Message}";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Swaps the active hotkey registrations for a new set, rolling back to
    /// <paramref name="previousBindings"/> if any new registration fails.
    /// </summary>
    private bool TryReplaceHotkeys(
        IReadOnlyList<HotkeyBinding> newBindings,
        IReadOnlyList<HotkeyBinding>? previousBindings,
        out string? error)
    {
        // The old registrations must be released first: RegisterHotKey refuses a
        // combination that is already registered, so the new manager could not be
        // built alongside the old one for any binding they have in common.
        _hotkeys?.Dispose();
        _hotkeys = null;

        var candidate = new HotkeyManager(_messageWindowHwnd);
        try
        {
            RegisterHotkeys(candidate, newBindings);
        }
        catch (InvalidOperationException ex)
        {
            candidate.Dispose();

            error = $"Could not register the requested shortcuts (another application may already use one of them): {ex.Message}";

            // Restore the previously working set so the app is never left with
            // no hotkeys at all.
            if (previousBindings is not null)
            {
                var rollback = new HotkeyManager(_messageWindowHwnd);
                try
                {
                    RegisterHotkeys(rollback, previousBindings);
                    _hotkeys = rollback;
                }
                catch (InvalidOperationException rollbackEx)
                {
                    rollback.Dispose();
                    error += $" The previous shortcuts could not be restored either: {rollbackEx.Message}";
                }
            }

            return false;
        }

        _hotkeys = candidate;
        error = null;
        return true;
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

    private void RegisterHotkeys(HotkeyManager manager, IReadOnlyList<HotkeyBinding> bindings)
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
                HotkeyAction.ShowOverview => ToggleOverview,
                _ => throw new InvalidOperationException($"Unhandled hotkey action {binding.Action}")
            };
            manager.Register(boundId, binding.Modifiers, binding.VirtualKey, callback);
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

    private void ToggleOverview()
    {
        if (_overviewWindows.Count > 0)
        {
            foreach (var win in _overviewWindows.ToList())
            {
                try { win.Close(); } catch {}
            }
            _overviewWindows.Clear();
        }
        else
        {
            var monitors = _monitorApi.GetMonitors();
            foreach (var monitor in monitors)
            {
                var win = new OverviewWindow(monitor.Id, _workspaceManager, _tracker, _config, monitor);
                win.Closed += (s, e) =>
                {
                    _overviewWindows.Remove(win);
                };
                win.Activate();
                _overviewWindows.Add(win);
            }
        }
    }

    public void HandleMessage(uint message, nint wParam) => _hotkeys?.HandleMessage(message, wParam);

    public void HandleTrayMessage(uint message, nint wParam, nint lParam) => _trayIcon?.HandleMessage(message, wParam, lParam);

    public bool ReloadConfiguration(out string? error)
    {
        var monitors = _monitorApi.GetMonitors();
        var config = LoadOrDefaultConfiguration(monitors);
        return ApplyConfiguration(config, out error);
    }

    public void Dispose()
    {
        _eventSource.Stop();
        _hotkeys?.Dispose();
        _trayIcon?.Dispose();
        _ipcServer?.Dispose();
        _configWatcher?.Dispose();

        foreach (var win in _overviewWindows.ToList())
        {
            try { win.Close(); } catch {}
        }
        _overviewWindows.Clear();
    }
}
