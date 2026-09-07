using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.Views;

/// <summary>
/// One full-screen overview pane, covering a single physical monitor and
/// showing that monitor's spaces side by side.
///
/// Everything in here is input plumbing and presentation only — every action
/// (switch, focus, move, close, add/rename/delete a space) is delegated to
/// <see cref="OverviewViewModel"/>, which routes it through WorkspaceManager.
/// </summary>
public sealed partial class OverviewWindow : Window
{
    /// <summary>Pointer travel, in DIPs, before a press on a window tile becomes a drag rather than a click.</summary>
    private const double DragThreshold = 6.0;

    private readonly OverviewViewModel _viewModel;

    private readonly Dictionary<nint, nint> _thumbnails = new();
    private readonly Dictionary<nint, FrameworkElement> _thumbnailHosts = new();

    private bool _isClosed;
    private bool _pointerCaptured;
    private WindowOverviewViewModel? _pressedWindow;
    private WorkspaceOverviewViewModel? _pressedWorkspace;
    private Point _pressOrigin;
    private bool _isDragging;
    private WorkspaceOverviewViewModel? _hoveredWorkspace;

    /// <summary>What the in-flight drag is carrying, or null when nothing is being dragged.</summary>
    private OverviewDragKind? _dragKind;

    /// <summary>True when the press landed on a button or the rename box, which handle their own clicks.</summary>
    private bool _pressOnInteractive;

    /// <summary>This pane's monitor in physical screen pixels — the frame every cross-pane drag coordinate is expressed in.</summary>
    private readonly System.Drawing.Rectangle _monitorBounds;

    private readonly OverviewDragCoordinator _coordinator;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int GWL_STYLE = -16;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly nint HWND_TOPMOST = -1;

    /// <summary>
    /// Raised when the user dismisses this pane. The host closes every pane
    /// in response — the overview is one surface across all monitors.
    /// </summary>
    public event EventHandler? CloseRequested;

