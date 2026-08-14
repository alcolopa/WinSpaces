using System.Drawing;
using WindowsSpaces.Core;
using static WindowsSpaces.Platform.Win32.NativeMethods;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Platform.Win32;

public sealed class MonitorApi : IMonitorManager
{
    public event EventHandler? MonitorsChanged;

    public IReadOnlyList<Monitor> GetMonitors()
    {
        var monitors = new List<Monitor>();

        bool Callback(nint hMonitor, nint hdc, ref RECT rect, nint data)
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add(new Monitor(
                    Id: info.szDevice,
                    DevicePath: info.szDevice,
                    Bounds: Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
                    IsPrimary: (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }

        if (!EnumDisplayMonitors(0, 0, Callback, 0))
        {
            throw new InvalidOperationException($"EnumDisplayMonitors failed, Win32 error {GetLastError()}");
        }

        return monitors;
    }

    public Monitor? GetMonitorForWindow(nint hwnd)
    {
        var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == 0) return null;

        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hMonitor, ref info)) return null;

        return new Monitor(
            Id: info.szDevice,
            DevicePath: info.szDevice,
            Bounds: Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
            IsPrimary: (info.dwFlags & MONITORINFOF_PRIMARY) != 0);
    }
}
