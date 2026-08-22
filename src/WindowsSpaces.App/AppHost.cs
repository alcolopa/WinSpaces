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
    private IConfigurationStore _configStore;
    private HotkeyManager? _hotkeys;
    private TrayIcon? _trayIcon;
    private AppConfiguration _config = null!;
    private nint _messageWindowHwnd;
    private IpcServer? _ipcServer;
    private string _configFilePath;
    private FileSystemWatcher? _configWatcher;
    private readonly List<OverviewWindow> _overviewWindows = new();
    private readonly ConfigSyncLocation? _syncLocation;

    public AppHost() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsSpaces", "config.json"))
    {
    }

    /// <summary>
    /// <paramref name="defaultConfigFilePath"/> is where config lives absent
    /// any sync folder override — a local-only pointer file next to it
    /// (see <see cref="ConfigSyncLocation"/>) records the override, if any,
    /// and is consulted here to pick the actual config path.
    /// </summary>
    public AppHost(string defaultConfigFilePath)
    {
        var pointerFilePath = Path.Combine(Path.GetDirectoryName(defaultConfigFilePath) ?? ".", "sync-location.txt");
        _syncLocation = new ConfigSyncLocation(pointerFilePath, defaultConfigFilePath);
        _configFilePath = _syncLocation.ResolveConfigFilePath();
        _configStore = new JsonConfigurationStore(_configFilePath);

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

    public WorkspaceManager WorkspaceManager => _workspaceManager;
    public WindowTracker WindowTracker => _tracker;
    public AppConfiguration Config => _config;

    public AppHost(IConfigurationStore configStore, string configFilePath)
    {
        _configStore = configStore;
        _configFilePath = configFilePath;
        _syncLocation = null;
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

        // Startup must never crash the whole app over a hotkey conflict (with
        // Windows itself or another app) — go through the same rollback-safe
        // path ApplyConfiguration uses rather than a raw RegisterHotkeys call
        // that would throw out of this native Application.Start callback.
        TryReplaceHotkeys(_config.Hotkeys, previousBindings: null, out var hotkeyError);

        _trayIcon = new TrayIcon(messageWindowHwnd);
        _trayIcon.MenuItemInvoked += OnTrayMenuItemInvoked;
        _trayIcon.Show();

        if (hotkeyError is not null)
        {
            _trayIcon.SetTooltip($"Windows Spaces — shortcuts unavailable: {hotkeyError}");
        }

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
                new SettingsWindow(GetConfiguration, ApplyConfiguration, GetConfigSyncFolder, SetConfigSyncFolder).Activate();
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

    /// <summary>
    /// The folder config is currently synced through, or null when using the
    /// default per-machine location.
    /// </summary>
    public string? GetConfigSyncFolder() => _syncLocation?.GetSyncFolder();

    /// <summary>
    /// Relocates persisted configuration to <paramref name="folder"/> (or
    /// back to the default per-machine location when null/empty). Copies the
    /// current in-memory config into the new location first so relocating
    /// never loses local settings, then restarts the file watcher there.
    /// Never throws — mirrors <see cref="ApplyConfiguration"/>'s TrySave-style
    /// contract.
    /// </summary>
    public bool SetConfigSyncFolder(string? folder, out string? error)
    {
        if (_syncLocation is null)
        {
            error = "Configuration sync is not available for this session.";
            return false;
        }

        var normalizedFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        var newPath = normalizedFolder is null
            ? _syncLocation.DefaultConfigFilePath
            : Path.Combine(normalizedFolder, "config.json");

        try
        {
            var newStore = new JsonConfigurationStore(newPath);
            newStore.Save(_config);

            _configWatcher?.Dispose();
            _configWatcher = null;

            _configStore = newStore;
            _configFilePath = newPath;
            _syncLocation.SetSyncFolder(normalizedFolder);

            InitializeConfigWatcher();
        }
        catch (Exception ex)
        {
            error = $"Could not move configuration to the new location: {ex.Message}";
            return false;
        }

        error = null;
        return true;
    }

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

            // Hotkey callbacks run synchronously on the Win32 message-pump
            // thread (see HotkeyManager.HandleMessage) with no other
            // handler above them — an unhandled exception here (e.g. a
            // stale window handle from rapid workspace switching) crashes
            // the whole process. Catch and log so a repro leaves evidence
            // instead of just vanishing, without turning a single bad
            // switch into a dead app.
            var boundBinding = binding;
            Action loggedCallback = () =>
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    CrashLogger.Log($"Hotkey callback threw for action {boundBinding.Action} (workspace index {boundBinding.WorkspaceIndex})", ex);
                }
            };

            manager.Register(boundId, binding.Modifiers, binding.VirtualKey, loggedCallback);
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
