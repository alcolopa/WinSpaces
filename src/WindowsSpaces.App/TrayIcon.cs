using System.Runtime.InteropServices;

namespace WindowsSpaces.App;

public enum TrayMenuCommand
{
    Settings,
    Shortcuts,
    Rules,
    Profiles,
    Diagnostics,
    ShowAllWindows,
    Exit
}

/// <summary>
/// Shell_NotifyIcon wrapper with a right-click context menu.
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
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
    }

    private const int NIF_MESSAGE = 0x1;
    private const int NIF_ICON = 0x2;
    private const int NIF_TIP = 0x4;
    private const int NIM_ADD = 0x0;
    private const int NIM_MODIFY = 0x1;
    private const int NIM_DELETE = 0x2;
    private const int NIM_SETVERSION = 0x4;
    private const int NOTIFYICON_VERSION_4 = 4;

    private const uint WM_APP = 0x8000;
    private const uint TrayCallbackMessage = WM_APP;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint NIN_SELECT = 0x0400;
    private const uint NIN_KEYSELECT = 0x0401;
    private const uint WM_NULL = 0x0000;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractIcon(nint hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    private const int IDI_APPLICATION = 32512;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, nint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private static readonly (TrayMenuCommand Command, string Label)[] MenuItems =
    {
        (TrayMenuCommand.Settings, "Settings..."),
        (TrayMenuCommand.Shortcuts, "Shortcuts..."),
        (TrayMenuCommand.Rules, "App Rules..."),
        (TrayMenuCommand.Profiles, "Profiles..."),
        (TrayMenuCommand.Diagnostics, "Diagnostics..."),
        (TrayMenuCommand.ShowAllWindows, "Show All Windows"),
        (TrayMenuCommand.Exit, "Exit")
    };

    private readonly nint _hwnd;
    private NOTIFYICONDATA _data;
    private bool _added;
    private nint _hIcon;
    private bool _suppressNextClick;

    public event EventHandler<TrayMenuCommand>? MenuItemInvoked;
    public event EventHandler? DoubleClicked;

    public TrayIcon(nint hwnd)
    {
        _hwnd = hwnd;

        var hInst = GetModuleHandle(null);
        var exePath = Environment.ProcessPath ?? string.Empty;
        _hIcon = ExtractIcon(hInst, exePath, 0);
        if (_hIcon == 1) _hIcon = 0;

        if (_hIcon == 0)
        {
            var assetIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(assetIconPath))
            {
                _hIcon = LoadImage(0, assetIconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            }
        }

        if (_hIcon == 0)
        {
            _hIcon = LoadIcon(0, (nint)IDI_APPLICATION);
        }

        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_TIP | (_hIcon != 0 ? NIF_ICON : 0),
            uCallbackMessage = (int)TrayCallbackMessage,
            hIcon = _hIcon,
            szTip = "Windows Spaces"
        };
    }

    public void Show()
    {
        // Clear any stale icon registration from a previous crash/run
        Shell_NotifyIcon(NIM_DELETE, ref _data);

        _added = Shell_NotifyIcon(NIM_ADD, ref _data);
        if (_added)
        {
            _data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref _data);
        }
        else
        {
            CrashLogger.Log($"TrayIcon.Show: Shell_NotifyIcon(NIM_ADD) failed, hwnd={_hwnd}, hIcon={_hIcon}, Win32 error {Marshal.GetLastWin32Error()}");
            _added = Shell_NotifyIcon(NIM_MODIFY, ref _data);
        }
    }

    public void SetTooltip(string text)
    {
        _data.szTip = text;
        Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    /// <summary>Call from the App's message loop for every received message.</summary>
    public void HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (message != TrayCallbackMessage) return;

        // Shell_NotifyIcon packs the mouse message in LOWORD(lParam), and may place the icon ID in HIWORD(lParam).
        var mouseMessage = (uint)((long)lParam & 0xFFFF);
        if (mouseMessage == WM_LBUTTONDBLCLK)
        {
            // Windows sends LBUTTONUP, then LBUTTONDBLCLK, then a second
            // LBUTTONUP for a double-click — suppress that trailing LBUTTONUP
            // so a double-click doesn't also fire the single-click action.
            _suppressNextClick = true;
            DoubleClicked?.Invoke(this, EventArgs.Empty);
            MenuItemInvoked?.Invoke(this, TrayMenuCommand.Settings);
            return;
        }

        // Single left-click opens the Settings window.
        if (mouseMessage == WM_LBUTTONUP || mouseMessage == NIN_SELECT)
        {
            if (_suppressNextClick)
            {
                _suppressNextClick = false;
                return;
            }
            MenuItemInvoked?.Invoke(this, TrayMenuCommand.Settings);
            return;
        }

        // Right-click or context menu triggers the popup menu.
        if (mouseMessage == WM_RBUTTONUP || mouseMessage == WM_CONTEXTMENU || mouseMessage == NIN_KEYSELECT)
        {
            ShowContextMenuAndInvoke();
        }
    }

    private void ShowContextMenuAndInvoke()
    {
        var hMenu = CreatePopupMenu();
        try
        {
            for (var i = 0; i < MenuItems.Length; i++)
            {
                AppendMenu(hMenu, 0, (nint)(i + 1), MenuItems[i].Label);
            }

            GetCursorPos(out var cursor);
            SetForegroundWindow(_hwnd);
            var selectedId = TrackPopupMenu(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD, cursor.X, cursor.Y, 0, _hwnd, 0);

            // Documented tray-icon idiom (MSDN sample): the owner window needs a
            // message posted to it right after TrackPopupMenu for the menu to
            // dismiss properly when the user clicks away without picking an item.
            // Especially needed here because _hwnd is a hidden window, which
            // SetForegroundWindow can never actually bring to the foreground.
            PostMessage(_hwnd, WM_NULL, 0, 0);

            if (selectedId > 0)
            {
                MenuItemInvoked?.Invoke(this, MenuItems[selectedId - 1].Command);
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    public void Dispose()
    {
        Shell_NotifyIcon(NIM_DELETE, ref _data);
        if (_hIcon != 0 && _hIcon != 1)
        {
            DestroyIcon(_hIcon);
            _hIcon = 0;
        }
    }
}
