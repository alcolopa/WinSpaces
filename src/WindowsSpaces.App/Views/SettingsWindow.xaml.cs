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

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly ApplyConfigurationCallback _onSaved;
    private readonly Func<string?> _getSyncFolder;
    private readonly ConfigSyncFolderCallback _setSyncFolder;
    private readonly Func<DiagnosticsSnapshot>? _getDiagnostics;
    private readonly IReadOnlyDictionary<string, string>? _activeWorkspaces;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _applyTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _savedHintTimer;

    public SettingsWindow(
        Func<AppConfiguration> getConfig,
        ApplyConfigurationCallback onSaved,
        Func<string?> getSyncFolder,
        ConfigSyncFolderCallback setSyncFolder,
        Func<DiagnosticsSnapshot>? getDiagnostics = null,
        IReadOnlyDictionary<string, string>? activeWorkspaces = null)
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
            _activeWorkspaces = activeWorkspaces;

            MonitorsItemsControl.ItemsSource = _viewModel.MonitorItems;
            ShortcutsItemsControl.ItemsSource = _viewModel.HotkeyItems;
            RulesItemsControl.ItemsSource = _viewModel.RuleItems;
            ProfilesItemsControl.ItemsSource = _viewModel.ProfileItems;

            EnableTransitionsToggle.IsOn = _viewModel.EnableTransitions;
            EnableTransitionsToggle.Toggled += (s, e) => _viewModel.EnableTransitions = EnableTransitionsToggle.IsOn;

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

    public void NavigateToSection(string sectionTag)
    {
        foreach (var item in MainNavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(item.Tag as string, sectionTag, StringComparison.OrdinalIgnoreCase))
            {
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
            DiagMonitorsList.ItemsSource = snapshot.Monitors.Select(m => $"{m.MonitorId}: Active Space = {m.ActiveWorkspaceId ?? "None"}").ToList();
            DiagWindowsList.ItemsSource = snapshot.Windows.Select(w => $"HWND: 0x{w.Hwnd:X8} | Space: {w.WorkspaceId ?? "unassigned"} | Monitor: {w.MonitorId ?? "none"} | PID: {w.ProcessId} | Visible: {w.IsVisible}").ToList();
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
        var firstMonitor = _viewModel.MonitorItems.FirstOrDefault()?.MonitorId ?? "MON-1";
        var id = Guid.NewGuid().ToString("N");
        var nextNumber = _viewModel.RuleItems.Count + 1;
        var rule = new ApplicationRule(
            Id: id,
            RuleName: $"App Rule {nextNumber}",
            ProcessPath: null,
            WindowClass: null,
            WindowTitle: null,
            TargetMonitorId: firstMonitor,
            TargetWorkspaceIndex: 1
        );
        _viewModel.RuleItems.Add(new RuleItemViewModel(rule));
        StatusInfoBar.IsOpen = false;
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

        var active = _activeWorkspaces ?? _viewModel.MonitorItems.ToDictionary(
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
        if (sender is Button { Tag: string profileName })
        {
            ShowInfo($"Profile '{profileName}' will be activated when settings are saved.");
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
