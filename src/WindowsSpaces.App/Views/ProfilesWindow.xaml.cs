using Microsoft.UI.Xaml;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.Views;

public sealed partial class ProfilesWindow : Window
{
    private readonly ProfilesViewModel _viewModel;
    private readonly ApplyConfigurationCallback _onSaved;

    public ProfilesWindow(Func<AppConfiguration> getConfig, ApplyConfigurationCallback onSaved, IReadOnlyDictionary<string, string> currentActiveWorkspaces)
    {
        InitializeComponent();
        _viewModel = new ProfilesViewModel(getConfig(), currentActiveWorkspaces);
        _onSaved = onSaved;
        ProfilesList.ItemsSource = _viewModel.Profiles;
    }

    private void OnSaveCurrentClicked(object sender, RoutedEventArgs e)
    {
        var name = ProfileNameInput.Text;
        if (!string.IsNullOrWhiteSpace(name))
        {
            _viewModel.SaveCurrentAsProfile(name);
            ProfilesList.ItemsSource = null;
            ProfilesList.ItemsSource = _viewModel.Profiles;
            ProfileNameInput.Text = string.Empty;
        }
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
