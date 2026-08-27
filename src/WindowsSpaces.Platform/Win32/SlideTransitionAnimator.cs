using System.Collections.Concurrent;
using System.Diagnostics;
using WindowsSpaces.Core;
using static WindowsSpaces.Platform.Win32.NativeMethods;

namespace WindowsSpaces.Platform.Win32;

/// <summary>
/// macOS-style directional slide between spaces: the outgoing space slides
/// off one edge while the incoming one slides in from the other.
///
/// Nothing the user's windows do is animated directly. A borderless topmost
/// overlay is raised over the monitor showing live DWM mirrors of the
/// outgoing windows — pixel-identical to what was already there, so raising
/// it is invisible — and the real windows are then hidden and shown behind
/// it. Only the mirrors move. That keeps real window bounds untouched (apps
/// that clamp or reflow on move never see anything) and keeps the whole
/// animation off the UI thread: <see cref="Animate"/> queues the transition
/// and returns immediately.
/// </summary>
public sealed class SlideTransitionAnimator : IWorkspaceTransitionAnimator, IDisposable
{
    private const string WindowClassName = "WindowsSpacesSlideOverlay";
    /// <summary>How long the outgoing space takes to leave.</summary>
    private const int OutgoingMilliseconds = 240;

    /// <summary>
    /// How long after the outgoing space starts leaving the incoming one
    /// starts arriving. Doubles as the grace period the freshly shown windows
    /// get to paint themselves before their mirrors are put on screen.
    /// </summary>
    private const int IncomingDelayMilliseconds = 200;

    /// <summary>How long the incoming space takes to arrive.</summary>
    private const int IncomingMilliseconds = 240;

    private const int FrameSleepMilliseconds = 4;

    // Kept alive for the process lifetime: the window class holds a raw
    // function pointer to it, and a collected delegate is a hard crash.
    private static readonly WndProc OverlayWndProc = (hwnd, msg, wParam, lParam) => DefWindowProc(hwnd, msg, wParam, lParam);
    private static readonly object ClassLock = new();
    private static bool _classRegistered;

    private readonly IMonitorManager _monitors;
    private readonly Func<bool> _isEnabled;
    private readonly BlockingCollection<WorkspaceTransition> _queue = new();
    private readonly ConcurrentDictionary<string, int> _queuedByMonitor = new();
    private readonly Thread _worker;
    private bool _disposed;

    /// <param name="isEnabled">
    /// Read per transition, not once at construction: the animator outlives
    /// any single configuration, and the user can turn transitions off in
    /// Settings while the app is running.
    /// </param>
    public SlideTransitionAnimator(IMonitorManager monitors, Func<bool>? isEnabled = null)
    {
        _monitors = monitors;
        _isEnabled = isEnabled ?? (() => true);
        _worker = new Thread(RunWorker)
        {
            IsBackground = true,
            Name = "WindowsSpaces slide transitions"
        };
        _worker.Start();
    }

    public void Animate(WorkspaceTransition transition)
    {
        if (_disposed || !IsEnabled())
        {
            Apply(transition);
            return;
        }

        // Counted before queueing so an in-flight transition for this monitor
        // can see that it has been superseded and cut itself short.
        _queuedByMonitor.AddOrUpdate(transition.MonitorId, 1, (_, count) => count + 1);

        try
        {
            _queue.Add(transition);
        }
        catch (Exception)
        {
            // Racing a Dispose. The switch still has to be applied.
            _queuedByMonitor.AddOrUpdate(transition.MonitorId, 0, (_, count) => count - 1);
            Apply(transition);
        }
    }

    private void RunWorker()
    {
        foreach (var transition in _queue.GetConsumingEnumerable())
        {
            _queuedByMonitor.AddOrUpdate(transition.MonitorId, 0, (_, count) => count - 1);

            try
            {
                Run(transition);
            }
            catch (Exception)
            {
                // A failed animation must never cost the switch itself.
                Apply(transition);
            }
        }
    }

    /// <summary>The switch with no animation at all — the fallback everywhere.</summary>
    private static void Apply(WorkspaceTransition transition)
    {
        transition.ShowIncoming();
        transition.HideOutgoing();
    }

