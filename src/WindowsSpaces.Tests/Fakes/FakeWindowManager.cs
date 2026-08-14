using System.Drawing;
using WindowsSpaces.Core;

namespace WindowsSpaces.Tests.Fakes;

public sealed class FakeWindowManager : IWindowManager
{
    public Dictionary<nint, WindowState> Windows { get; } = new();
    public List<(nint Hwnd, string Op)> Operations { get; } = new();
    public nint Foreground { get; set; }

    public IReadOnlyList<nint> EnumerateTopLevelWindows() => Windows.Keys.ToList();

    public WindowState? GetWindowState(nint hwnd) => Windows.GetValueOrDefault(hwnd);

    public void Hide(nint hwnd)
    {
        Operations.Add((hwnd, "Hide"));
        if (Windows.TryGetValue(hwnd, out var s)) s.IsVisible = false;
    }

    public void Show(nint hwnd)
    {
        Operations.Add((hwnd, "Show"));
        if (Windows.TryGetValue(hwnd, out var s)) s.IsVisible = true;
    }

    public void Move(nint hwnd, Rectangle bounds)
    {
        Operations.Add((hwnd, "Move"));
        if (Windows.TryGetValue(hwnd, out var s)) s.NormalBounds = bounds;
    }

    public void SetForeground(nint hwnd)
    {
        Operations.Add((hwnd, "SetForeground"));
        Foreground = hwnd;
    }

    public nint GetForegroundWindow() => Foreground;
}
