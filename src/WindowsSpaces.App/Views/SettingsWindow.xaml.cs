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

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly ApplyConfigurationCallback _onSaved;

    /// <summary>
    /// Takes the config as a factory rather than a snapshot so the editor is
    /// seeded from the configuration current at window-open time (e.g. after
    /// another settings window already saved), not from a stale capture.
    /// </summary>
    public SettingsWindow(Func<AppConfiguration> getConfig, ApplyConfigurationCallback onSaved)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(getConfig());
        _onSaved = onSaved;
        MonitorsList.ItemsSource = _viewModel.Monitors;
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