    public OverviewWindow(
        Core.Monitor monitor,
        AppHost host,
        WorkspaceManager workspaceManager,
        WindowTracker windowTracker,
        OverviewDragCoordinator coordinator)
    {
        InitializeComponent();

        _monitorBounds = monitor.Bounds;
        _coordinator = coordinator;
        _coordinator.Register(this);

        _viewModel = new OverviewViewModel(
            monitor.Id,
            workspaceManager,
            windowTracker,
            host.AddWorkspace,
            host.RemoveWorkspace,
            host.RenameWorkspace,
            host.MoveWindowToMonitor,
            host.MoveWorkspaceToMonitor)
        {
            MonitorLabel = DescribeMonitor(monitor)
        };

        MonitorIdText.Text = _viewModel.MonitorLabel;
        WorkspacesControl.ItemsSource = _viewModel.Workspaces;

        // The view model's first Refresh runs inside its own constructor,
        // before the handlers below are attached, so seed from it directly.
        AddSpaceButton.Visibility = _viewModel.AddButtonVisibility;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Refreshed += OnViewModelRefreshed;

        // Borderless pop-up covering exactly this monitor.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, (style & ~WS_CAPTION & ~WS_THICKFRAME) | WS_POPUP);
        SetWindowPos(hwnd, HWND_TOPMOST, monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height,
            SWP_FRAMECHANGED | SWP_SHOWWINDOW);

        RootGrid.Loaded += OnRootLoaded;
        RootGrid.PointerPressed += OnRootPointerPressed;
        RootGrid.PointerMoved += OnRootPointerMoved;
        RootGrid.PointerReleased += OnRootPointerReleased;
        RootGrid.PointerCaptureLost += OnRootPointerCaptureLost;

        // handledEventsToo: a focused Button marks its own key presses handled,
        // and without this the overview's shortcuts would silently stop working
        // the moment the user tabbed to (or clicked) any button in it.
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), handledEventsToo: true);

        Closed += OnWindowClosed;
    }

    private static string DescribeMonitor(Core.Monitor monitor)
    {
        var label = $"{monitor.Bounds.Width}×{monitor.Bounds.Height}";
        return monitor.IsPrimary ? $"{label} · primary" : label;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        // The pane has to hold focus itself or none of its keyboard shortcuts
        // reach it — nothing else in the tree is focused on open.
        RootGrid.IsTabStop = true;
        RootGrid.Focus(FocusState.Programmatic);
        SchedulePreviewSync();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _coordinator.Unregister(this);
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Refreshed -= OnViewModelRefreshed;

        // Detach before teardown so a view-model refresh triggered by an
        // action taken on the way out can't push changes into a dead tree.
        WorkspacesControl.ItemsSource = null;
        UnregisterAllThumbnails();
    }

    private void RequestClose()
    {
        if (_isClosed) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isClosed) return;

        if (e.PropertyName == nameof(OverviewViewModel.StatusMessage))
        {
            StatusText.Text = _viewModel.StatusMessage ?? string.Empty;
            StatusText.Visibility = _viewModel.StatusVisibility;
        }
        else if (e.PropertyName == nameof(OverviewViewModel.AddButtonVisibility))
        {
            AddSpaceButton.Visibility = _viewModel.AddButtonVisibility;
        }
    }

    private void OnViewModelRefreshed(object? sender, EventArgs e)
    {
        if (_isClosed) return;
        AddSpaceButton.Visibility = _viewModel.AddButtonVisibility;
        SchedulePreviewSync();
    }

    // ---- Live window previews ------------------------------------------

    /// <summary>
    /// Re-positions every registered thumbnail once the pending layout pass
    /// has run. Positions computed before arrange are meaningless, and after
    /// a refresh every card has moved.
    /// </summary>
    private void SchedulePreviewSync()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_isClosed) return;
            foreach (var (hwnd, host) in _thumbnailHosts.ToList())
            {
                if (_thumbnails.TryGetValue(hwnd, out var thumbnail))
                {
                    UpdateThumbnailPosition(thumbnail, host);
                }
            }
        });
    }

    /// <summary>
    /// A DWM thumbnail is registered as a destination rect in screen space,
    /// computed from the preview host's position at the time it was
    /// registered/resized. Scrolling the card's window list moves that host
    /// within the ScrollViewer's viewport without raising Loaded or
    /// SizeChanged, so without this the thumbnail stays pinned at its old
    /// position while the row underneath it scrolls away.
    /// </summary>
    private void OnWindowListScrollViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isClosed) return;
        SchedulePreviewSync();
    }

    private void OnPreviewHostLoaded(object sender, RoutedEventArgs e)
    {
        if (_isClosed || sender is not FrameworkElement host) return;
        if (host.DataContext is not WindowOverviewViewModel window) return;

        // Only a window in the space that is actually on screen has a
        // composition surface for DWM to mirror; the rest are SW_HIDE'd and
        // would register a thumbnail that renders nothing.
        if (!window.CanPreview) return;

        RegisterThumbnail(window.Hwnd, host);
    }

    private void OnPreviewHostUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement host && host.DataContext is WindowOverviewViewModel window)
        {
            UnregisterThumbnail(window.Hwnd);
        }
    }

    private void OnPreviewHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isClosed || sender is not FrameworkElement host) return;
        if (host.DataContext is not WindowOverviewViewModel window) return;

        if (_thumbnails.TryGetValue(window.Hwnd, out var thumbnail))
        {
            UpdateThumbnailPosition(thumbnail, host);
        }
    }

    private void RegisterThumbnail(nint source, FrameworkElement host)
    {
        if (_thumbnails.ContainsKey(source)) return;

        var destination = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (Platform.Win32.DwmApi.DwmRegisterThumbnail(destination, source, out var thumbnail) != 0) return;

        _thumbnails[source] = thumbnail;
        _thumbnailHosts[source] = host;
        UpdateThumbnailPosition(thumbnail, host);
    }

    private void UnregisterThumbnail(nint source)
    {
        _thumbnailHosts.Remove(source);
        if (_thumbnails.Remove(source, out var thumbnail))
        {
            Platform.Win32.DwmApi.DwmUnregisterThumbnail(thumbnail);
        }
    }

    private void UnregisterAllThumbnails()
    {
        foreach (var thumbnail in _thumbnails.Values)
        {
            Platform.Win32.DwmApi.DwmUnregisterThumbnail(thumbnail);
        }
        _thumbnails.Clear();
        _thumbnailHosts.Clear();
    }

    private void UpdateThumbnailPosition(nint thumbnail, FrameworkElement host)
    {
        try
        {
            if (host.ActualWidth <= 0 || host.ActualHeight <= 0) return;

            var bounds = host.TransformToVisual(RootGrid)
                .TransformBounds(new Rect(0, 0, host.ActualWidth, host.ActualHeight));

            // DwmUpdateThumbnailProperties takes physical pixels relative to
            // the destination window; XAML hands us device-independent ones.
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;

            var rect = new Platform.Win32.DwmApi.RECT(
                (int)Math.Round(bounds.Left * scale),
                (int)Math.Round(bounds.Top * scale),
                (int)Math.Round(bounds.Right * scale),
                (int)Math.Round(bounds.Bottom * scale));

            var properties = new Platform.Win32.DwmApi.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Platform.Win32.DwmApi.DWM_TNP_RECTDESTINATION
                          | Platform.Win32.DwmApi.DWM_TNP_VISIBLE
                          | Platform.Win32.DwmApi.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = rect,
                fVisible = true,
                fSourceClientAreaOnly = true
            };

            Platform.Win32.DwmApi.DwmUpdateThumbnailProperties(thumbnail, ref properties);
        }
        catch
        {
            // A preview is decoration; never let one break the overview.
        }
    }

    // ---- Buttons --------------------------------------------------------

    private void OnAddWorkspaceClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.AddWorkspace();
    }

    private void OnRemoveWorkspaceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is WorkspaceOverviewViewModel workspace)
        {
            _viewModel.RemoveWorkspace(workspace.WorkspaceId);
        }
    }

    private void OnRenameWorkspaceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is WorkspaceOverviewViewModel workspace)
        {
            _viewModel.Select(workspace.WorkspaceId);
            _viewModel.BeginRename(workspace.WorkspaceId);
        }
    }

    private void OnCloseWindowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is WindowOverviewViewModel window)
        {
            _viewModel.CloseWindow(window.Hwnd);
        }
    }

    // ---- Inline rename --------------------------------------------------

    private void OnRenameBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;

        // The box is always in the tree with only its Visibility toggled, so
        // Loaded fires once and focus has to follow the visibility change.
        box.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (d, _) =>
        {
            if (d is TextBox target && target.Visibility == Visibility.Visible)
            {
                target.Focus(FocusState.Programmatic);
                target.SelectAll();
            }
        });

        if (box.Visibility == Visibility.Visible)
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        }
    }

    private void OnRenameBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not WorkspaceOverviewViewModel workspace) return;

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            CommitRename(workspace, box.Text);
            RootGrid.Focus(FocusState.Programmatic);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _viewModel.CancelRename();
            workspace.RevertName();
            RootGrid.Focus(FocusState.Programmatic);
        }
    }

    private void OnRenameBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not WorkspaceOverviewViewModel workspace) return;
        if (!workspace.IsEditingName) return;

        CommitRename(workspace, box.Text);
    }

    private void CommitRename(WorkspaceOverviewViewModel workspace, string text)
    {
        workspace.IsEditingName = false;
        _viewModel.RenameWorkspace(workspace.WorkspaceId, text);
    }

    // ---- Pointer: click to switch/focus, drag to move --------------------

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isClosed) return;

        _pressOrigin = e.GetCurrentPoint(RootGrid).Position;
        _isDragging = false;
        _dragKind = null;

        // What was pressed has to be resolved now, not on release: capturing
        // the pointer below re-targets every later pointer event to RootGrid,
        // so the release event's OriginalSource is no longer the tile.
        var source = e.OriginalSource as DependencyObject;
        _pressedWindow = FindDataContext<WindowOverviewViewModel>(source);
        _pressedWorkspace = FindDataContext<WorkspaceOverviewViewModel>(source);

        // A press that landed on a button or the rename box belongs to that
        // control. Capturing the pointer here would take the release away from
        // it and its click would never fire.
        _pressOnInteractive = IsInteractiveControl(source);
        if (_pressOnInteractive) return;

        if (_pressedWindow is not null)
        {
            _dragKind = OverviewDragKind.Window;
            DragGhostText.Text = _pressedWindow.Title;
        }
        else if (_pressedWorkspace is not null)
        {
            _dragKind = OverviewDragKind.Workspace;
            DragGhostText.Text = $"Space · {_pressedWorkspace.Name}";
        }

        if (_dragKind is not null)
        {
            _pointerCaptured = RootGrid.CapturePointer(e.Pointer);
        }
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isClosed || _dragKind is null) return;

        var position = e.GetCurrentPoint(RootGrid).Position;

        if (!_isDragging)
        {
            var travelled = Math.Abs(position.X - _pressOrigin.X) + Math.Abs(position.Y - _pressOrigin.Y);
            if (travelled < DragThreshold) return;

            _isDragging = true;
            _coordinator.Begin(new OverviewDragSession(
                _dragKind.Value,
                this,
                _viewModel.MonitorId,
                _pressedWindow?.Hwnd ?? 0,
                _pressedWindow?.WorkspaceId ?? _pressedWorkspace?.WorkspaceId ?? string.Empty,
                DragGhostText.Text));
        }

        // The pane that owns the capture keeps receiving moves after the
        // pointer has left its monitor, but the coordinates it reports are
        // then outside this pane. Screen coordinates are what the other panes
        // can act on, so broadcast those.
        if (TryGetCursorScreenPoint(out var screenX, out var screenY))
        {
            _coordinator.DragOver(screenX, screenY);
        }
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isClosed) return;

        var draggedWindow = _pressedWindow;
        var draggedWorkspace = _pressedWorkspace;
        var wasDragging = _isDragging;
        var kind = _dragKind;
        var wasInteractive = _pressOnInteractive;
        var hasScreenPoint = TryGetCursorScreenPoint(out var screenX, out var screenY);

        ResetDragState(e);

        // The button or text box under the press is handling this itself.
        if (wasInteractive) return;

        if (wasDragging && hasScreenPoint)
        {
            if (kind == OverviewDragKind.Window && draggedWindow is not null)
            {
                DropWindow(draggedWindow, screenX, screenY);
            }
            else if (kind == OverviewDragKind.Workspace && draggedWorkspace is not null)
            {
                DropWorkspace(draggedWorkspace, screenX, screenY);
            }

            return;
        }

        HandleClick(draggedWindow, draggedWorkspace);
    }

    /// <summary>
    /// Completes a window-tile drag. Dropped on this monitor it is the plain
    /// space reassignment; dropped on another monitor's pane the window moves
    /// monitor as well, landing on whichever space it was dropped on (or that
    /// monitor's on-screen space, if it missed the cards).
    /// </summary>
    private void DropWindow(WindowOverviewViewModel window, int screenX, int screenY)
    {
        var pane = _coordinator.PaneAt(screenX, screenY);
        if (pane is null) return;

        var targetWorkspaceId = pane.ResolveDropWorkspaceId(screenX, screenY);
        if (targetWorkspaceId is null) return;

        if (ReferenceEquals(pane, this))
        {
            _viewModel.MoveWindowToWorkspace(window.Hwnd, targetWorkspaceId);
            return;
        }

        if (_viewModel.MoveWindowToMonitor(window.Hwnd, pane.MonitorId, targetWorkspaceId))
        {
            _coordinator.RefreshAll();
        }
    }

    /// <summary>
    /// Completes a space-card drag. Only a drop on a different monitor means
    /// anything — dropping a space back on its own monitor is a no-op rather
    /// than a reorder, which the strip does not support.
    /// </summary>
    private void DropWorkspace(WorkspaceOverviewViewModel workspace, int screenX, int screenY)
    {
        var pane = _coordinator.PaneAt(screenX, screenY);
        if (pane is null || ReferenceEquals(pane, this)) return;

        if (_viewModel.MoveWorkspaceToMonitor(workspace.WorkspaceId, pane.MonitorId))
        {
            _coordinator.RefreshAll();
        }
    }

    private void OnRootPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        ResetDragState(e, releaseCapture: false);
    }

    private void ResetDragState(PointerRoutedEventArgs e, bool releaseCapture = true)
    {
        if (releaseCapture && _pointerCaptured)
        {
            RootGrid.ReleasePointerCapture(e.Pointer);
        }

        _pointerCaptured = false;
        _pressedWindow = null;
        _pressedWorkspace = null;
        _pressOnInteractive = false;
        _isDragging = false;
        _dragKind = null;

        // Clears the ghost and every drop highlight, on this pane and the
        // others — a drag that started here may have left feedback on any of
        // them.
        _coordinator.End();
    }

    // ---- Cross-pane drag feedback ---------------------------------------

    /// <summary>The monitor this pane covers.</summary>
    public string MonitorId => _viewModel.MonitorId;

    /// <summary>Whether a screen point (physical pixels) falls on this pane's monitor.</summary>
    public bool ContainsScreenPoint(int screenX, int screenY) =>
        screenX >= _monitorBounds.Left && screenX < _monitorBounds.Right &&
        screenY >= _monitorBounds.Top && screenY < _monitorBounds.Bottom;

    /// <summary>
    /// Draws this pane's share of an in-flight drag: the ghost when the
    /// pointer is over this monitor, plus the drop highlight — a single space
    /// card for a window drag, the whole pane for a space arriving from
    /// another monitor.
    /// </summary>
    public void UpdateDragFeedback(OverviewDragSession session, int screenX, int screenY)
    {
        if (_isClosed) return;

        if (!ContainsScreenPoint(screenX, screenY))
        {
            ClearDragFeedback();
            return;
        }

        var local = ToLocalPoint(screenX, screenY);

        DragGhostText.Text = session.GhostText;
        DragGhost.Visibility = Visibility.Visible;
        Canvas.SetLeft(DragGhost, local.X + 12);
        Canvas.SetTop(DragGhost, local.Y + 12);

        if (session.Kind == OverviewDragKind.Window)
        {
            SetHoveredWorkspace(FindWorkspaceAt(local));
            SetPaneDropTarget(null);
        }
        else
        {
            SetHoveredWorkspace(null);
            SetPaneDropTarget(ReferenceEquals(session.SourcePane, this)
                ? null
                : $"Move {session.GhostText} to this monitor");
        }
    }

    public void ClearDragFeedback()
    {
        if (_isClosed) return;

        DragGhost.Visibility = Visibility.Collapsed;
        SetHoveredWorkspace(null);
        SetPaneDropTarget(null);
    }

    /// <summary>Re-reads live state. Called on every pane after a drop, since a cross-monitor move changes two of them.</summary>
    public void RefreshFromCoordinator()
    {
        if (_isClosed) return;
        _viewModel.Refresh();
    }

    /// <summary>
    /// The space a drop at this screen point lands on: the card under the
    /// pointer, or — for a drop that missed the cards — the space this monitor
    /// currently has on screen, so a window dropped on a pane is never lost.
    /// </summary>
    public string? ResolveDropWorkspaceId(int screenX, int screenY)
    {
        if (_isClosed) return null;

        var card = FindWorkspaceAt(ToLocalPoint(screenX, screenY));
        if (card is not null) return card.WorkspaceId;

        var active = _viewModel.Workspaces.FirstOrDefault(w => w.IsActive) ?? _viewModel.Workspaces.FirstOrDefault();
        return active?.WorkspaceId;
    }

    /// <summary>Screen point (physical pixels) to a point in this pane's DIP coordinate space.</summary>
    private Point ToLocalPoint(int screenX, int screenY)
    {
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0) scale = 1.0;

        return new Point((screenX - _monitorBounds.X) / scale, (screenY - _monitorBounds.Y) / scale);
    }

    private static bool TryGetCursorScreenPoint(out int screenX, out int screenY)
    {
        if (GetCursorPos(out var point))
        {
            screenX = point.X;
            screenY = point.Y;
            return true;
        }

        screenX = 0;
        screenY = 0;
        return false;
    }

    private void SetPaneDropTarget(string? hint)
    {
        if (hint is null)
        {
            PaneDropTarget.Visibility = Visibility.Collapsed;
            return;
        }

        PaneDropTargetText.Text = hint;
        PaneDropTarget.Visibility = Visibility.Visible;
    }

    private void SetHoveredWorkspace(WorkspaceOverviewViewModel? workspace)
    {
        if (ReferenceEquals(_hoveredWorkspace, workspace)) return;

        if (_hoveredWorkspace is not null) _hoveredWorkspace.IsDropTarget = false;
        _hoveredWorkspace = workspace;
        if (_hoveredWorkspace is not null) _hoveredWorkspace.IsDropTarget = true;
    }

    /// <summary>
    /// A plain click: on a window tile it focuses that window, on a space card
    /// it switches to that space, and on the backdrop it dismisses the
    /// overview. All three end the overview session.
    /// </summary>
    private void HandleClick(WindowOverviewViewModel? window, WorkspaceOverviewViewModel? workspace)
    {
        if (window is not null)
        {
            // Switches to the window's space if needed, then raises it. Done
            // before the pane is torn down so the pane is still the foreground
            // window and the activation is not blocked by the foreground lock.
            _viewModel.ActivateWindow(window.Hwnd);

            // Clicking a tile, a card, or the backdrop all end the overview
            // session — the way the platform's other full-screen overlays behave.
            RequestClose();
            return;
        }

        if (workspace is not null)
        {
            // Panes down first, switch second: the slide transition is a
            // topmost overlay over the whole monitor, and running it while the
            // panes are still up would paint it over them (or, suppressed,
            // lose the animation entirely for the one gesture that most wants
            // it).
            RequestClose();
            _viewModel.SwitchToWorkspace(workspace.WorkspaceId);
            return;
        }

        RequestClose();
    }

    private WorkspaceOverviewViewModel? FindWorkspaceAt(Point position)
    {
        for (var i = 0; i < _viewModel.Workspaces.Count; i++)
        {
            if (WorkspacesControl.ContainerFromIndex(i) is not UIElement container) continue;

            var bounds = container.TransformToVisual(RootGrid)
                .TransformBounds(new Rect(0, 0, container.RenderSize.Width, container.RenderSize.Height));

            if (position.X >= bounds.Left && position.X <= bounds.Right &&
                position.Y >= bounds.Top && position.Y <= bounds.Bottom)
            {
                return _viewModel.Workspaces[i];
            }
        }

        return null;
    }

    private static T? FindDataContext<T>(DependencyObject? element) where T : class
    {
        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: T match }) return match;
            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    /// <summary>
    /// Whether the press landed on a control that owns its own pointer
    /// handling — the per-card buttons and the inline rename box.
    /// </summary>
    private static bool IsInteractiveControl(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or TextBox) return true;
            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    // ---- Keyboard -------------------------------------------------------

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isClosed) return;

        // While a space name is being edited every key belongs to the text
        // box — otherwise typing "n" in a name would spawn a new space.
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox) return;

        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                RequestClose();
                return;

            case VirtualKey.Left:
                e.Handled = true;
                _viewModel.MoveSelection(-1);
                return;

            case VirtualKey.Right:
                e.Handled = true;
                _viewModel.MoveSelection(1);
                return;

            case VirtualKey.Enter:
            case VirtualKey.Space:
                if (_viewModel.SelectedWorkspace is { } selected)
                {
                    e.Handled = true;
                    RequestClose();
                    _viewModel.SwitchToWorkspace(selected.WorkspaceId);
                }
                return;

            case VirtualKey.Delete:
                e.Handled = true;
                _viewModel.RemoveSelectedWorkspace();
                return;

            case VirtualKey.N:
                e.Handled = true;
                _viewModel.AddWorkspace();
                return;

            case VirtualKey.F2:
                if (_viewModel.SelectedWorkspace is { } toRename)
                {
                    e.Handled = true;
                    _viewModel.BeginRename(toRename.WorkspaceId);
                }
                return;
        }

        // 1-9 jump straight to the Nth space, matching the switch hotkeys.
        if (e.Key >= VirtualKey.Number1 && e.Key <= VirtualKey.Number9)
        {
            var position = e.Key - VirtualKey.Number1 + 1;
            if (_viewModel.HasPosition(position))
            {
                e.Handled = true;
                RequestClose();
                _viewModel.SwitchToPosition(position);
            }
        }
    }
}
