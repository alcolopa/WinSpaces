using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.Core;

public class EnvironmentRestorationTests
{
    private sealed class FakeProcessManager : IProcessManager
    {
        public readonly List<(string Path, string? Args)> Launched = new();

        public void Launch(string processPath, string? arguments)
        {
            Launched.Add((processPath, arguments));
        }

        public string? GetCommandLine(int processId) => null;
    }

    [Fact]
    public void ApplyProfile_LaunchesNonRunningAppsAndEnqueuesRestoration()
    {
        var wm = new FakeWindowManager();
        var monitors = new FakeMonitorManager();
        var events = new FakeWindowEventSource();
        var guard = new OperationGuard();
        var pm = new FakeProcessManager();

        WindowTracker.TryConsumeRestorationDelegate tryConsume = null!;
        var tracker = new WindowTracker(wm, events, monitors, guard,
            tryConsumeRestoration: (string p, string c, string t, out WindowProfileState? r) => tryConsume(p, c, t, out r));

        var manager = new WorkspaceManager(wm, tracker, guard, pm);
        tryConsume = manager.TryConsumeRestoration;

        var profile = new WorkspaceProfile(
            "Work",
            new Dictionary<string, string> { { "MON-1", "MON-1:1" } },
            Windows: new[]
            {
                new WindowProfileState(
                    ProcessPath: "C:\\Windows\\notepad.exe",
                    WindowClass: "Notepad",
                    Title: "Untitled - Notepad",
                    MonitorId: "MON-1",
                    WorkspaceId: "MON-1:2",
                    IsMinimized: false,
                    IsMaximized: false,
                    NormalBounds: new Rectangle(100, 100, 500, 400),
                    CommandLineArguments: "file.txt"
                )
            }
        );

        manager.ApplyProfile(profile);

        // Verify Notepad was launched
        Assert.Single(pm.Launched);
        Assert.Equal("C:\\Windows\\notepad.exe", pm.Launched[0].Path);
        Assert.Equal("file.txt", pm.Launched[0].Args);

        // Verify window tracking intercepts newly created Notepad window
        var hwnd = (nint)12345;
        wm.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd,
            ProcessId = 999,
            ProcessPath = "C:\\Windows\\notepad.exe",
            WindowClass = "Notepad",
            Title = "Untitled - Notepad",
            IsVisible = true,
            LastUpdated = DateTimeOffset.UtcNow
        };

        events.Raise(new WindowEvent(WindowEventKind.Created, hwnd, DateTimeOffset.UtcNow));

        // Verify target workspace and geometry are applied
        Assert.True(tracker.TrackedWindows.TryGetValue(hwnd, out var state));
        Assert.Equal("MON-1", state.MonitorId);
        Assert.Equal("MON-1:2", state.WorkspaceId);
        Assert.Equal(new Rectangle(100, 100, 500, 400), state.NormalBounds);

        // Verify restoration has been consumed
        Assert.False(manager.TryConsumeRestoration("C:\\Windows\\notepad.exe", "Notepad", "Untitled - Notepad", out _));
    }

    [Fact]
    public void ApplyProfile_MovesAlreadyRunningAppWithoutLaunching()
    {
        var wm = new FakeWindowManager();
        var monitors = new FakeMonitorManager();
        var events = new FakeWindowEventSource();
        var guard = new OperationGuard();
        var pm = new FakeProcessManager();

        WindowTracker.TryConsumeRestorationDelegate tryConsume = null!;
        var tracker = new WindowTracker(wm, events, monitors, guard,
            tryConsumeRestoration: (string p, string c, string t, out WindowProfileState? r) => tryConsume(p, c, t, out r));

        var manager = new WorkspaceManager(wm, tracker, guard, pm);
        tryConsume = manager.TryConsumeRestoration;

        // Seed tracker with a running Notepad window
        var hwnd = (nint)54321;
        wm.Windows[hwnd] = new WindowState
        {
            Hwnd = hwnd,
            ProcessId = 100,
            ProcessPath = "C:\\Windows\\notepad.exe",
            WindowClass = "Notepad",
            Title = "Untitled - Notepad",
            MonitorId = "MON-1",
            WorkspaceId = "MON-1:1",
            IsVisible = true,
            NormalBounds = new Rectangle(0, 0, 100, 100),
            LastUpdated = DateTimeOffset.UtcNow
        };
        tracker.Rescan();

        var profile = new WorkspaceProfile(
            "Work",
            new Dictionary<string, string> { { "MON-1", "MON-1:1" } },
            Windows: new[]
            {
                new WindowProfileState(
                    ProcessPath: "C:\\Windows\\notepad.exe",
                    WindowClass: "Notepad",
                    Title: "Untitled - Notepad",
                    MonitorId: "MON-1",
                    WorkspaceId: "MON-1:2",
                    IsMinimized: false,
                    IsMaximized: false,
                    NormalBounds: new Rectangle(200, 200, 400, 300)
                )
            }
        );

        manager.ApplyProfile(profile);

        // Verify process was NOT launched
        Assert.Empty(pm.Launched);

        // Verify window was moved/assigned live
        Assert.True(tracker.TrackedWindows.TryGetValue(hwnd, out var state));
        Assert.Equal("MON-1:2", state.WorkspaceId);
        Assert.Equal(new Rectangle(200, 200, 400, 300), wm.Windows[hwnd].NormalBounds);
    }
}
