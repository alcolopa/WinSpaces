using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WindowsSpaces.App.ViewModels;

namespace WindowsSpaces.App.Views;

public sealed partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticsViewModel _viewModel = new();
    private readonly Func<WindowsSpaces.Core.DiagnosticsSnapshot> _getSnapshot;
    private readonly DispatcherQueueTimer _timer;

    public DiagnosticsWindow(Func<WindowsSpaces.Core.DiagnosticsSnapshot> getSnapshot)
    {
        InitializeComponent();
        _getSnapshot = getSnapshot;

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => RefreshSnapshot();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();

        RefreshSnapshot();
    }

    private void RefreshSnapshot()
    {
        _viewModel.UpdateSnapshot(_getSnapshot());
        MonitorsList.ItemsSource = _viewModel.Monitors;
        WindowsList.ItemsSource = _viewModel.Windows;
    }
}
