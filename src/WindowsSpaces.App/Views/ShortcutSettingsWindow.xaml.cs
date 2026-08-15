using Microsoft.UI.Xaml;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.Views;

public sealed partial class ShortcutSettingsWindow : Window
{
    private readonly ShortcutSettingsViewModel _viewModel;
    private readonly ApplyConfigurationCallback _onSaved;

    /// <summary>
    /// Takes the config as a factory rather than a snapshot so the editor is
    /// seeded from the configuration current at window-open time (e.g. after
    /// the Settings window already saved), not from a stale capture.
    /// </summary>
    public ShortcutSettingsWindow(Func<AppConfiguration> getConfig, ApplyConfigurationCallback onSaved)
    {
        InitializeComponent();
        _viewModel = new ShortcutSettingsViewModel(getConfig());
        _onSaved = onSaved;
        BindingsList.ItemsSource = _viewModel.Bindings;
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
