using Microsoft.UI.Xaml;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.Views;

/// <summary>
/// Callback the settings windows use to hand a saved configuration back to
/// the host. TrySave-style: returns false with a user-displayable
/// <paramref name="error"/> instead of throwing, so a failure (unregisterable
/// hotkey, unwritable config file) is shown inline rather than crashing the
/// click handler.
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

    /// <summary>
    /// Takes the config as a factory rather than a snapshot so the editor is
    /// seeded from the configuration current at window-open time (e.g. after
    /// another settings window already saved), not from a stale capture.
    /// </summary>
    public SettingsWindow(
        Func<AppConfiguration> getConfig,
        ApplyConfigurationCallback onSaved,
        Func<string?> getSyncFolder,
        ConfigSyncFolderCallback setSyncFolder)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(getConfig());
        _onSaved = onSaved;
        _getSyncFolder = getSyncFolder;
        _setSyncFolder = setSyncFolder;
        MonitorsList.ItemsSource = _viewModel.Monitors;
        RefreshSyncFolderText();
    }

    private void RefreshSyncFolderText()
    {
        var folder = _getSyncFolder();
        SyncFolderText.Text = folder is null ? "Default location (this PC only)" : folder;
        UseDefaultLocationButton.IsEnabled = folder is not null;
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
            ErrorText.Text = error;
            return;
        }

        ErrorText.Text = string.Empty;
        RefreshSyncFolderText();
    }

    private void OnUseDefaultLocationClicked(object sender, RoutedEventArgs e)
    {
        if (!_setSyncFolder(null, out var error))
        {
            ErrorText.Text = error;
            return;
        }

        ErrorText.Text = string.Empty;
        RefreshSyncFolderText();
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TrySave(out var updated, out var error))
        {
            ErrorText.Text = error;
            return;
        }

        if (!_onSaved(updated, out var applyError))
        {
            ErrorText.Text = applyError;
            return;
        }

        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
