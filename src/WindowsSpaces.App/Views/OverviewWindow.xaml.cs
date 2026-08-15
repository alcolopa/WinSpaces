using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.Views;

public sealed partial class OverviewWindow : Window
{
    private readonly OverviewViewModel _viewModel;
    private bool _isDragging;
    private nint _draggedHwnd;
    private Grid? _draggedCard;
    private Point _dragStartPoint;
    private TranslateTransform _transform = new();
    private readonly Dictionary<nint, nint> _thumbnails = new();

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int GWL_STYLE = -16;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly nint HWND_TOPMOST = -1;

    public OverviewWindow(string monitorId, WorkspaceManager workspaceManager, WindowTracker windowTracker, AppConfiguration config, Core.Monitor monitor)
    {
        InitializeComponent();
        
        _viewModel = new OverviewViewModel(monitorId, workspaceManager, windowTracker, config);
        MonitorIdText.Text = monitorId;
        WorkspacesControl.ItemsSource = _viewModel.Workspaces;

        // Apply borderless pop-up style to cover the physical monitor
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, (style & ~WS_CAPTION & ~WS_THICKFRAME) | WS_POPUP);
        SetWindowPos(hwnd, HWND_TOPMOST, monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height, SWP_FRAMECHANGED | SWP_SHOWWINDOW);

        // Register visual tree loaded/closed lifecycle events
        RootGrid.Loaded += (s, e) => RegisterAllVisibleThumbnails();
        this.Closed += (s, e) => UnregisterAllThumbnails();

        // Subscribe to pointer drag-and-drop events
        WorkspacesControl.PointerPressed += OnWorkspacesControlPointerPressed;
        WorkspacesControl.PointerMoved += OnWorkspacesControlPointerMoved;
        WorkspacesControl.PointerReleased += OnWorkspacesControlPointerReleased;

        // Close on Escape key press
        Content.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                Close();
            }
        };
    }

    private void RegisterAllVisibleThumbnails()
    {
        var placeholders = new List<Border>();
        FindChildren(WorkspacesControl, placeholders);

        foreach (var placeholder in placeholders)
        {
            if (placeholder.Tag as string == "ThumbnailPlaceholder" && placeholder.Parent is Grid card)
            {
                var workspaceGrid = FindParent<Grid>(card);
                if (workspaceGrid?.DataContext is WorkspaceOverviewViewModel wsVm && wsVm.IsActive)
                {
                    if (card.DataContext is WindowOverviewViewModel winVm)
                    {
                        RegisterDwmThumbnail(winVm.Hwnd, placeholder);
                    }
                }
            }
        }
    }

    private void RegisterDwmThumbnail(nint hwndSource, Border placeholder)
    {
        if (_thumbnails.ContainsKey(hwndSource)) return;

        var hwndDest = WinRT.Interop.WindowNative.GetWindowHandle(this);
        int result = Platform.Win32.DwmApi.DwmRegisterThumbnail(hwndDest, hwndSource, out nint hThumbnail);
        if (result == 0)
        {
            _thumbnails[hwndSource] = hThumbnail;
            UpdateThumbnailPosition(hThumbnail, placeholder);

            placeholder.SizeChanged += (s, e) =>
            {
                if (_thumbnails.TryGetValue(hwndSource, out var hThumb))
                {
                    UpdateThumbnailPosition(hThumb, placeholder);
                }
            };
        }
    }

    private void UnregisterDwmThumbnail(nint hwndSource)
    {
        if (_thumbnails.Remove(hwndSource, out var hThumbnail))
        {
            Platform.Win32.DwmApi.DwmUnregisterThumbnail(hThumbnail);
        }
    }

    private void UnregisterAllThumbnails()
    {
        foreach (var hThumb in _thumbnails.Values)
        {
            Platform.Win32.DwmApi.DwmUnregisterThumbnail(hThumb);
        }
        _thumbnails.Clear();
    }

    private void UpdateThumbnailPosition(nint hThumbnail, Border placeholder)
    {
        try
        {
            var transform = placeholder.TransformToVisual(null);
            var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, placeholder.ActualWidth, placeholder.ActualHeight));

            var rect = new Platform.Win32.DwmApi.RECT(
                (int)bounds.Left,
                (int)bounds.Top,
                (int)bounds.Right,
                (int)bounds.Bottom
            );

            var props = new Platform.Win32.DwmApi.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Platform.Win32.DwmApi.DWM_TNP_RECTDESTINATION | Platform.Win32.DwmApi.DWM_TNP_VISIBLE | Platform.Win32.DwmApi.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = rect,
                fVisible = true,
                fSourceClientAreaOnly = true
            };

            Platform.Win32.DwmApi.DwmUpdateThumbnailProperties(hThumbnail, ref props);
        }
        catch
        {
        }
    }

    public void OnWorkspacesControlPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            var card = FindCardGrid(source);
            if (card is not null && card.DataContext is WindowOverviewViewModel winVm)
            {
                _isDragging = true;
                _draggedHwnd = winVm.Hwnd;
                _draggedCard = card;
                _dragStartPoint = e.GetCurrentPoint(card).Position;

                // Unregister preview during drag
                UnregisterDwmThumbnail(_draggedHwnd);

                _transform = new TranslateTransform();
                card.RenderTransform = _transform;

                card.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }
    }

    public void OnWorkspacesControlPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging && _draggedCard is not null)
        {
            var currentPoint = e.GetCurrentPoint(_draggedCard).Position;
            _transform.X += currentPoint.X - _dragStartPoint.X;
            _transform.Y += currentPoint.Y - _dragStartPoint.Y;
            e.Handled = true;
        }
    }

    public void OnWorkspacesControlPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging && _draggedCard is not null)
        {
            _isDragging = false;
            _draggedCard.ReleasePointerCapture(e.Pointer);

            var currentPoint = e.GetCurrentPoint(WorkspacesControl).Position;
            string? targetWorkspaceId = FindDropWorkspaceId(currentPoint);

            _draggedCard.RenderTransform = null;

            if (targetWorkspaceId is not null)
            {
                _viewModel.MoveWindowToWorkspace(_draggedHwnd, targetWorkspaceId);
            }

            _draggedCard = null;
            _draggedHwnd = 0;
            e.Handled = true;

            // Re-register all thumbnails to update positions
            RegisterAllVisibleThumbnails();
        }
    }

    private string? FindDropWorkspaceId(Point point)
    {
        var count = _viewModel.Workspaces.Count;
        for (int i = 0; i < count; i++)
        {
            var container = WorkspacesControl.ContainerFromIndex(i) as UIElement;
            if (container is not null)
            {
                var transform = container.TransformToVisual(WorkspacesControl);
                var bounds = new Windows.Foundation.Rect(0, 0, container.RenderSize.Width, container.RenderSize.Height);
                var relativeBounds = transform.TransformBounds(bounds);
                
                if (point.X >= relativeBounds.Left && point.X <= relativeBounds.Right &&
                    point.Y >= relativeBounds.Top && point.Y <= relativeBounds.Bottom)
                {
                    return _viewModel.Workspaces[i].WorkspaceId;
                }
            }
        }

        return null;
    }

    private static Grid? FindCardGrid(DependencyObject child)
    {
        if (child is Grid grid && grid.DataContext is WindowOverviewViewModel)
        {
            return grid;
        }
        var parent = VisualTreeHelper.GetParent(child);
        if (parent is null) return null;
        return FindCardGrid(parent);
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindParent<T>(parentObject);
    }

    private static void FindChildren<T>(DependencyObject parent, List<T> results) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                results.Add(typed);
            }
            FindChildren(child, results);
        }
    }
}
