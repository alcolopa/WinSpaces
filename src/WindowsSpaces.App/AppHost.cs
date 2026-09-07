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
    private readonly SlideTransitionAnimator _animator;
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
        _animator = new SlideTransitionAnimator(_monitorApi, isEnabled: TransitionsEnabled);
        _workspaceManager = new WorkspaceManager(_windowApi, _tracker, _guard, new ProcessManager(), _animator);
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
        _animator = new SlideTransitionAnimator(_monitorApi, isEnabled: TransitionsEnabled);
        _workspaceManager = new WorkspaceManager(_windowApi, _tracker, _guard, new ProcessManager(), _animator);
    }

    public void Start(nint messageWindowHwnd)
    {
        _messageWindowHwnd = messageWindowHwnd;
        _tracker.Rescan();
        _eventSource.Start();

        var monitors = _monitorApi.GetMonitors();
        _config = SyncConfigurationToCurrentState(LoadOrDefaultConfiguration(monitors), monitors);

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

        // Open the settings window on initial startup so the user immediately sees the interface
        OnTrayMenuItemInvoked(this, TrayMenuCommand.Settings);
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    private SettingsWindow? _settingsWindow;
    private bool _openingSettingsWindow;

    private void OpenSettings(string? section = null)
    {
        if (_settingsWindow is null)
        {
            // A single tray click can deliver both a legacy WM_LBUTTONUP and
            // a version-4 NIN_SELECT for the same click, and constructing a
            // SettingsWindow is slow enough (XAML parse, Mica backdrop, view
            // model) that a second, reentrant tray message can arrive before
            // _settingsWindow is assigned below — the null-check alone let
            // two windows get constructed for one click. Flip this flag
            // before construction starts so the reentrant call bails out
            // immediately instead of racing the assignment.
            if (_openingSettingsWindow) return;
            _openingSettingsWindow = true;
            try
            {
                _settingsWindow = new SettingsWindow(
                    GetConfiguration,
                    ApplyConfiguration,
                    GetConfigSyncFolder,
                    SetConfigSyncFolder,
                    GetDiagnosticsSnapshot,
                    _workspaceManager.GetActiveWorkspaces());
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }
            finally
            {
                _openingSettingsWindow = false;
            }
        }

        if (!string.IsNullOrEmpty(section))
        {
            _settingsWindow.NavigateToSection(section);
        }

        _settingsWindow.Activate();
        BringToForeground(_settingsWindow);
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
                Program.RequestExit();
                break;
            case TrayMenuCommand.Settings:
                OpenSettings("Workspaces");
                break;
            case TrayMenuCommand.Shortcuts:
                OpenSettings("Shortcuts");
                break;
            case TrayMenuCommand.Rules:
                OpenSettings("Rules");
                break;
            case TrayMenuCommand.Profiles:
                OpenSettings("Profiles");
                break;
            case TrayMenuCommand.Diagnostics:
                OpenSettings("Diagnostics");
                break;
        }
    }

    private static void BringToForeground(Microsoft.UI.Xaml.Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd != 0)
            {
                SetForegroundWindow(hwnd);
            }
        }
        catch
        {
            // Best effort
        }
    }

    /// <summary>
    /// Combines saved config with fresh defaults for any monitor missing
    /// from it (new/reconnected monitor never seen before), so a partial
    /// or missing config never leaves a monitor unconfigured.
    /// </summary>
    /// <summary>
    /// Reconciles the loaded configuration with what is actually on screen at
    /// startup: every attached monitor collapses to a single space holding
    /// every window currently on it.
    ///
    /// The app shows all windows again as it exits, so at launch there is no
    /// hidden state to restore — what is on screen genuinely is one space per
    /// monitor, and a space list left over from a previous session would only
    /// describe spaces that no longer have anything in them. Monitors that
    /// are not attached right now are left untouched: their entries exist to
    /// preserve identity across docking, and nothing can be said about their
    /// contents while they are away.
    /// </summary>
    private AppConfiguration SyncConfigurationToCurrentState(AppConfiguration config, IReadOnlyList<Monitor> monitors)
    {
        var attached = monitors.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var synced = new List<MonitorWorkspaceConfig>();

        foreach (var monitorConfig in config.Monitors)
        {
            if (!attached.Contains(monitorConfig.MonitorId))
            {
                synced.Add(monitorConfig);
                continue;
            }

            // Keep the first space's identity, name and automation commands —
            // "sync to what's on screen" is about which spaces exist and what
            // is in them, not about discarding what the user named things.
            var space = monitorConfig.Workspaces.Count > 0
                ? monitorConfig.Workspaces[0]
                : new WorkspaceDefinition($"{monitorConfig.MonitorId}:1", "Space 1", 1);

            _workspaceManager.ResetToSingleWorkspace(monitorConfig.MonitorId, space);
            synced.Add(new MonitorWorkspaceConfig(monitorConfig.MonitorId, new[] { space }));
        }

        var result = config with
        {
            Monitors = synced,
            Profiles = PruneProfilesToExistingWorkspaces(config.ActiveProfiles, synced)
        };

        // Persisting is best-effort: the in-memory state is already correct
        // and a read-only or locked config file must not stop the app coming
        // up. The next successful save writes it out anyway.
        try
        {
            _configStore.Save(result);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("Startup workspace sync could not be saved", ex);
        }

        return result;
    }

    /// <summary>
    /// Drops profile entries pointing at spaces the sync has just removed. A
    /// profile that still named a deleted space would fail validation and
    /// block every later save.
    /// </summary>
    private static IReadOnlyList<WorkspaceProfile> PruneProfilesToExistingWorkspaces(
        IReadOnlyList<WorkspaceProfile> profiles,
        IReadOnlyList<MonitorWorkspaceConfig> monitors)
    {
        if (profiles.Count == 0) return profiles;

        var existing = monitors
            .SelectMany(m => m.Workspaces.Select(w => w.Id))
            .ToHashSet(StringComparer.Ordinal);

        return profiles
            .Select(p => p with
            {
                ActiveWorkspaceByMonitor = p.ActiveWorkspaceByMonitor
                    .Where(kv => existing.Contains(kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
            })
            .ToList();
    }

    private AppConfiguration LoadOrDefaultConfiguration(IReadOnlyList<Monitor> monitors)
    {
        var saved = _configStore.Load();
        var defaults = AppConfiguration.CreateDefault(monitors);

        if (saved is null) return defaults;

        saved = RenumberWorkspaceIndexes(saved);

        var savedMonitorIds = saved.Monitors.Select(m => m.MonitorId).ToHashSet();
        var missingMonitors = defaults.Monitors.Where(m => !savedMonitorIds.Contains(m.MonitorId));

        // A hotkey action introduced in a later version (e.g. move-window-to-
        // monitor) is entirely absent from an existing user's saved config —
        // there is nothing there for them to have customized — so it is
        // always safe to backfill the default binding for it. An action the
        // user already has (any binding at all, including one they rebound
        // away from the default key) is left untouched.
        var savedActions = saved.Hotkeys.Select(h => h.Action).ToHashSet();
        var missingHotkeys = defaults.Hotkeys.Where(h => !savedActions.Contains(h.Action));

        return saved with
        {
            Monitors = saved.Monitors.Concat(missingMonitors).ToList(),
            Hotkeys = saved.Hotkeys.Concat(missingHotkeys).ToList()
        };
    }

    /// <summary>
    /// Cleans up a monitor's <see cref="WorkspaceDefinition.Index"/>/Id
    /// sequence back to a contiguous 1..N whenever it has drifted (repeated
    /// add/delete cycles leave the survivors with an ever-growing Index even
    /// though <see cref="WorkspaceManager.AddWorkspace"/> always computes the
    /// next one from the current max — e.g. monitor 2 ending up with spaces
    /// ":19"/":20" while monitor 1 is still ":1"/":2"). Safe to do purely at
    /// load time: hotkeys resolve a space by its position in the list (see
    /// <see cref="GetWorkspaceIdAt"/> in WorkspaceManager), never by the raw
    /// id, so nothing observable changes except the numbers in the config
    /// file and, for a workspace profile snapshot, the ids it references.
    /// </summary>
    private static AppConfiguration RenumberWorkspaceIndexes(AppConfiguration config)
    {
        var idMap = new Dictionary<string, string>();
        var renumberedMonitors = new List<MonitorWorkspaceConfig>();
        var changed = false;

        foreach (var monitor in config.Monitors)
        {
            var ordered = monitor.Workspaces;
            var isContiguous = ordered.Select((w, i) => w.Index == i + 1).All(isCorrect => isCorrect);
            if (isContiguous)
            {
                renumberedMonitors.Add(monitor);
                continue;
            }

            changed = true;
            var newWorkspaces = new List<WorkspaceDefinition>();
            for (var i = 0; i < ordered.Count; i++)
            {
                var old = ordered[i];
                var newId = $"{monitor.MonitorId}:{i + 1}";
                idMap[old.Id] = newId;
                newWorkspaces.Add(old with { Id = newId, Index = i + 1 });
            }
            renumberedMonitors.Add(monitor with { Workspaces = newWorkspaces });
        }

        if (!changed) return config;

        var renumberedProfiles = config.ActiveProfiles.Select(p => p with
        {
            ActiveWorkspaceByMonitor = p.ActiveWorkspaceByMonitor.ToDictionary(
                kv => kv.Key,
                kv => idMap.GetValueOrDefault(kv.Value, kv.Value)),
            Windows = p.Windows?.Select(w => w with
            {
                WorkspaceId = idMap.GetValueOrDefault(w.WorkspaceId, w.WorkspaceId)
            }).ToList()
        }).ToList();

        return config with { Monitors = renumberedMonitors, Profiles = renumberedProfiles };
    }

    /// <summary>
    /// Applies a Settings/Shortcuts save: re-registers hotkeys and applies the
    /// monitors' space lists live, including spaces added or removed since the
    /// last apply. No restart is needed — windows stranded in a deleted space
    /// are relocated to a surviving neighbour by
    /// <see cref="WorkspaceManager.SetMonitorWorkspaces"/>.
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
        error = null;
        var previousConfig = _config;

        // Adding, renaming or deleting a space goes through here, and those
        // changes leave the hotkey set untouched. Tearing every global hotkey
        // down and re-registering it for them is not just wasted work: a
        // transient RegisterHotKey failure would abort the space change with a
        // "could not register the requested shortcuts" error that has nothing
        // to do with what the user asked for.
        var hotkeysUnchanged = previousConfig is not null &&
                               previousConfig.Hotkeys.SequenceEqual(config.Hotkeys);

        if (!hotkeysUnchanged && !TryReplaceHotkeys(config.Hotkeys, previousConfig?.Hotkeys, out error))
        {
            return false;
        }

        _config = config;

        foreach (var monitorConfig in config.Monitors)
        {
            if (monitorConfig.Workspaces.Count == 0) continue;
            _workspaceManager.SetMonitorWorkspaces(monitorConfig.MonitorId, monitorConfig.Workspaces);
        }

        WorkspacesChanged?.Invoke(this, EventArgs.Empty);

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
                HotkeyAction.NextWorkspace => () => SwitchRelative(1),
                HotkeyAction.PreviousWorkspace => () => SwitchRelative(-1),
                HotkeyAction.MoveToNextWorkspace => () => MoveActiveWindowRelative(1),
                HotkeyAction.MoveToPreviousWorkspace => () => MoveActiveWindowRelative(-1),
                HotkeyAction.CreateWorkspace => CreateWorkspaceOnCurrentMonitor,
                HotkeyAction.CloseWorkspace => CloseWorkspaceOnCurrentMonitor,
                HotkeyAction.MoveToNextMonitor => () => MoveActiveWindowToMonitor(1),
                HotkeyAction.MoveToPreviousMonitor => () => MoveActiveWindowToMonitor(-1),
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

    private void SwitchCurrentMonitor(int workspacePosition)
    {
        // "Current monitor" is the one the pointer is on, not the one holding
        // the foreground window. Switching hides every window on the target
        // monitor, so focus lands somewhere arbitrary immediately afterwards —
        // with foreground-based targeting the next press then switches a
        // *different* monitor, which reads as "changing one monitor changed
        // the other one too".
        var monitor = _monitorApi.GetMonitorUnderCursor();
        if (monitor is null) return;

        // Resolved by position in the monitor's list, not by the space's
        // stored index: deleting a space leaves the remaining indexes sparse
        // (delete the 2nd of 3 and indexes 1 and 3 survive), so "switch to
        // space 2" has to mean "the second space" or it targets nothing.
        var targetId = _workspaceManager.GetWorkspaceIdAt(monitor.Id, workspacePosition);
        if (targetId is null) return;

        _workspaceManager.SwitchWorkspace(monitor.Id, targetId);
        ReportActiveWorkspace(monitor.Id);
    }

    private void MoveActiveWindow(int workspacePosition)
    {
        var foreground = _windowApi.GetForegroundWindow();
        var monitor = _monitorApi.GetMonitorForWindow(foreground);
        if (monitor is null) return;

        var targetId = _workspaceManager.GetWorkspaceIdAt(monitor.Id, workspacePosition);
        if (targetId is null) return;

        _workspaceManager.AssignWindow(foreground, targetId);
    }

    private void SwitchRelative(int delta)
    {
        var monitor = _monitorApi.GetMonitorUnderCursor();
        if (monitor is null) return;

        _workspaceManager.SwitchRelative(monitor.Id, delta);
        ReportActiveWorkspace(monitor.Id);
    }

    /// <summary>
    /// Sends the focused window to the neighbouring space and follows it
    /// there. Without following, the window vanishes the instant it is moved
    /// (its new space is not the active one) with nothing on screen to say
    /// where it went.
    /// </summary>
    private void MoveActiveWindowRelative(int delta)
    {
        var foreground = _windowApi.GetForegroundWindow();
        var monitor = _monitorApi.GetMonitorForWindow(foreground);
        if (monitor is null) return;

        var targetId = _workspaceManager.GetRelativeWorkspaceId(monitor.Id, delta);
        if (targetId is null) return;

        _workspaceManager.AssignWindow(foreground, targetId);
        _workspaceManager.SwitchWorkspace(monitor.Id, targetId);
        _workspaceManager.ActivateWindow(foreground);
        ReportActiveWorkspace(monitor.Id);
    }

    /// <summary>
    /// Sends the focused window to the next/previous monitor's currently
    /// active space and follows it there — the cross-monitor counterpart of
    /// <see cref="MoveActiveWindowRelative"/>. Monitors are cycled in
    /// <see cref="IMonitorManager.GetMonitors"/> order, wrapping past either
    /// end; a single-monitor system has nothing to move to.
    /// </summary>
    private void MoveActiveWindowToMonitor(int delta)
    {
        var foreground = _windowApi.GetForegroundWindow();
        var currentMonitor = _monitorApi.GetMonitorForWindow(foreground);
        if (currentMonitor is null) return;

        var monitors = _monitorApi.GetMonitors();
        if (monitors.Count < 2) return;

        var currentIndex = monitors.ToList().FindIndex(m => m.Id == currentMonitor.Id);
        if (currentIndex < 0) return;

        var targetIndex = ((currentIndex + delta) % monitors.Count + monitors.Count) % monitors.Count;
        var targetMonitor = monitors[targetIndex];
        if (targetMonitor.Id == currentMonitor.Id) return;

        var targetWorkspaceId = _workspaceManager.GetActiveWorkspace(targetMonitor.Id);
        if (targetWorkspaceId is null) return;

        if (!MoveWindowToMonitor(foreground, targetMonitor.Id, targetWorkspaceId, out _)) return;

        _workspaceManager.ActivateWindow(foreground);
        ReportActiveWorkspace(targetMonitor.Id);
    }

    private void CreateWorkspaceOnCurrentMonitor()
    {
        var monitor = _monitorApi.GetMonitorUnderCursor();
        if (monitor is null) return;

        if (AddWorkspace(monitor.Id, out var newWorkspaceId, out var error) && newWorkspaceId is not null)
        {
            _workspaceManager.SwitchWorkspace(monitor.Id, newWorkspaceId);
            ReportActiveWorkspace(monitor.Id);
        }
        else if (error is not null)
        {
            _trayIcon?.SetTooltip($"Windows Spaces — {error}");
        }
    }

    private void CloseWorkspaceOnCurrentMonitor()
    {
        var monitor = _monitorApi.GetMonitorUnderCursor();
        if (monitor is null) return;

        var active = _workspaceManager.GetActiveWorkspace(monitor.Id);
        if (active is null) return;

        if (!RemoveWorkspace(monitor.Id, active, out var error) && error is not null)
        {
            _trayIcon?.SetTooltip($"Windows Spaces — {error}");
            return;
        }

        ReportActiveWorkspace(monitor.Id);
    }

    private void ReportActiveWorkspace(string monitorId)
    {
        var workspaces = _workspaceManager.GetWorkspaces(monitorId);
        var activeId = _workspaceManager.GetActiveWorkspace(monitorId);
        var name = workspaces.FirstOrDefault(w => w.Id == activeId)?.Name ?? activeId ?? "?";
        var position = workspaces.ToList().FindIndex(w => w.Id == activeId) + 1;

        _trayIcon?.SetTooltip(position > 0
            ? $"Windows Spaces — {monitorId}: {name} ({position}/{workspaces.Count})"
            : $"Windows Spaces — {monitorId}: {name}");
    }

    /// <summary>Raised after the configured spaces change, so open UI can refresh.</summary>
    public event EventHandler? WorkspacesChanged;

    public IReadOnlyList<Monitor> GetMonitors() => _monitorApi.GetMonitors();

    /// <summary>
    /// Creates a space on the given monitor, applies it live and persists it.
    /// The new space's Index is one past the highest ever used on that
    /// monitor, so ids stay unique even after deletions.
    /// </summary>
    public bool AddWorkspace(string monitorId, out string? newWorkspaceId, out string? error)
    {
        newWorkspaceId = null;

        var monitorConfig = _config.Monitors.FirstOrDefault(m => m.MonitorId == monitorId);
        var existing = monitorConfig?.Workspaces ?? Array.Empty<WorkspaceDefinition>();

        if (existing.Count >= AppConfiguration.MaxWorkspacesPerMonitor)
        {
            error = $"A monitor can have at most {AppConfiguration.MaxWorkspacesPerMonitor} spaces.";
            return false;
        }

        var nextIndex = existing.Count == 0 ? 1 : existing.Max(w => w.Index) + 1;
        var candidateId = $"{monitorId}:{nextIndex}";

        // Guard against an index collision from a hand-edited config where
        // Index and the id's suffix have drifted apart.
        while (existing.Any(w => w.Id == candidateId))
        {
            nextIndex++;
            candidateId = $"{monitorId}:{nextIndex}";
        }

        var name = UniqueWorkspaceName(existing, nextIndex);
        var updatedWorkspaces = existing.Append(new WorkspaceDefinition(candidateId, name, nextIndex)).ToList();

        if (!TryUpdateMonitorWorkspaces(monitorId, updatedWorkspaces, out error)) return false;

        newWorkspaceId = candidateId;
        return true;
    }

    /// <summary>
    /// Deletes a space, relocating any windows on it to a neighbouring space.
    /// Refuses to delete a monitor's last space — a monitor with no spaces has
    /// nowhere to show its windows.
    /// </summary>
    public bool RemoveWorkspace(string monitorId, string workspaceId, out string? error)
    {
        var monitorConfig = _config.Monitors.FirstOrDefault(m => m.MonitorId == monitorId);
        if (monitorConfig is null)
        {
            error = $"Monitor '{monitorId}' is not configured.";
            return false;
        }

        if (monitorConfig.Workspaces.Count <= 1)
        {
            error = "A monitor must keep at least one space.";
            return false;
        }

        var updatedWorkspaces = monitorConfig.Workspaces.Where(w => w.Id != workspaceId).ToList();
        if (updatedWorkspaces.Count == monitorConfig.Workspaces.Count)
        {
            error = $"Space '{workspaceId}' does not exist on monitor '{monitorId}'.";
            return false;
        }

        return TryUpdateMonitorWorkspaces(monitorId, updatedWorkspaces, out error);
    }

    /// <summary>Renames a space, applying it live and persisting it.</summary>
    public bool RenameWorkspace(string monitorId, string workspaceId, string newName, out string? error)
    {
        var monitorConfig = _config.Monitors.FirstOrDefault(m => m.MonitorId == monitorId);
        if (monitorConfig is null)
        {
            error = $"Monitor '{monitorId}' is not configured.";
            return false;
        }

        var trimmed = (newName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = "A space name cannot be empty.";
            return false;
        }

        var updatedWorkspaces = monitorConfig.Workspaces
            .Select(w => w.Id == workspaceId ? w with { Name = trimmed } : w)
            .ToList();

        return TryUpdateMonitorWorkspaces(monitorId, updatedWorkspaces, out error);
    }

    /// <summary>
    /// Moves one window to a space on another monitor. This is what dropping
    /// a window tile on a different monitor's overview pane does; the window
    /// keeps its relative position and size on the new screen.
    /// </summary>
    public bool MoveWindowToMonitor(nint hwnd, string targetMonitorId, string targetWorkspaceId, out string? error)
    {
        error = null;

        if (!_tracker.TrackedWindows.TryGetValue(hwnd, out var state) || state.MonitorId is null)
        {
            error = "That window is no longer being tracked.";
            return false;
        }

        if (state.MonitorId == targetMonitorId)
        {
            // Not a cross-monitor move at all — the plain assignment path
            // handles it and does not need monitor geometry.
            _workspaceManager.AssignWindow(hwnd, targetWorkspaceId);
            return true;
        }

        if (!TryGetMonitorBounds(state.MonitorId, targetMonitorId, out var sourceBounds, out var targetBounds, out error))
        {
            return false;
        }

        if (!_workspaceManager.MoveWindowToMonitor(hwnd, targetMonitorId, targetWorkspaceId, sourceBounds, targetBounds))
        {
            error = "That space no longer exists on the target monitor.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Moves a whole space — its name and every window on it — from one
    /// monitor to another. This is what dropping a space card on a different
    /// monitor's overview pane does.
    ///
    /// The order matters and is not an implementation detail: the destination
    /// space is created first, the windows are re-homed onto it, and only then
    /// is the source space deleted. Deleting first would make every window on
    /// it an orphan, and <see cref="WorkspaceManager.SetMonitorWorkspaces"/>
    /// would rescue them onto a neighbouring space of the *source* monitor
    /// before they ever reached the target.
    /// </summary>
    public bool MoveWorkspaceToMonitor(
        string sourceMonitorId,
        string workspaceId,
        string targetMonitorId,
        out string? newWorkspaceId,
        out string? error)
    {
        newWorkspaceId = null;
        error = null;

        if (sourceMonitorId == targetMonitorId)
        {
            error = "That space is already on this monitor.";
            return false;
        }

        var sourceConfig = _config.Monitors.FirstOrDefault(m => m.MonitorId == sourceMonitorId);
        if (sourceConfig is null)
        {
            error = $"Monitor '{sourceMonitorId}' is not configured.";
            return false;
        }

        var moving = sourceConfig.Workspaces.FirstOrDefault(w => w.Id == workspaceId);
        if (moving is null)
        {
            error = $"Space '{workspaceId}' does not exist on monitor '{sourceMonitorId}'.";
            return false;
        }

        if (sourceConfig.Workspaces.Count <= 1)
        {
            error = "A monitor must keep at least one space.";
            return false;
        }

        var targetConfig = _config.Monitors.FirstOrDefault(m => m.MonitorId == targetMonitorId);
        var targetWorkspaces = targetConfig?.Workspaces ?? Array.Empty<WorkspaceDefinition>();

        if (targetWorkspaces.Count >= AppConfiguration.MaxWorkspacesPerMonitor)
        {
            error = $"A monitor can have at most {AppConfiguration.MaxWorkspacesPerMonitor} spaces.";
            return false;
        }

        if (!TryGetMonitorBounds(sourceMonitorId, targetMonitorId, out var sourceBounds, out var targetBounds, out error))
        {
            return false;
        }

        // The space id encodes its monitor, so a moved space is a new space on
        // the target monitor rather than the same id carried across.
        var nextIndex = targetWorkspaces.Count == 0 ? 1 : targetWorkspaces.Max(w => w.Index) + 1;
        var candidateId = $"{targetMonitorId}:{nextIndex}";
        while (targetWorkspaces.Any(w => w.Id == candidateId))
        {
            nextIndex++;
            candidateId = $"{targetMonitorId}:{nextIndex}";
        }

        // Keep the user's name for the space unless the target monitor already
        // has one by that name — Validate() rejects duplicates per monitor.
        var takenNames = targetWorkspaces.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = takenNames.Contains(moving.Name)
            ? UniqueWorkspaceName(targetWorkspaces, nextIndex)
            : moving.Name;

        var arrived = moving with { Id = candidateId, Name = name, Index = nextIndex };

        // Step 1: create the destination space, source monitor untouched.
        if (!TryUpdateMonitorWorkspaces(targetMonitorId, targetWorkspaces.Append(arrived).ToList(), out error))
        {
            return false;
        }

        // Step 2: re-home the windows onto it.
        _workspaceManager.MoveWorkspaceWindowsToMonitor(
            sourceMonitorId, workspaceId, targetMonitorId, candidateId, sourceBounds, targetBounds);

        // Step 3: the source space is now empty, so deleting it relocates
        // nothing. Read the source list back off _config — step 1 replaced it.
        var remaining = _config.Monitors
            .First(m => m.MonitorId == sourceMonitorId)
            .Workspaces
            .Where(w => w.Id != workspaceId)
            .ToList();

        if (!TryUpdateMonitorWorkspaces(sourceMonitorId, remaining, out error))
        {
            // The windows have already arrived, so this is not a rollback
            // point: report it and leave the (now empty) source space in
            // place rather than dragging the windows back.
            newWorkspaceId = candidateId;
            return false;
        }

        newWorkspaceId = candidateId;
        return true;
    }

    private bool TryGetMonitorBounds(
        string sourceMonitorId,
        string targetMonitorId,
        out System.Drawing.Rectangle sourceBounds,
        out System.Drawing.Rectangle targetBounds,
        out string? error)
    {
        sourceBounds = default;
        targetBounds = default;
        error = null;

        var monitors = _monitorApi.GetMonitors();
        var source = monitors.FirstOrDefault(m => m.Id == sourceMonitorId);
        var target = monitors.FirstOrDefault(m => m.Id == targetMonitorId);

        if (source is null || target is null)
        {
            error = "That monitor is no longer connected.";
            return false;
        }

        sourceBounds = source.Bounds;
        targetBounds = target.Bounds;
        return true;
    }

    private static string UniqueWorkspaceName(IReadOnlyList<WorkspaceDefinition> existing, int startingNumber)
    {
        // Validate() rejects duplicate names on a monitor, so a new space
        // whose default name collides with a renamed one has to step past it.
        var taken = existing.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = startingNumber;
        while (taken.Contains($"Space {number}")) number++;
        return $"Space {number}";
    }

    private bool TryUpdateMonitorWorkspaces(string monitorId, IReadOnlyList<WorkspaceDefinition> workspaces, out string? error)
    {
        var monitors = _config.Monitors.Any(m => m.MonitorId == monitorId)
            ? _config.Monitors.Select(m => m.MonitorId == monitorId ? new MonitorWorkspaceConfig(monitorId, workspaces) : m).ToList()
            : _config.Monitors.Append(new MonitorWorkspaceConfig(monitorId, workspaces)).ToList();

        var candidate = _config with { Monitors = monitors };

        if (!candidate.Validate(out error))
        {
            return false;
        }

        return ApplyConfiguration(candidate, out error);
    }

    public void ShowAllWindows() => _workspaceManager.ShowAllWindows();

    private bool _closingOverview;

    /// <summary>
    /// Whether a workspace switch should be animated. Never while the overview
    /// is open: the slide overlay is a topmost window raised over the whole
    /// monitor, so it would paint straight over the overview panes and read as
    /// the overview vanishing. Switches made from the overview take effect
    /// instantly instead.
    /// </summary>
    private bool TransitionsEnabled() =>
        (_config?.EnableTransitions ?? false) && _overviewWindows.Count == 0;

    private void ToggleOverview()
    {
        if (_overviewWindows.Count > 0)
        {
            CloseOverview();
            return;
        }

        // One coordinator per overview session, shared by every pane, so a
        // drag that starts on one monitor can be resolved against another.
        var dragCoordinator = new OverviewDragCoordinator();

        foreach (var monitor in _monitorApi.GetMonitors())
        {
            var win = new OverviewWindow(monitor, this, _workspaceManager, _tracker, dragCoordinator);

            // The overview is one surface spanning every monitor: dismissing
            // it on one monitor has to take down the panes on the others too,
            // or the user is left with topmost windows they can't get rid of.
            win.CloseRequested += (_, _) => CloseOverview();
            win.Closed += (_, _) =>
            {
                _overviewWindows.Remove(win);
                if (!_closingOverview) CloseOverview();
            };

            _overviewWindows.Add(win);
            win.Activate();
        }
    }

    private void CloseOverview()
    {
        if (_closingOverview) return;
        _closingOverview = true;
        try
        {
            foreach (var win in _overviewWindows.ToList())
            {
                try { win.Close(); } catch { }
            }
            _overviewWindows.Clear();
        }
        finally
        {
            _closingOverview = false;
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
        // Windows in inactive spaces are SW_HIDE'd, and nothing but this
        // process ever shows them again — quitting without this leaves the
        // user with windows that are running but permanently invisible, and
        // no app left to recover them.
        // Before ShowAllWindows: a transition still in flight would otherwise
        // hide windows again a moment after they were shown for the last time.
        try { _animator.Dispose(); } catch { }

        try { _workspaceManager.ShowAllWindows(); } catch { }

        _eventSource.Stop();
        _hotkeys?.Dispose();
        _trayIcon?.Dispose();
        _ipcServer?.Dispose();
        _configWatcher?.Dispose();

        try { _settingsWindow?.Close(); } catch {}

        CloseOverview();
    }
}
