using System.Drawing;

namespace WindowsSpaces.Core;

public interface IWindowManager
{
    IReadOnlyList<nint> EnumerateTopLevelWindows();
    bool IsManageable(nint hwnd);
    WindowState? GetWindowState(nint hwnd);
    void Hide(nint hwnd);
    void Show(nint hwnd);
    void Move(nint hwnd, Rectangle bounds);
    void SetForeground(nint hwnd);

    /// <summary>Asks the window to close (WM_CLOSE). The app owning it may prompt or refuse; this is a request, not a kill.</summary>
    void Close(nint hwnd);
    nint GetForegroundWindow();
}
