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

    private static bool IsManagedTopLevelWindow(nint hWnd)
    {
        if (!IsWindowVisible(hWnd)) return false;
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

    public void SetForeground(nint hwnd)
    {
        SetForegroundWindow(hwnd);
    }

    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();
}
