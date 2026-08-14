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
            throw new InvalidOperationException($"EnumWindows failed, Win32 error {GetLastError()}");
        }

        return result;
    }

    private static bool IsManagedTopLevelWindow(nint hWnd)
    {
        if (!IsWindowVisible(hWnd)) return false;
        if (GetWindow(hWnd, GW_OWNER) != 0) return false;
        if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        if (GetWindowTextLength(hWnd) == 0) return false;
        return true;
    }

    public WindowState? GetWindowState(nint hwnd)
    {
        if (!IsWindow(hwnd)) return null;

        var placement = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref placement))
        {
            throw new InvalidOperationException($"GetWindowPlacement failed for {hwnd}, Win32 error {GetLastError()}");
        }

        GetWindowThreadProcessId(hwnd, out var processId);

        var normal = placement.rcNormalPosition;

        return new WindowState
        {
            Hwnd = hwnd,
            ProcessId = (int)processId,
            IsVisible = IsWindowVisible(hwnd),
            IsMinimized = placement.showCmd == SW_MINIMIZE,
            IsMaximized = placement.showCmd == 3, // SW_SHOWMAXIMIZED
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
            2 => 2,   // SW_SHOWMINIMIZED -> keep minimized
            3 => 3,   // SW_SHOWMAXIMIZED -> keep maximized
            _ => SW_SHOWNOACTIVATE
        };

        ShowWindow(hwnd, showCmd);
    }

    public void Move(nint hwnd, Rectangle bounds)
    {
        if (!SetWindowPos(hwnd, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE))
        {
            throw new InvalidOperationException($"SetWindowPos failed for {hwnd}, Win32 error {GetLastError()}");
        }
    }

    public void SetForeground(nint hwnd)
    {
        SetForegroundWindow(hwnd);
    }

    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();
}
