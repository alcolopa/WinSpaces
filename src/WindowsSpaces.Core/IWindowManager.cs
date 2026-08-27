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
    nint GetForegroundWindow();
}
