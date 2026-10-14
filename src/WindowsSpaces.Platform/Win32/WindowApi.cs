using System.Drawing;
using WindowsSpaces.Core;
using static WindowsSpaces.Platform.Win32.NativeMethods;

namespace WindowsSpaces.Platform.Win32;

public sealed class WindowApi : IWindowManager
{
    private readonly HiddenWindowJournal _journal;

    public WindowApi() : this(new HiddenWindowJournal(HiddenWindowJournal.DefaultPath)) { }

    public WindowApi(HiddenWindowJournal journal) => _journal = journal;

    /// <summary>
    /// Shows windows a previous run hid and never got to restore (it was
    /// killed or lost power). Call once at startup, before tracking begins.
    /// Returns how many windows were shown.
    /// </summary>
    public int RecoverHiddenWindows()
    {
        var recovered = 0;
        foreach (var entry in _journal.TakeStaleEntries())
        {
            var hwnd = (nint)entry.Hwnd;
            if (!IsWindow(hwnd)) continue;

            GetWindowThreadProcessId(hwnd, out var pid);
            var cls = new System.Text.StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            if (pid != entry.ProcessId || cls.ToString() != entry.WindowClass) continue;

            // Parked windows are still visible, just off-screen: put them back
            // where they were.
            if (entry.Parked is { } p)
            {
                SetWindowPos(hwnd, 0, p.Left, p.Top, p.Width, p.Height, SWP_NOACTIVATE);
                recovered++;
                continue;
            }

            if (IsWindowVisible(hwnd)) continue;

            Show(hwnd);
            recovered++;
        }

        return recovered + RecoverStrandedWindows();
    }

    /// <summary>
    /// Pulls back real windows left sitting in the parking area with no journal
    /// entry to say where they belong (a crash before the journal was written,
    /// or a journal lost with the machine). A fresh process would otherwise
    /// ignore them as helper surfaces and they would stay off-screen for good.
    /// Helper strips are tiny; anything window-sized is moved onto the screen.
    /// </summary>
    private int RecoverStrandedWindows()
    {
        var moved = 0;

        bool Callback(nint hWnd, nint lParam)
        {
            if (!IsManagedTopLevelWindow(hWnd, includeParkArea: true) || IsIconic(hWnd) || _parked.ContainsKey(hWnd)) return true;
            if (!GetWindowRect(hWnd, out var rect) || rect.Left > -16000) return true;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 300 || height < 200) return true;

            SetWindowPos(hWnd, 0, 100, 100, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
            moved++;
            return true;
        }

        EnumWindows(Callback, 0);
        return moved;
    }

    public IReadOnlyList<nint> EnumerateTopLevelWindows()
    {
        var result = new List<nint>();

        bool Callback(nint hWnd, nint lParam)
        {
            if (IsManagedTopLevelWindow(hWnd))
            {
                result.Add(hWnd);
            }
            return true;
        }

        if (!EnumWindows(Callback, 0))
        {
            var errorCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (errorCode != 0)
            {
                throw new InvalidOperationException($"EnumWindows failed, Win32 error {errorCode}");
            }
        }

        return result;
    }

