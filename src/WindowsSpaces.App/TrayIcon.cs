using System.Runtime.InteropServices;

namespace WindowsSpaces.App;

/// <summary>
/// Minimal Shell_NotifyIcon wrapper for a status-only tray icon. This is
/// UI chrome (not window management), so it is intentionally kept local
/// to the App project rather than routed through Core/Platform.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    private const int NIF_MESSAGE = 0x1;
    private const int NIF_ICON = 0x2;
    private const int NIF_TIP = 0x4;
    private const int NIM_ADD = 0x0;
    private const int NIM_MODIFY = 0x1;
    private const int NIM_DELETE = 0x2;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    private NOTIFYICONDATA _data;
    private bool _added;

    public TrayIcon(nint hwnd)
    {
        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_TIP,
            uCallbackMessage = 0x8000, // WM_APP
            hIcon = 0,
            szTip = "Windows Spaces"
        };
    }

    public void Show()
    {
        _added = Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    public void SetTooltip(string text)
    {
        _data.szTip = text;
        if (_added) Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    public void Dispose()
    {
        if (_added) Shell_NotifyIcon(NIM_DELETE, ref _data);
    }
}
