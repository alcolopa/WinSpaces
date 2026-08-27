using System;
using System.IO;
using System.Runtime.InteropServices;

class Program
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? lpModuleName);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    static void Main()
    {
        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = DefWindowProc,
            hInstance = hInstance,
            lpszClassName = "TestTrayWindowClass4",
            lpszMenuName = string.Empty
        };
        RegisterClassEx(ref wc);

        var hwnd = CreateWindowEx(0, "TestTrayWindowClass4", "TestWindow", 0, 0, 0, 0, 0, 0, 0, hInstance, 0);
        string icoPath = @"F:\MultiMonitor Project\src\WindowsSpaces.App\Assets\app.ico";
        var hIcon = LoadImage(0, icoPath, 1, 0, 0, 0x0010 | 0x0040); // IMAGE_ICON, LR_LOADFROMFILE | LR_DEFAULTSIZE
        Console.WriteLine($"HWND: {hwnd}, hIcon: {hIcon}, struct size: {Marshal.SizeOf<NOTIFYICONDATA>()}");

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = 1 | 2 | 4, // NIF_MESSAGE | NIF_ICON | NIF_TIP
            uCallbackMessage = 0x8000,
            hIcon = hIcon,
            szTip = "Windows Spaces Test",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

        for (int i = 0; i < 5; i++)
        {
            bool res = Shell_NotifyIcon(0, ref data);
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"Attempt {i+1}: returned {res}, LastError=0x{err:X8} ({err})");
            if (res)
            {
                Console.WriteLine("SUCCESS!");
                Shell_NotifyIcon(2, ref data);
                break;
            }
            System.Threading.Thread.Sleep(200);
        }
    }
}
