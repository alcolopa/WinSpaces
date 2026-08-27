using System.Drawing;
using WindowsSpaces.Core;
using static WindowsSpaces.Platform.Win32.NativeMethods;

namespace WindowsSpaces.Platform.Win32;

public sealed class WindowApi : IWindowManager
{
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
        "Button" // classic Start button, present on some configurations
    };

    public bool IsManageable(nint hwnd) => IsManagedTopLevelWindow(hwnd);

    /// <summary>
    /// This process. Our own UI (the overview panes, Settings, Shortcuts, …)
    /// looks like an ordinary managed window to every check below — visible,
    /// unowned, titled, not a tool window — so without this it gets tracked,
    /// assigned to a workspace, and then SW_HIDE'd by the next workspace
    /// switch. That is what made the overview vanish the moment the user
    /// added a space or moved a window from inside it.
    /// </summary>
    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;

    private static bool IsManagedTopLevelWindow(nint hWnd)
    {
        if (!IsWindowVisible(hWnd)) return false;

        GetWindowThreadProcessId(hWnd, out var owningProcessId);
        if (owningProcessId == CurrentProcessId) return false;

        if (GetWindow(hWnd, GW_OWNER) != 0) return false;
        if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        if (GetWindowTextLength(hWnd) == 0) return false;

        var classBuilder = new System.Text.StringBuilder(256);
        if (GetClassName(hWnd, classBuilder, classBuilder.Capacity) > 0 &&
            ShellWindowClasses.Contains(classBuilder.ToString()))
        {
            return false;
        }

        return true;
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

    public void Hide(nint hwnd)
    {
        // Set before the hide, not after: DWM decides whether to fade at the
        // moment the window is hidden. Left disabled while the window is out
        // of view; Show turns it back on.
        DwmApi.SetTransitionsDisabled(hwnd, true);
        ShowWindow(hwnd, SW_HIDE);
    }

    public void Show(nint hwnd)
    {
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
