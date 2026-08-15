using Microsoft.UI.Xaml;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.Views;

public sealed partial class RulesWindow : Window
{
    private readonly RulesViewModel _viewModel;
    private readonly ApplyConfigurationCallback _onSaved;

    public RulesWindow(Func<AppConfiguration> getConfig, ApplyConfigurationCallback onSaved)
    {
        InitializeComponent();
        _viewModel = new RulesViewModel(getConfig());
        _onSaved = onSaved;
        RulesList.ItemsSource = _viewModel.Rules;
    }

    private void OnAddRuleClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.AddRule("Active");
        RulesList.ItemsSource = null;
        RulesList.ItemsSource = _viewModel.Rules;
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
