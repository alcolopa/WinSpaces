using System.Collections.Concurrent;

namespace WindowsSpaces.Core;

/// <summary>
/// Maintains the live WindowState collection from a stream of WindowEvents.
/// Event handling is synchronous and cheap by design — callers on Win32
/// WinEvent hooks must enqueue events elsewhere and dispatch them here
/// off the hook callback thread.
/// </summary>
public sealed class WindowTracker
{
    private readonly IWindowManager _windowManager;
    private readonly ConcurrentDictionary<nint, WindowState> _tracked = new();

    public WindowTracker(IWindowManager windowManager, IWindowEventSource eventSource)
    {
        _windowManager = windowManager;
        eventSource.WindowEvent += (_, evt) => HandleEvent(evt);
    }

    public IReadOnlyDictionary<nint, WindowState> TrackedWindows => _tracked;

    public void Rescan()
    {
        _tracked.Clear();
        foreach (var hwnd in _windowManager.EnumerateTopLevelWindows())
        {
            var state = _windowManager.GetWindowState(hwnd);
            if (state is not null)
            {
                _tracked[hwnd] = state;
            }
        }
    }

    public void HandleEvent(WindowEvent evt)
    {
        switch (evt.Kind)
        {
            case WindowEventKind.Destroyed:
                _tracked.TryRemove(evt.Hwnd, out _);
                break;

            case WindowEventKind.Created:
            case WindowEventKind.Shown:
            case WindowEventKind.Hidden:
            case WindowEventKind.LocationChanged:
            case WindowEventKind.ForegroundChanged:
                var state = _windowManager.GetWindowState(evt.Hwnd);
                if (state is not null)
                {
                    _tracked[evt.Hwnd] = state;
                }
                break;
        }
    }
}
