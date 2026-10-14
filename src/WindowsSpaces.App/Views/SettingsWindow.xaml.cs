using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.Views;

/// <summary>
/// Callback the settings windows use to hand a saved configuration back to
/// the host. TrySave-style: returns false with a user-displayable
/// <paramref name="error"/> instead of throwing.
/// </summary>
public delegate bool ApplyConfigurationCallback(AppConfiguration config, out string? error);

/// <summary>
/// Relocates persisted config to a folder (e.g. one synced by OneDrive/
/// Dropbox), or back to the default location when <paramref name="folder"/>
/// is null. TrySave-style: returns false with a user-displayable
/// <paramref name="error"/> instead of throwing.
/// </summary>
public delegate bool ConfigSyncFolderCallback(string? folder, out string? error);

/// <summary>
/// Applies a saved profile by name. TrySave-style: returns false with a
/// user-displayable <paramref name="error"/> instead of throwing.
/// </summary>
public delegate bool ActivateProfileCallback(string name, out string? error);

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly ApplyConfigurationCallback _onSaved;
    private readonly Func<string?> _getSyncFolder;
    private readonly ConfigSyncFolderCallback _setSyncFolder;
    private readonly Func<DiagnosticsSnapshot>? _getDiagnostics;
    // A lookup, not a snapshot: spaces are switched all the time while this
    // window sits open, and a profile saved from a stale snapshot would
    // capture the layout from when the window opened.
    private readonly Func<IReadOnlyDictionary<string, string>>? _getActiveWorkspaces;
    private readonly ActivateProfileCallback? _activateProfile;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _applyTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _savedHintTimer;

    public SettingsWindow(
        Func<AppConfiguration> getConfig,
        ApplyConfigurationCallback onSaved,
        Func<string?> getSyncFolder,
        ConfigSyncFolderCallback setSyncFolder,
        Func<DiagnosticsSnapshot>? getDiagnostics = null,
        Func<IReadOnlyDictionary<string, string>>? getActiveWorkspaces = null,
        ActivateProfileCallback? activateProfile = null)
    {
        try
        {
            InitializeComponent();

            TryEnableMicaBackdrop();
            ConfigureWindow();

            var config = getConfig();
            _viewModel = new SettingsViewModel(config);
            _onSaved = onSaved;
            _getSyncFolder = getSyncFolder;
            _setSyncFolder = setSyncFolder;
            _getDiagnostics = getDiagnostics;
            _getActiveWorkspaces = getActiveWorkspaces;
            _activateProfile = activateProfile;

            MonitorsItemsControl.ItemsSource = _viewModel.MonitorItems;
            ShortcutsItemsControl.ItemsSource = _viewModel.HotkeyItems;
            RulesItemsControl.ItemsSource = _viewModel.RuleItems;
            ProfilesItemsControl.ItemsSource = _viewModel.ProfileItems;

            // Empty pages say what they are for instead of sitting blank.
            void UpdateEmptyStates()
            {
                RulesEmptyText.Visibility = _viewModel.RuleItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ProfilesEmptyText.Visibility = _viewModel.ProfileItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            _viewModel.RuleItems.CollectionChanged += (_, _) => UpdateEmptyStates();
            _viewModel.ProfileItems.CollectionChanged += (_, _) => UpdateEmptyStates();
            UpdateEmptyStates();

            StartWithWindowsToggle.IsOn = Helpers.StartupRegistration.IsEnabled();
            StartWithWindowsToggle.Toggled += (s, e) =>
            {
                var wanted = StartWithWindowsToggle.IsOn;
                if (!Helpers.StartupRegistration.SetEnabled(wanted))
                {
                    // Registry write failed: show the real state, not the wish.
                    StartWithWindowsToggle.IsOn = Helpers.StartupRegistration.IsEnabled();
                }
            };

            EnableTransitionsToggle.IsOn = _viewModel.EnableTransitions;
            EnableTransitionsToggle.Toggled += (s, e) => _viewModel.EnableTransitions = EnableTransitionsToggle.IsOn;

            KeepRenderedToggle.IsOn = _viewModel.KeepWindowsRendered;
            KeepRenderedToggle.Toggled += (s, e) => _viewModel.KeepWindowsRendered = KeepRenderedToggle.IsOn;

            DebugModeToggle.IsOn = _viewModel.DebugMode;
            ApplyDebugMode(_viewModel.DebugMode);
            DebugModeToggle.Toggled += (s, e) =>
            {
                _viewModel.DebugMode = DebugModeToggle.IsOn;
                ApplyDebugMode(DebugModeToggle.IsOn);
            };

            ShowSettingsOnStartupToggle.IsOn = _viewModel.ShowSettingsOnStartup;
            ShowSettingsOnStartupToggle.Toggled += (s, e) => _viewModel.ShowSettingsOnStartup = ShowSettingsOnStartupToggle.IsOn;

            _viewModel.Changed += (_, _) => ScheduleApply();

            RefreshSyncFolderText();
            RefreshDiagnostics();

            // Default to first nav item
            if (MainNavView.MenuItems.Count > 0)
            {
                MainNavView.SelectedItem = MainNavView.MenuItems[0];
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log("SettingsWindow.ctor", ex);
            throw;
        }
    }

    private void TryEnableMicaBackdrop()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Fallback to default theme brush if Mica not supported on current OS build
        }
    }

    private void ConfigureWindow()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                appWindow.Title = "Windows Spaces Settings";
                appWindow.Resize(new SizeInt32(980, 720));

                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    // Taskbar / Alt+Tab icon, and the one drawn in our own title bar.
                    appWindow.SetIcon(iconPath);
                    AppTitleIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
                }

                // Draw our own title bar and let the system caption buttons
                // sit on it, transparent, instead of a separate grey strip.
                ExtendsContentIntoTitleBar = true;
                SetTitleBar(AppTitleBar);

                var titleBar = appWindow.TitleBar;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

                // 32px matches the system caption buttons, so they line up with
                // our title bar instead of hanging from its top edge.
                titleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

                _appWindow = appWindow;
                if (Content is FrameworkElement root)
                {
                    root.Loaded += (_, _) => ApplyCaptionButtonColors();
                    root.ActualThemeChanged += (_, _) => ApplyCaptionButtonColors();
                }

                // Center on current display
                var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                if (displayArea != null)
                {
                    var centeredPosition = new PointInt32(
                        displayArea.WorkArea.X + (displayArea.WorkArea.Width - 980) / 2,
                        displayArea.WorkArea.Y + (displayArea.WorkArea.Height - 720) / 2
                    );
                    appWindow.Move(centeredPosition);
                }
            }
        }
        catch
        {
            // Best effort
        }
    }

    /// <summary>
    /// Folds spaces changed elsewhere into the open window. While an edit of
    /// the user's own is still waiting out its debounce, only additions are
    /// merged: removals and renames would clobber that pending edit.
    /// </summary>
    public void SyncFromHost(AppConfiguration hostConfig) =>
        _viewModel.SyncFromConfiguration(hostConfig, mergeRemovalsAndRenames: _applyTimer?.IsRunning != true);

    /// <summary>Diagnostics exists only in debug mode; switching it off while on that page moves you off it.</summary>
    private void ApplyDebugMode(bool enabled)
    {
        DiagnosticsNavItem.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (!enabled && MainNavView.SelectedItem == DiagnosticsNavItem && MainNavView.MenuItems.Count > 0)
        {
            MainNavView.SelectedItem = MainNavView.MenuItems[0];
        }
    }

    private AppWindow? _appWindow;

    /// <summary>
    /// The default caption glyphs go very faint while the window is inactive,
    /// which on a transparent title bar reads as "disabled". Pin readable
    /// colours for the current theme; hover and pressed states stay the
    /// system's, including the red close button.
    /// </summary>
    private void ApplyCaptionButtonColors()
    {
        if (_appWindow is null) return;

        try
        {
            var dark = (Content as FrameworkElement)?.ActualTheme != ElementTheme.Light;
            var active = dark ? Colors.White : Colors.Black;
            var inactive = dark
                ? ColorHelper.FromArgb(0xFF, 0xA0, 0xA0, 0xA0)
                : ColorHelper.FromArgb(0xFF, 0x70, 0x70, 0x70);

            var titleBar = _appWindow.TitleBar;
            titleBar.ButtonForegroundColor = active;
            titleBar.ButtonInactiveForegroundColor = inactive;
        }
        catch
        {
            // Cosmetic only.
        }
    }

    public void NavigateToSection(string sectionTag)
    {
        foreach (var item in MainNavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(item.Tag as string, sectionTag, StringComparison.OrdinalIgnoreCase))
            {
                if (item.Visibility != Visibility.Visible) break;
                MainNavView.SelectedItem = item;
                break;
            }
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem selectedItem && selectedItem.Tag is string tag)
        {
            WorkspacesSection.Visibility = tag == "Workspaces" ? Visibility.Visible : Visibility.Collapsed;
            ShortcutsSection.Visibility = tag == "Shortcuts" ? Visibility.Visible : Visibility.Collapsed;
            RulesSection.Visibility = tag == "Rules" ? Visibility.Visible : Visibility.Collapsed;
            ProfilesSection.Visibility = tag == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
            SyncSection.Visibility = tag == "Sync" ? Visibility.Visible : Visibility.Collapsed;
            DiagnosticsSection.Visibility = tag == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;

            if (tag == "Diagnostics")
            {
                RefreshDiagnostics();
            }
        }
    }

    private void RefreshSyncFolderText()
    {
        var folder = _getSyncFolder();
        SyncFolderText.Text = folder is null ? "Default Location (%AppData%\\WindowsSpaces\\config.json)" : folder;
        UseDefaultLocationButton.IsEnabled = folder is not null;
    }

    private void RefreshDiagnostics()
    {
        if (_getDiagnostics is null) return;

        try
        {
            var snapshot = _getDiagnostics();
            // Friendly names where the settings know them; the raw ids are
            // device-path artefacts like \\.\DISPLAY1:1.
            string MonitorLabel(string? id) =>
                id is null ? "none" : _viewModel.MonitorItems.FirstOrDefault(m => m.MonitorId == id)?.MonitorDisplayName
                                       ?? MonitorNaming.ToDisplayName(id);

            string SpaceLabel(string? id) =>
                id is null ? "unassigned" : _viewModel.MonitorItems.SelectMany(m => m.Workspaces).FirstOrDefault(w => w.Id == id)?.Name
                                             ?? id;

            DiagMonitorsList.ItemsSource = snapshot.Monitors
                .Select(m => $"{MonitorLabel(m.MonitorId)}: Active Space = {(m.ActiveWorkspaceId is null ? "None" : SpaceLabel(m.ActiveWorkspaceId))}")
                .ToList();
            DiagWindowsList.ItemsSource = snapshot.Windows
                .Select(w => $"HWND: 0x{w.Hwnd:X8} | Space: {SpaceLabel(w.WorkspaceId)} | Monitor: {MonitorLabel(w.MonitorId)} | PID: {w.ProcessId} | Visible: {w.IsVisible}")
                .ToList();
        }
        catch
        {
            // Diagnostics best effort
        }
    }

    private void OnAddWorkspaceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string monitorId })
        {
            _viewModel.AddWorkspace(monitorId);
            StatusInfoBar.IsOpen = false;
        }
    }

    private void OnDeleteWorkspaceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WorkspaceItemViewModel workspaceItem })
        {
            var monitor = _viewModel.MonitorItems.FirstOrDefault(m => m.Workspaces.Contains(workspaceItem));
            if (monitor != null)
            {
                if (monitor.Workspaces.Count <= 1)
                {
                    ShowError("Each monitor must have at least 1 workspace.");
                    return;
                }

                _viewModel.RemoveWorkspace(monitor.MonitorId, workspaceItem.Id);
                StatusInfoBar.IsOpen = false;
            }
        }
    }

    private void OnWorkspaceNameChanged(object sender, TextChangedEventArgs e)
    {
        StatusInfoBar.IsOpen = false;
    }

    private void OnResetShortcutsClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetHotkeysToDefault();
        ShowInfo("Shortcuts have been reset to factory defaults.");
    }

    private void OnAddRuleClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.AddRule();
        ShowInfo("Choose an app for the new rule — it is saved once it has something to match.");
    }

    /// <summary>Lists apps that currently have a window, so a rule can target one without typing its name.</summary>
    private async void OnPickRunningAppClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RuleItemViewModel rule } button) return;

        var apps = await Task.Run(() =>
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var self = Environment.ProcessId;

            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (process.Id == self || process.MainWindowHandle == 0 || string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path)) found.TryAdd(path, process.MainWindowTitle);
                }
                catch (Exception)
                {
                    // Protected or exiting processes refuse module access; they can't be matched by path anyway.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return found.OrderBy(kv => System.IO.Path.GetFileName(kv.Key), StringComparer.OrdinalIgnoreCase).ToList();
        });

        var flyout = new MenuFlyout();
        if (apps.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem { Text = "No running apps found", IsEnabled = false });
        }

        foreach (var (path, title) in apps)
        {
            var item = new MenuFlyoutItem { Text = $"{System.IO.Path.GetFileName(path)}  —  {title}" };
            ToolTipService.SetToolTip(item, path);
            item.Click += (_, _) => ApplyPickedApp(rule, path);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(button);
    }

    private async void OnBrowseForAppClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RuleItemViewModel rule }) return;

        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is not null) ApplyPickedApp(rule, file.Path);
    }

    // Rules match on "process path contains", so the bare file name keeps
    // working when an updater moves the app into a new versioned folder.
    private static void ApplyPickedApp(RuleItemViewModel rule, string path)
    {
        rule.ProcessPath = System.IO.Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(rule.RuleName) || rule.RuleName.StartsWith("App Rule ", StringComparison.Ordinal))
        {
            rule.RuleName = System.IO.Path.GetFileNameWithoutExtension(path);
        }
    }

    private void OnDeleteRuleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string ruleId })
        {
            var rule = _viewModel.RuleItems.FirstOrDefault(r => r.Id == ruleId);
            if (rule != null)
            {
                _viewModel.RuleItems.Remove(rule);
            }
        }
    }

    private void OnSaveCurrentProfileClicked(object sender, RoutedEventArgs e)
    {
        var name = NewProfileNameInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Please enter a name for the profile.");
            return;
        }

        var active = _getActiveWorkspaces?.Invoke() ?? _viewModel.MonitorItems.ToDictionary(
            m => m.MonitorId,
            m => m.Workspaces.FirstOrDefault()?.Id ?? $"{m.MonitorId}:1"
        );

        var existing = _viewModel.ProfileItems.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            _viewModel.ProfileItems.Remove(existing);
        }

        _viewModel.ProfileItems.Add(new WorkspaceProfile(name, active.ToDictionary(kv => kv.Key, kv => kv.Value)));
        NewProfileNameInput.Text = string.Empty;
        ShowInfo($"Saved current layout as profile '{name}'.");
    }

    private void OnActivateProfileClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string profileName }) return;

        if (_activateProfile is null)
        {
            ShowError("Profiles can't be activated from here.");
            return;
        }

        // A profile saved a moment ago may still be inside the debounce window;
        // the host only knows about it once it has been applied.
        _applyTimer?.Stop();
        ApplyNow();

        if (_activateProfile(profileName, out var error))
        {
            // Keeps the next save from writing the previous profile name back,
            // which would re-apply that old profile.
            _viewModel.NoteActiveProfile(profileName);
            ShowInfo($"Activated profile '{profileName}'.");
        }
        else
        {
            ShowError(error ?? $"Could not activate '{profileName}'.");
        }
    }

    private void OnDeleteProfileClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string profileName })
        {
            var profile = _viewModel.ProfileItems.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
            if (profile != null)
            {
                _viewModel.ProfileItems.Remove(profile);
            }
        }
    }

    private async void OnChooseSyncFolderClicked(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        if (!_setSyncFolder(folder.Path, out var error))
        {
            ShowError(error ?? "Failed to set sync folder.");
            return;
        }

        StatusInfoBar.IsOpen = false;
        RefreshSyncFolderText();
    }

    private void OnUseDefaultLocationClicked(object sender, RoutedEventArgs e)
    {
        if (!_setSyncFolder(null, out var error))
        {
            ShowError(error ?? "Failed to restore default sync folder.");
            return;
        }

        StatusInfoBar.IsOpen = false;
        RefreshSyncFolderText();
    }

    /// <summary>
    /// Coalesces a burst of edits into one apply. Typing a space name fires a
    /// change per keystroke, and each apply re-registers hotkeys and rewrites
    /// the config file — work worth doing once the user pauses, not eleven
    /// times on the way to "Development".
    /// </summary>
    private void ScheduleApply()
    {
        if (_applyTimer is null)
        {
            _applyTimer = DispatcherQueue.CreateTimer();
            _applyTimer.IsRepeating = false;
            _applyTimer.Interval = TimeSpan.FromMilliseconds(400);
            _applyTimer.Tick += (_, _) => ApplyNow();
        }

        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private void ApplyNow()
    {
        // An edit part-way to being valid — a name that momentarily duplicates
        // another, a shortcut that collides — is a normal state to pass
        // through while typing, not a failure to shout about. Show it, keep
        // the user's edits, and apply again on the next change.
        if (!_viewModel.TrySave(out var updated, out var error))
        {
            ShowError(error ?? "Failed to validate settings.");
            return;
        }

        if (!_onSaved(updated, out var applyError))
        {
            ShowError(applyError ?? "Failed to apply settings.");
            return;
        }

        ShowSaved();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        // Anything still inside the debounce window would be lost otherwise.
        _applyTimer?.Stop();
        ApplyNow();
        Close();
    }

    /// <summary>
    /// Confirms an applied change and clears any error the previous attempt
    /// left up — a settled state should not keep showing a stale complaint.
    /// </summary>
    private void ShowSaved()
    {
        StatusInfoBar.IsOpen = false;
        SavedHintText.Visibility = Visibility.Visible;

        if (_savedHintTimer is null)
        {
            _savedHintTimer = DispatcherQueue.CreateTimer();
            _savedHintTimer.IsRepeating = false;
            _savedHintTimer.Interval = TimeSpan.FromSeconds(2);
            _savedHintTimer.Tick += (_, _) => SavedHintText.Visibility = Visibility.Collapsed;
        }

        _savedHintTimer.Stop();
        _savedHintTimer.Start();
    }

    private void ShowError(string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.Title = "Error";
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private void ShowInfo(string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = "Notice";
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
