using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.Views;

public sealed partial class ShortcutSettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly ApplyConfigurationCallback _onSaved;

    public ShortcutSettingsWindow(Func<AppConfiguration> getConfig, ApplyConfigurationCallback onSaved)
    {
        InitializeComponent();

        TryEnableMicaBackdrop();
        ConfigureWindow();

        var config = getConfig();
        _viewModel = new SettingsViewModel(config);
        _onSaved = onSaved;
        BindingsList.ItemsSource = _viewModel.HotkeyItems;
    }

    private void TryEnableMicaBackdrop()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Best effort
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
                appWindow.Title = "Keyboard Shortcuts — Windows Spaces";
                appWindow.Resize(new SizeInt32(840, 640));

                var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                if (displayArea != null)
                {
                    var centeredPosition = new PointInt32(
                        displayArea.WorkArea.X + (displayArea.WorkArea.Width - 840) / 2,
                        displayArea.WorkArea.Y + (displayArea.WorkArea.Height - 640) / 2
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

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.TrySave(out var updated, out var error))
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Error";
            StatusInfoBar.Message = error ?? "Failed to validate shortcuts.";
            StatusInfoBar.IsOpen = true;
            return;
        }

        if (!_onSaved(updated, out var applyError))
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Error";
            StatusInfoBar.Message = applyError ?? "Failed to apply shortcuts.";
            StatusInfoBar.IsOpen = true;
            return;
        }

        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