    // Shell chrome (desktop, taskbar, Start) shows up in EnumWindows like any
    // other top-level window — visible, unowned, not a tool window, and (for
    // Progman at least) with a non-empty title — so title/style checks alone
    // let it slip through and get hidden/shown by workspace switches. If the
    // app crashes between hiding and re-showing it, the taskbar/desktop stay
    // gone until Explorer is manually restarted. Exclude these classes by
    // name so they're never tracked at all.
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Button", // classic Start button, present on some configurations
        "TopLevelWindowForOverflowXamlIsland" // taskbar's hidden tray-icon overflow flyout host
    };

    // Windows keeps these shell-surface hosts (Start menu, Search, Task View,
    // touch keyboard/emoji panel) running persistently in the background and
    // reuses one window per host rather than creating/destroying it each time
    // the user opens and dismisses it. That window is genuinely visible and
    // uncloaked at the moment the user invokes it, so it passes every other
    // check here and gets tracked once — then, because it's never destroyed,
    // it lingers in the workspace's window list forever, looking like a
    // permanently "open" app the user never actually launched. Exclude these
    // hosts by process name so they're never tracked in the first place.
    private static readonly HashSet<string> ShellHostProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SearchHost",
        "StartMenuExperienceHost",
        "ShellExperienceHost",
        "TextInputHost",
        "ShellHost",
        "LockApp" // the lock screen: lives on as a background process after unlocking
    };

    /// <summary>
    /// Exact titles of helper windows that apps create alongside their real
    /// window and that are never something the user opened.
    /// <list type="bullet">
    /// <item>"Chrome Legacy Window" — Chromium's internal window-management helper.</item>
    /// <item>"ReunionCaptionControlsWindow" — the Windows App SDK (formerly
    /// "Project Reunion") window that draws the minimize/maximize/close buttons
    /// of a custom title bar; WhatsApp and other WinUI 3 apps create one.</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> HelperWindowTitles = new(StringComparer.Ordinal)
    {
        "Chrome Legacy Window",
        "ReunionCaptionControlsWindow"
    };

    public bool IsManageable(nint hwnd) => IsManagedTopLevelWindow(hwnd);

    public bool IsCloaked(nint hwnd) => IsWindowCloaked(hwnd);

    private static bool IsWindowCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>
    /// This process. Our own UI (the overview panes, Settings, Shortcuts, …)
    /// looks like an ordinary managed window to every check below — visible,
    /// unowned, titled, not a tool window — so without this it gets tracked,
    /// assigned to a workspace, and then SW_HIDE'd by the next workspace
    /// switch. That is what made the overview vanish the moment the user
    /// added a space or moved a window from inside it.
    /// </summary>
    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

    private static bool IsManagedTopLevelWindow(nint hWnd, bool includeParkArea = false)
    {
        if (!IsWindowVisible(hWnd)) return false;

        GetWindowThreadProcessId(hWnd, out var owningProcessId);
        if (owningProcessId == CurrentProcessId) return false;

        if (GetWindow(hWnd, GW_OWNER) != 0) return false;
        if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        if (GetWindowTextLength(hWnd) == 0) return false;

        var classBuilder = new System.Text.StringBuilder(256);
        var windowClass = GetClassName(hWnd, classBuilder, classBuilder.Capacity) > 0 ? classBuilder.ToString() : string.Empty;
        if (ShellWindowClasses.Contains(windowClass))
        {
            return false;
        }

        // DWM-cloaked windows (e.g. UWP/Store app host windows kept alive off-
        // screen, and other background frame windows) report IsWindowVisible
        // == true despite never actually being drawn or seen by the user.
        // Without this check every such window gets tracked and dumped onto
        // a workspace like a real user window, so a workspace can appear to
        // list "every process" instead of just what the user actually opened.
        if (IsWindowCloaked(hWnd))
        {
            return false;
        }

        var processName = GetProcessName(owningProcessId);
        if (ShellHostProcessNames.Contains(processName))
        {
            return false;
        }

        // Task View / "Running applications" is a shell CoreWindow hosted
        // inside explorer.exe itself, indistinguishable from other shell
        // surfaces except that it lives in explorer's process. Real File
        // Explorer windows never use this class (they're CabinetWClass), so
        // this can't exclude an actual File Explorer window.
        if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(windowClass, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // explorer.exe also hosts a pile of internal surfaces that are visible
        // top-level windows but never something the user opened: XAML-island
        // hosts ("DesktopWindowXamlSource"), "Details Pane", file-dialog
        // fragments, and so on. The only explorer window a user actually
        // opens is a File Explorer window (CabinetWClass), so allow only that.
        if (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(windowClass, "CabinetWClass", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Non-activating windows (popups, flyouts, helper surfaces) can't be
        // focused by the user, so they aren't windows the user "opened".
        if (((uint)GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_NOACTIVATE) != 0)
        {
            return false;
        }

        // Helper surfaces that belong to a parked window report positions in
        // the parking area (the parent's offset carries over). They are not
        // windows the user opened; only ones this process parked itself count.
        if (!includeParkArea && !IsIconic(hWnd) && !_parked.ContainsKey(hWnd) && GetWindowRect(hWnd, out var parkedArea) && parkedArea.Left <= -16000)
        {
            return false;
        }

        // Degenerate-size windows are invisible helpers, not user content.
        // Minimized windows report a parked off-screen rect, so skip them.
        if (!IsIconic(hWnd) && GetWindowRect(hWnd, out var rect) &&
            (rect.Right - rect.Left < 32 || rect.Bottom - rect.Top < 32))
        {
            return false;
        }

        // Chromium (Edge/Chrome/any Chromium-based app) creates a hidden
        // internal helper window with this exact literal title as part of
        // its own window management — never real user-facing content, and
        // Windows still reports it visible/uncloaked.
        var titleBuilder = new System.Text.StringBuilder(256);
        if (GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity) > 0 &&
            HelperWindowTitles.Contains(titleBuilder.ToString()))
        {
            return false;
        }

        return true;
    }

    private static string GetProcessName(uint processId)
    {
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (int)processId);
        if (hProcess == 0) return string.Empty;

        try
        {
            var size = 1024;
            var pathBuilder = new System.Text.StringBuilder(size);
            if (!QueryFullProcessImageName(hProcess, 0, pathBuilder, ref size)) return string.Empty;
            return System.IO.Path.GetFileNameWithoutExtension(pathBuilder.ToString());
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    public WindowState? GetWindowState(nint hwnd)
    {
        if (!IsWindow(hwnd)) return null;

        var placement = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref placement))
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (err == 1400 || err == 5 || err == 0 || !IsWindow(hwnd)) return null;
            return null;
        }

        GetWindowThreadProcessId(hwnd, out var processId);

        var classBuilder = new System.Text.StringBuilder(256);
        string? windowClass = null;
        if (GetClassName(hwnd, classBuilder, classBuilder.Capacity) > 0)
        {
            windowClass = classBuilder.ToString();
        }

        var titleBuilder = new System.Text.StringBuilder(256);
        string? title = null;
        if (GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity) > 0)
        {
            title = titleBuilder.ToString();
        }

        string? processPath = null;
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (int)processId);
        if (hProcess != 0)
        {
            try
            {
                var size = 1024;
                var pathBuilder = new System.Text.StringBuilder(size);
                if (QueryFullProcessImageName(hProcess, 0, pathBuilder, ref size))
                {
                    processPath = pathBuilder.ToString();
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        var normal = placement.rcNormalPosition;

        return new WindowState
        {
            Hwnd = hwnd,
            ProcessId = (int)processId,
            ProcessPath = processPath,
            WindowClass = windowClass,
            Title = title,
            IsVisible = IsWindowVisible(hwnd),
            IsMinimized = placement.showCmd == SW_SHOWMINIMIZED,
            IsMaximized = placement.showCmd == SW_SHOWMAXIMIZED,
            NormalBounds = Rectangle.FromLTRB(normal.Left, normal.Top, normal.Right, normal.Bottom),
            LastUpdated = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Last pictures of windows, so the overview can preview windows that are currently hidden.</summary>
    public WindowSnapshotCache Snapshots { get; } = new();

    /// <summary>
    /// When set, <see cref="Hide"/> parks a window far off-screen instead of
    /// SW_HIDE-ing it. A hidden window loses its composition surface and has
    /// to repaint from scratch when shown, which is what makes switching to a
    /// heavy window stall; a parked one stays a live, painted window, so it
    /// can be mirrored and brought back instantly.
    /// </summary>
    public bool KeepWindowsRendered { get; set; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, RECT> _parked = new();

    /// <summary>Far enough left to be on no monitor, short of the 16-bit coordinate limit some apps assume.</summary>
    private const int ParkX = -24000;

    public bool IsParked(nint hwnd) => _parked.ContainsKey(hwnd);

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_FRAME = 0x0400;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RedrawWindow(nint hWnd, nint lprcUpdate, nint hrgnUpdate, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(nint hWnd);

    public void Hide(nint hwnd)
    {
        // Once hidden there is nothing left to capture. Normally the periodic
        // refresh has a recent picture already; this covers a window that
        // was never captured (new, or hidden within seconds of appearing).
        Snapshots.CaptureIfMissing(hwnd);

        if (_parked.ContainsKey(hwnd)) return;

        // Minimized windows have nothing to keep rendered; they take the
        // ordinary hide below.
        if (KeepWindowsRendered && !IsIconic(hwnd) && GetWindowRect(hwnd, out var rect))
        {
            // Journal first, as with a hide: a crash between the two lines
            // must leave a window that can be put back.
            GetWindowThreadProcessId(hwnd, out var parkedPid);
            var parkedClass = new System.Text.StringBuilder(256);
            GetClassName(hwnd, parkedClass, parkedClass.Capacity);
            _journal.Add(new HiddenWindowJournal.Entry(hwnd, parkedPid, parkedClass.ToString(),
                new HiddenWindowJournal.ParkedRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
            _parked[hwnd] = rect;
            SetWindowPos(hwnd, 0, ParkX, rect.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            return;
        }


        // Set before the hide, not after: DWM decides whether to fade at the
        // moment the window is hidden. Left disabled while the window is out
        // of view; Show turns it back on.
        DwmApi.SetTransitionsDisabled(hwnd, true);
        // Journal first: if we die between the two lines the window is
        // recorded and recoverable; the reverse order could lose it.
        GetWindowThreadProcessId(hwnd, out var pid);
        var cls = new System.Text.StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        _journal.Add(new HiddenWindowJournal.Entry(hwnd, pid, cls.ToString()));
        ShowWindow(hwnd, SW_HIDE);
    }

    public void Show(nint hwnd)
    {
        _journal.Remove(hwnd);

        // A parked window never stopped being visible: put it back where it
        // was and raise it (HWND_TOP, no activation). No ShowWindow, no
        // recompose kick — it never lost its surface.
        if (_parked.TryRemove(hwnd, out var parkedRect))
        {
            // Position and size both: the switch moves the window to its saved
            // normal bounds first, which for a maximized window is the wrong size,
            // and with no ShowWindow to re-maximize it that size would stick.
            SetWindowPos(hwnd, 0, parkedRect.Left, parkedRect.Top,
                parkedRect.Right - parkedRect.Left, parkedRect.Bottom - parkedRect.Top, SWP_NOACTIVATE);

            // Chromium-based apps stop painting while they are off-screen and
            // come back a pane at a time. Invalidating the whole tree tells every
            // child surface to redraw now rather than whenever it gets round to it.
            // No RDW_UPDATENOW: that would block on an app that isn't responding.
            if (!IsHungAppWindow(hwnd)) RedrawWindow(hwnd, 0, 0, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_FRAME);
            return;
        }

        var placement = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hwnd, ref placement);

        var showCmd = placement.showCmd switch
        {
            SW_SHOWMINIMIZED => SW_SHOWMINIMIZED,
            SW_SHOWMAXIMIZED => SW_SHOWMAXIMIZED,
            _ => SW_SHOWNOACTIVATE
        };

        ShowWindow(hwnd, showCmd);

        // DirectComposition-backed windows (WinUI3, Xaml Islands) can stop
        // painting after a raw SW_HIDE/SW_SHOW cycle — the compositor never
        // gets the resize/paint signal it normally relies on, leaving the
        // window's content solid black even though Win32 reports it visible.
        // A no-op SetWindowPos with SWP_FRAMECHANGED forces just this window
        // to recompose, without touching anything else on the desktop.
        SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // Restored only once the window is back on screen, so the window keeps
        // its normal minimise/maximise animations while the user is using it —
        // Hide is the only place the fade actually hurts.
        DwmApi.SetTransitionsDisabled(hwnd, false);
    }

    public void Move(nint hwnd, Rectangle bounds)
    {
        if (!IsWindow(hwnd)) return;
        if (!SetWindowPos(hwnd, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE))
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (err == 1400 || err == 5 || !IsWindow(hwnd)) return;
            // Best effort
        }
    }

    /// <summary>
    /// Brings a window to the front. A bare SetForegroundWindow is refused by
    /// Windows' foreground lock whenever the calling thread does not own the
    /// current foreground window — which is exactly the case when activating
    /// a window picked in the overview, since the overview is torn down first.
    /// Attaching to the current foreground thread's input queue lifts that
    /// restriction for the duration of the call.
    /// </summary>
    public void SetForeground(nint hwnd)
    {
        if (!IsWindow(hwnd)) return;

        var placement = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(hwnd, ref placement) && placement.showCmd == SW_SHOWMINIMIZED)
        {
            ShowWindow(hwnd, SW_RESTORE);
        }

        var currentForeground = NativeMethods.GetForegroundWindow();
        var thisThread = GetCurrentThreadId();
        var foregroundThread = currentForeground == 0
            ? thisThread
            : GetWindowThreadProcessId(currentForeground, out _);

        var attached = foregroundThread != thisThread &&
                       AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
    }

    /// <summary>
    /// Posts WM_CLOSE rather than terminating: the owning app still gets to
    /// run its shutdown path and show any "save changes?" prompt, and a
    /// refusal to close is its right. Never kills a process.
    /// </summary>
    public void Close(nint hwnd)
    {
        if (!IsWindow(hwnd)) return;
        PostMessage(hwnd, WM_CLOSE, 0, 0);
    }

    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();
}
