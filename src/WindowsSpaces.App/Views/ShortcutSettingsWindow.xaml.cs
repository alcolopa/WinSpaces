using Microsoft.UI.Xaml;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.Views;

public sealed partial class ShortcutSettingsWindow : Window
{
    private readonly ShortcutSettingsViewModel _viewModel;
    private readonly Action<AppConfiguration> _onSaved;

    public ShortcutSettingsWindow(AppConfiguration current, Action<AppConfiguration> onSaved)
    {
        InitializeComponent();
        _viewModel = new ShortcutSettingsViewModel(current);
        _onSaved = onSaved;
        BindingsList.ItemsSource = _viewModel.Bindings;
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
