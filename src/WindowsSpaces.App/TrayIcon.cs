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
/// Shell_NotifyIcon wrapper with a right-click context menu. This is UI
/// chrome (not window management), so it is intentionally kept local to
/// the App project rather than routed through Core/Platform.
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

    private const uint WM_APP = 0x8000;
    private const uint TrayCallbackMessage = WM_APP;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_NULL = 0x0000;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractIcon(nint hInst, string lpszExeFileName, int nIconIndex);

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
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private static readonly (TrayMenuCommand Command, string Label)[] MenuItems =
    {
        (TrayMenuCommand.Settings, "Settings"),
        (TrayMenuCommand.Shortcuts, "Shortcuts"),
        (TrayMenuCommand.Rules, "Rules"),
        (TrayMenuCommand.Profiles, "Profiles"),
        (TrayMenuCommand.Diagnostics, "Diagnostics"),
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
        _added = Shell_NotifyIcon(NIM_ADD, ref _data);
    }

    public void SetTooltip(string text)
    {
        _data.szTip = text;
        if (_added) Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    /// <summary>Call from the App's message loop for every received message.</summary>
    public void HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (message != TrayCallbackMessage) return;

        var mouseMessage = (uint)lParam;
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

        // A plain single click is the click most users make on a tray icon —
        // without this, clicking it did nothing at all and only the
        // easy-to-miss double-click or right-click-menu opened anything.
        if (mouseMessage == WM_LBUTTONUP)
        {
            if (_suppressNextClick)
            {
                _suppressNextClick = false;
                return;
            }
            MenuItemInvoked?.Invoke(this, TrayMenuCommand.Shortcuts);
            return;
        }

        if (mouseMessage == WM_RBUTTONUP)
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
            // Especially needed here because _hwnd is a message-only window, which
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
        if (_added) Shell_NotifyIcon(NIM_DELETE, ref _data);
        if (_hIcon != 0)
        {
            DestroyIcon(_hIcon);
            _hIcon = 0;
        }
    }
}
