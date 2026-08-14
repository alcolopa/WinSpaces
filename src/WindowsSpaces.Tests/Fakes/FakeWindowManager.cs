using System.Collections.Concurrent;
using System.Drawing;
using System.Threading;
using WindowsSpaces.Core;

namespace WindowsSpaces.Tests.Fakes;

public sealed class FakeWindowManager : IWindowManager
{
    public Dictionary<nint, WindowState> Windows { get; } = new();
    public ConcurrentBag<(nint Hwnd, string Op)> Operations { get; } = new();
    public nint Foreground { get; set; }

    /// <summary>
    /// When set, Hide() signals HideEntered and blocks until this gate is
    /// released — used to simulate an in-flight transition so tests can
    /// prove the latest-request-wins queue actually collapses concurrent
    /// requests instead of executing every one.
    /// </summary>
    public ManualResetEventSlim? HideGate { get; set; }
    public ManualResetEventSlim HideEntered { get; } = new(false);

    public IReadOnlyList<nint> EnumerateTopLevelWindows() => Windows.Keys.ToList();

    public WindowState? GetWindowState(nint hwnd) => Windows.GetValueOrDefault(hwnd);

    public void Hide(nint hwnd)
    {
        HideEntered.Set();
        HideGate?.Wait();
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
