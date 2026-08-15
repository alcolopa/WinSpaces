using System;
using System.IO;
using System.Threading;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class AutomationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _enterFile;
    private readonly string _exitFile;
    private readonly string _profileFile;

    public AutomationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WindowsSpacesAuto_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _enterFile = Path.Combine(_tempDir, "enter.txt");
        _exitFile = Path.Combine(_tempDir, "exit.txt");
        _profileFile = Path.Combine(_tempDir, "profile.txt");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void SwitchWorkspace_TriggersEnterAndExitCommands()
    {
        var wm = new FakeWindowManager();
        var monitors = new FakeMonitorManager();
        var events = new FakeWindowEventSource();
        var guard = new OperationGuard();
        var tracker = new WindowTracker(wm, events, monitors, guard);
        var manager = new WorkspaceManager(wm, tracker, guard);

        // Define workspaces with enter/exit automation commands
        var ws1 = new WorkspaceDefinition("MON-1:1", "Space 1", 1, ExitCommand: $"echo exit_run > \"{_exitFile}\"");
        var ws2 = new WorkspaceDefinition("MON-1:2", "Space 2", 2, EnterCommand: $"echo enter_run > \"{_enterFile}\"");

        manager.SetWorkspaceDefinition(ws1);
        manager.SetWorkspaceDefinition(ws2);

        // Switch workspace from space 1 to space 2
        manager.RenameWorkspace("MON-1:1", "Space 1");
        manager.SwitchWorkspace("MON-1", "MON-1:1"); // Initial switch
        Thread.Sleep(50); // Let initial transition finish

        manager.SwitchWorkspace("MON-1", "MON-1:2");

        // Wait a short duration for the background commands to execute
        bool completed = false;
        for (int i = 0; i < 20; i++)
        {
            if (File.Exists(_exitFile) && File.Exists(_enterFile))
            {
                completed = true;
                break;
            }
            Thread.Sleep(50);
        }

        Assert.True(completed, "Automation commands did not write exit and enter files in time.");
        Assert.Equal("exit_run \r\n", File.ReadAllText(_exitFile));
        Assert.Equal("enter_run \r\n", File.ReadAllText(_enterFile));
    }

    [Fact]
    public void ApplyProfile_TriggersProfileEnterCommand()
    {
        var wm = new FakeWindowManager();
        var monitors = new FakeMonitorManager();
        var events = new FakeWindowEventSource();
        var guard = new OperationGuard();
        var tracker = new WindowTracker(wm, events, monitors, guard);
        var manager = new WorkspaceManager(wm, tracker, guard);

        var profile = new WorkspaceProfile(
            "TestProfile",
            new System.Collections.Generic.Dictionary<string, string> { { "MON-1", "MON-1:1" } },
            Windows: null,
            EnterCommand: $"echo profile_enter > \"{_profileFile}\""
        );

        manager.ApplyProfile(profile);

        // Wait a short duration for the background command to execute
        bool completed = false;
        for (int i = 0; i < 20; i++)
        {
            if (File.Exists(_profileFile))
            {
                completed = true;
                break;
            }
            Thread.Sleep(50);
        }

        Assert.True(completed, "Profile automation enter command did not write file in time.");
        Assert.Equal("profile_enter \r\n", File.ReadAllText(_profileFile));
    }
}
