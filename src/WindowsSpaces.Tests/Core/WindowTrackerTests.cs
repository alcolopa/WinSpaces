using System.Drawing;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class WindowTrackerTests
{
    [Fact]
    public void HandleEvent_Created_AddsWindowFromWindowManager()
    {
        var wm = new FakeWindowManager();
        var hwnd = (nint)1;
        wm.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd,
            ProcessId = 100,
            IsVisible = true,
            NormalBounds = new Rectangle(0, 0, 100, 100),
            LastUpdated = DateTimeOffset.UtcNow
        };
        var events = new FakeWindowEventSource();
        var tracker = new WindowTracker(wm, events);

        events.Raise(new WindowEvent(WindowEventKind.Created, hwnd, DateTimeOffset.UtcNow));

        Assert.True(tracker.TrackedWindows.ContainsKey(hwnd));
    }

    [Fact]
    public void HandleEvent_Destroyed_RemovesWindow()
    {
        var wm = new FakeWindowManager();
        var hwnd = (nint)1;
        wm.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd, ProcessId = 100, IsVisible = true,
            NormalBounds = new Rectangle(0, 0, 100, 100), LastUpdated = DateTimeOffset.UtcNow
        };
        var events = new FakeWindowEventSource();
        var tracker = new WindowTracker(wm, events);
        events.Raise(new WindowEvent(WindowEventKind.Created, hwnd, DateTimeOffset.UtcNow));

        wm.Windows.Remove(hwnd);
        events.Raise(new WindowEvent(WindowEventKind.Destroyed, hwnd, DateTimeOffset.UtcNow));

        Assert.False(tracker.TrackedWindows.ContainsKey(hwnd));
    }

    [Fact]
    public void Rescan_PopulatesFromWindowManagerSnapshot()
    {
        var wm = new FakeWindowManager();
        var hwnd = (nint)7;
        wm.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd, ProcessId = 5, IsVisible = true,
            NormalBounds = new Rectangle(1, 1, 1, 1), LastUpdated = DateTimeOffset.UtcNow
        };
        var tracker = new WindowTracker(wm, new FakeWindowEventSource());

        tracker.Rescan();

        Assert.Single(tracker.TrackedWindows);
    }
}
