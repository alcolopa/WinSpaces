using Microsoft.UI.Xaml;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly Action<AppConfiguration> _onSaved;

    public SettingsWindow(AppConfiguration current, Action<AppConfiguration> onSaved)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(current);
        _onSaved = onSaved;
        MonitorsList.ItemsSource = _viewModel.Monitors;
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TrySave(out var updated, out var error))
        {
            _onSaved(updated);
            Close();
        }
        else
        {
            ErrorText.Text = error;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