    private void Run(WorkspaceTransition transition)
    {
        var monitor = _monitors.GetMonitors().FirstOrDefault(m => m.Id == transition.MonitorId);

        // Nothing to slide, nowhere to slide it, or no direction to slide in
        // (the first switch after startup has no space to come from).
        if (monitor is null ||
            transition.Direction == TransitionDirection.None ||
            (transition.OutgoingWindows.Count == 0 && transition.IncomingWindows.Count == 0))
        {
            Apply(transition);
            return;
        }

        var bounds = monitor.Bounds;
        var overlay = CreateOverlay(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        if (overlay == 0)
        {
            Apply(transition);
            return;
        }

        var thumbnails = new List<Thumbnail>();

        // Registered first so it sits underneath every window mirror: a live,
        // stationary mirror of the wallpaper behind this monitor. Without it
        // the overlay's own black background is what shows between and behind
        // the sliding windows, and the whole transition reads as a blackout.
        var backdrop = RegisterBackdrop(overlay, bounds.X, bounds.Y, bounds.Width, bounds.Height);

        try
        {
            // The outgoing windows are still on screen: mirror them where they
            // already are, so raising the overlay changes nothing visually.
            foreach (var hwnd in transition.OutgoingWindows)
            {
                // Positioned before the overlay is raised — a thumbnail with
                // no destination yet renders nothing, which would show through
                // as a black frame at the very start of the slide.
                Register(thumbnails, overlay, hwnd, bounds.X, bounds.Y)?.MoveTo(0);
            }

            if (backdrop is null && thumbnails.Count == 0)
            {
                // Nothing would be visible but the black overlay: skip the
                // animation. The finally block still applies the switch.
                return;
            }

            ShowOverlay(overlay, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            PumpMessages();
            var outgoingCount = thumbnails.Count;

            // Next means the incoming space arrives from the right, so the
            // content travels leftwards; previous is the mirror image.
            var sign = transition.Direction == TransitionDirection.Next ? 1 : -1;
            var distance = bounds.Width;

            // Safe now — the overlay hides the pop-in.
            transition.ShowIncoming();

            foreach (var hwnd in transition.IncomingWindows)
            {
                // Hidden until its leg of the animation begins — see below.
                Register(thumbnails, overlay, hwnd, bounds.X, bounds.Y)?.MoveTo(sign * distance, visible: false);
            }

            var clock = Stopwatch.StartNew();
            while (true)
            {
                var elapsed = clock.ElapsedMilliseconds;

                // The outgoing space leaves first and the incoming one follows
                // it in, rather than the two moving as one strip. That is not
                // only for looks: a window that has just come back from SW_HIDE
                // has not repainted yet, and its mirror is a flat sheet of its
                // backdrop colour until it does. Letting the outgoing space
                // clear the screen first buys those windows the time they need
                // to paint, without the switch ever standing still.
                var outProgress = Math.Clamp(elapsed / (double)OutgoingMilliseconds, 0, 1);
                var incomingElapsed = elapsed - IncomingDelayMilliseconds;
                var inProgress = Math.Clamp(incomingElapsed / (double)IncomingMilliseconds, 0, 1);

                var outTravelled = (int)Math.Round(EaseOut(outProgress) * distance);
                var inTravelled = (int)Math.Round(EaseOut(inProgress) * distance);

                for (var i = 0; i < thumbnails.Count; i++)
                {
                    if (i < outgoingCount)
                    {
                        thumbnails[i].MoveTo(-sign * outTravelled);
                    }
                    else
                    {
                        thumbnails[i].MoveTo(sign * (distance - inTravelled), visible: incomingElapsed >= 0);
                    }
                }

                PumpMessages();

                // A later switch on this monitor is already waiting: stop
                // here rather than making the user watch a queue drain.
                var done = thumbnails.Count > outgoingCount ? inProgress >= 1 : outProgress >= 1;
                if (done || IsSuperseded(transition.MonitorId)) break;

                Thread.Sleep(FrameSleepMilliseconds);
            }
        }
        finally
        {
            // Order matters: hide the real windows while the overlay is still
            // covering them, or they flash back at full size for a frame.
            transition.ShowIncoming();
            transition.HideOutgoing();

            foreach (var thumbnail in thumbnails) thumbnail.Dispose();
            backdrop?.Dispose();
            DestroyWindow(overlay);
            PumpMessages();
        }
    }

    private bool IsEnabled()
    {
        try { return _isEnabled(); }
        catch (Exception) { return false; }
    }

    private bool IsSuperseded(string monitorId) =>
        _queuedByMonitor.TryGetValue(monitorId, out var queued) && queued > 0;

    /// <summary>Cubic ease-out: fast departure, soft landing.</summary>
    private static double EaseOut(double t)
    {
        var inverse = 1 - t;
        return 1 - inverse * inverse * inverse;
    }

    private static Thumbnail? Register(List<Thumbnail> thumbnails, nint overlay, nint source, int originX, int originY)
    {
        if (!IsWindow(source)) return null;
        if (!GetWindowRect(source, out var rect)) return null;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        if (DwmApi.DwmRegisterThumbnail(overlay, source, out var handle) != 0) return null;

        var thumbnail = new Thumbnail(
            handle,
            rect.Left - originX,
            rect.Top - originY,
            width,
            height);

        thumbnails.Add(thumbnail);
        return thumbnail;
    }

    /// <summary>
    /// Mirrors the part of the desktop wallpaper that sits behind this
    /// monitor into the overlay, cropped to the monitor and pinned in place.
    /// It never moves during the slide — the wallpaper stays put while the
    /// spaces travel over it, which is what the user sees on a real desktop.
    /// Returns null if the wallpaper host can't be found or doesn't cover the
    /// monitor, in which case the overlay's black background is used instead.
    /// </summary>
    private static Thumbnail? RegisterBackdrop(nint overlay, int x, int y, int width, int height)
    {
        var host = FindWallpaperHost();
        if (host == 0) return null;
        if (!GetWindowRect(host, out var rect)) return null;

        // The host spans the whole virtual desktop; crop to this monitor.
        var left = x - rect.Left;
        var top = y - rect.Top;
        if (left < 0 || top < 0 ||
            left + width > rect.Right - rect.Left ||
            top + height > rect.Bottom - rect.Top)
        {
            return null;
        }

        if (DwmApi.DwmRegisterThumbnail(overlay, host, out var handle) != 0) return null;

        var thumbnail = new Thumbnail(
            handle,
            0,
            0,
            width,
            height,
            new DwmApi.RECT(left, top, left + width, top + height));

        thumbnail.MoveTo(0);
        return thumbnail;
    }

    /// <summary>
    /// The window the shell currently paints the wallpaper on. Usually
    /// Progman, but once the shell has spawned its desktop WorkerW pair the
    /// wallpaper moves to the WorkerW sitting directly behind the one that
    /// owns the icon view.
    /// </summary>
    private static nint FindWallpaperHost()
    {
        nint wallpaper = 0;

        EnumWindows((hwnd, _) =>
        {
            if (FindWindowEx(hwnd, 0, "SHELLDLL_DefView", null) == 0) return true;

            var behind = FindWindowEx(0, hwnd, "WorkerW", null);
            if (behind == 0) return true;

            wallpaper = behind;
            return false;
        }, 0);

        return wallpaper != 0 ? wallpaper : FindWindowEx(0, 0, "Progman", null);
    }

    private sealed class Thumbnail : IDisposable
    {
        private readonly nint _handle;
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;
        private readonly DwmApi.RECT? _source;

        public Thumbnail(nint handle, int x, int y, int width, int height, DwmApi.RECT? source = null)
        {
            _handle = handle;
            _x = x;
            _y = y;
            _width = width;
            _height = height;
            _source = source;
        }

        /// <summary>Places the mirror <paramref name="offsetX"/> pixels from where its window really is.</summary>
        public void MoveTo(int offsetX, bool visible = true)
        {
            var properties = new DwmApi.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DwmApi.DWM_TNP_RECTDESTINATION | DwmApi.DWM_TNP_VISIBLE | DwmApi.DWM_TNP_OPACITY,
                rcDestination = new DwmApi.RECT(_x + offsetX, _y, _x + offsetX + _width, _y + _height),
                opacity = 255,
                fVisible = visible
            };

            if (_source is { } source)
            {
                properties.dwFlags |= DwmApi.DWM_TNP_RECTSOURCE;
                properties.rcSource = source;
            }

            DwmApi.DwmUpdateThumbnailProperties(_handle, ref properties);
        }

        public void Dispose() => DwmApi.DwmUnregisterThumbnail(_handle);
    }

    private static nint CreateOverlay(int x, int y, int width, int height)
    {
        EnsureClassRegistered();

        // WS_EX_NOACTIVATE and WS_EX_TRANSPARENT together mean the overlay
        // never takes focus and never eats a click — if anything goes wrong
        // and it outlives the animation, the user's input still reaches the
        // windows underneath.
        return CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOPMOST,
            WindowClassName,
            null,
            WS_POPUP,
            x, y, width, height,
            0, 0, GetModuleHandle(null), 0);
    }

    private static void ShowOverlay(nint overlay, int x, int y, int width, int height) =>
        SetWindowPos(overlay, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW | SWP_NOACTIVATE);

    private static void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classRegistered) return;

            var windowClass = new WNDCLASSEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = OverlayWndProc,
                hInstance = GetModuleHandle(null),
                hbrBackground = GetStockObject(BLACK_BRUSH),
                lpszClassName = WindowClassName
            };

            RegisterClassEx(ref windowClass);
            _classRegistered = true;
        }
    }

    /// <summary>
    /// The overlay lives on this thread, so this thread owes it a message
    /// pump — without one it is marked unresponsive and DWM stops compositing
    /// it mid-slide.
    /// </summary>
    private static void PumpMessages()
    {
        while (PeekMessage(out var message, 0, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.CompleteAdding();

        // Anything still queued never gets its animation, but every switch
        // still has to land — a queued transition dropped on the floor would
        // leave windows visible in a space they are not in.
        _worker.Join(TimeSpan.FromSeconds(2));

        foreach (var transition in _queue)
        {
            Apply(transition);
        }

        _queue.Dispose();
    }
}
