using System.Collections.Concurrent;
using WindowsSpaces.Core;
using static WindowsSpaces.Platform.Win32.NativeMethods;

namespace WindowsSpaces.Platform.Win32;

/// <summary>
/// Wraps SetWinEventHook. The hook callback only enqueues events into a
/// ConcurrentQueue; a background thread drains the queue and raises
/// WindowEvent, keeping the hook callback itself lightweight per the
/// parent spec's requirement.
/// </summary>
public sealed class WinEventHook : IWindowEventSource, IDisposable
{
    private readonly ConcurrentQueue<WindowEvent> _queue = new();
    private readonly List<nint> _hooks = new();
    private readonly WinEventDelegate _callback;
    private Thread? _dispatchThread;
    private volatile bool _running;

    public event EventHandler<WindowEvent>? WindowEvent;

    public WinEventHook()
    {
        _callback = OnWinEvent;
    }

    public void Start()
    {
        AddHook(SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE, 0, _callback, 0, 0, WINEVENT_OUTOFCONTEXT));
        AddHook(SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, 0, _callback, 0, 0, WINEVENT_OUTOFCONTEXT));
        AddHook(SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _callback, 0, 0, WINEVENT_OUTOFCONTEXT));

        _running = true;
        _dispatchThread = new Thread(DispatchLoop) { IsBackground = true, Name = "WindowsSpaces.EventDispatch" };
        _dispatchThread.Start();
    }

    private void AddHook(nint hook)
    {
        if (hook == 0)
        {
            throw new InvalidOperationException($"SetWinEventHook failed, Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }
        _hooks.Add(hook);
    }

    public void Stop()
    {
        _running = false;
        foreach (var hook in _hooks)
        {
            if (hook != 0) UnhookWinEvent(hook);
        }
        _hooks.Clear();
    }

    private void OnWinEvent(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != OBJID_WINDOW || hwnd == 0) return;

        var kind = eventType switch
        {
            EVENT_OBJECT_CREATE => WindowEventKind.Created,
            EVENT_OBJECT_DESTROY => WindowEventKind.Destroyed,
            EVENT_OBJECT_SHOW => WindowEventKind.Shown,
            EVENT_OBJECT_HIDE => WindowEventKind.Hidden,
            EVENT_OBJECT_LOCATIONCHANGE => WindowEventKind.LocationChanged,
            EVENT_SYSTEM_FOREGROUND => WindowEventKind.ForegroundChanged,
            _ => (WindowEventKind?)null
        };

        if (kind is null) return;

        _queue.Enqueue(new WindowEvent(kind.Value, hwnd, DateTimeOffset.UtcNow));
    }

    private void DispatchLoop()
    {
        while (_running)
        {
            if (_queue.TryDequeue(out var evt))
            {
                WindowEvent?.Invoke(this, evt);
            }
            else
            {
                Thread.Sleep(10);
            }
        }
    }

    public void Dispose() => Stop();
}
