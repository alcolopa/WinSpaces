using System.Drawing;

namespace WindowsSpaces.Core;

public interface IWindowManager
{
    IReadOnlyList<nint> EnumerateTopLevelWindows();
    bool IsManageable(nint hwnd);

    /// <summary>
    /// True if DWM is not compositing this window on screen even though Win32
    /// still reports it visible — the state background shell surfaces (Start,
    /// Search, Task View, …) settle into once dismissed, since Windows keeps
    /// their process and window alive for fast reopening rather than
    /// destroying them. Used to drop a previously-tracked window once it
    /// stops being real on-screen content, without mistaking an ordinary
    /// app-initiated hide (e.g. minimize-to-tray) for the same thing.
    /// </summary>
    bool IsCloaked(nint hwnd);
    WindowState? GetWindowState(nint hwnd);
    void Hide(nint hwnd);
    void Show(nint hwnd);
    void Move(nint hwnd, Rectangle bounds);
    void SetForeground(nint hwnd);

    /// <summary>Asks the window to close (WM_CLOSE). The app owning it may prompt or refuse; this is a request, not a kill.</summary>
    void Close(nint hwnd);
    nint GetForegroundWindow();
}
