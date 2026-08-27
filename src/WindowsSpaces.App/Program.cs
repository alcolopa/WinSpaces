using System.Runtime.InteropServices;
using WindowsSpaces.App;

internal static class Program
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_APP = 0x8000;
    private const uint WM_APP_INIT = WM_APP + 1;

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

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool InSendMessage();

    [DllImport("user32.dll")]
    private static extern bool ReplyMessage(nint lResult);

    private const uint WM_QUIT = 0x0012;
    private const uint PM_REMOVE = 0x0001;

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    public static void RequestExit()
    {
        _isExiting = true;
        PostQuitMessage(0);
    }

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
    private static App? _app;
    private static WndProc? _wndProcDelegate;

    [STAThread]
    private static void Main()
    {
        try
        {
            CrashLogger.Log("Program.Main started");
            global::WinRT.ComWrappersSupport.InitializeComWrappers();

            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                _app = new App();
                RunMessageWindowLoop();
            });
        }
        catch (Exception ex)
        {
            CrashLogger.Log("Program.Main unhandled exception", ex);
        }
    }

    private static volatile bool _isExiting;

    private static void RunMessageWindowLoop()
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

        // A message-only window (HWND_MESSAGE parent) works fine for
        // RegisterHotKey/WM_HOTKEY, but Shell_NotifyIcon's WM_APP click
        // callbacks are never delivered to one: NIM_ADD still succeeds and
        // the icon still shows, but Explorer silently drops the callback
        // because the window isn't part of the normal top-level hierarchy.
        // Microsoft's own tray-icon sample uses a real (if invisible)
        // top-level window for this reason, so we do the same: parent NULL,
        // no WS_VISIBLE style.
        var hwnd = CreateWindowEx(0, "WindowsSpacesMessageWindow", "WindowsSpaces", 0, 0, 0, 0, 0, 0, 0, hInstance, 0);
        if (hwnd == 0)
        {
            throw new InvalidOperationException($"Failed to create hidden window for hotkey/tray hosting, Win32 error {Marshal.GetLastWin32Error()}");
        }

        if (!PostMessage(hwnd, WM_APP_INIT, 0, 0))
        {
            throw new InvalidOperationException($"Failed to post startup message, Win32 error {Marshal.GetLastWin32Error()}");
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                CrashLogger.Log("AppDomain.UnhandledException (process is about to terminate)", ex);
            }
            try { _host?.ShowAllWindows(); } catch { /* best effort: process is already going down */ }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLogger.Log("TaskScheduler.UnobservedTaskException", e.Exception);
            try { _host?.ShowAllWindows(); } catch { /* best effort */ }
            e.SetObserved();
        };

        CrashLogger.Log($"RunMessageWindowLoop started, hwnd={hwnd}");

        while (!_isExiting)
        {
            var getMessageResult = GetMessage(out var msg, 0, 0, 0);
            if (getMessageResult <= 0)
            {
                if (_isExiting) break;
                // WinUI posts WM_QUIT when a XAML window closes.
                // Consume WM_QUIT from the thread queue so GetMessage doesn't loop endlessly.
                PeekMessage(out _, 0, WM_QUIT, WM_QUIT, PM_REMOVE);
                continue;
            }

            if (msg.message == WM_APP_INIT && msg.hwnd == hwnd)
            {
                CrashLogger.Log("Received WM_APP_INIT, starting AppHost...");
                _host = new AppHost();
                _host.Start(hwnd);
                CrashLogger.Log("AppHost started successfully.");

                if (_app is not null)
                {
                    _app.Host = _host;
                }
            }
            else if (msg.message == WM_HOTKEY)
            {
                _host?.HandleMessage(msg.message, msg.wParam);
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        CrashLogger.Log("RunMessageWindowLoop exited.");

        _host?.Dispose();
    }

    private static nint WndProcHandler(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // Explorer delivers Shell_NotifyIcon callbacks with SendMessage, not
        // PostMessage: measured on Windows 11, InSendMessage() is true for
        // every NIN_SELECT / WM_CONTEXTMENU / WM_?BUTTONUP the tray produces.
        // A *sent* message is handed straight to the window procedure and
        // never surfaces from GetMessage, so the tray callback has to be
        // handled here. Handling it in the message pump (as this used to)
        // silently dropped every tray click: the icon appeared, but clicking
        // it did nothing and right-click showed no menu.
        if (msg == WM_APP)
        {
            // Release Explorer's sending thread before doing anything slow:
            // the right-click handler runs a modal TrackPopupMenu loop and the
            // left-click handler builds a XAML window, and blocking the caller
            // of a SendMessage for that long hangs the taskbar.
            if (InSendMessage())
            {
                ReplyMessage(0);
            }

            _host?.HandleTrayMessage(msg, wParam, lParam);
            return 0;
        }

        if (msg == WM_DESTROY)
        {
            _host?.Dispose();
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
