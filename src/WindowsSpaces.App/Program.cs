using System.Runtime.InteropServices;
using WindowsSpaces.App;

internal static class Program
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_DESTROY = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    private const nint HWND_MESSAGE = -3;

    private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public System.Drawing.Point pt;
    }

    private static AppHost? _host;
    private static WndProc? _wndProcDelegate;

    [STAThread]
    private static void Main()
    {
        _wndProcDelegate = WndProcHandler;
        var hInstance = GetModuleHandle(null);

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProcDelegate,
            hInstance = hInstance,
            lpszClassName = "WindowsSpacesMessageWindow",
            lpszMenuName = string.Empty
        };
        if (RegisterClassEx(ref wc) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed, Win32 error {Marshal.GetLastWin32Error()}");
        }

        var hwnd = CreateWindowEx(0, "WindowsSpacesMessageWindow", "WindowsSpaces", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, hInstance, 0);
        if (hwnd == 0)
        {
            throw new InvalidOperationException($"Failed to create message-only window for hotkey/tray hosting, Win32 error {Marshal.GetLastWin32Error()}");
        }

        _host = new AppHost();
        _host.Start(hwnd);

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY)
            {
                _host.HandleMessage(msg.message, msg.wParam);
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        _host.Dispose();
    }

    private static nint WndProcHandler(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_DESTROY)
        {
            _host?.Dispose();
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
