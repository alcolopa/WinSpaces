# Windows Spaces — Phase 0 Technical Spike Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove the independent-per-monitor workspace mechanism works reliably on Windows 11, per `docs/superpowers/specs/2026-08-14-phase0-spike-design.md`.

**Architecture:** Core (no Win32 refs) → Platform (Win32 P/Invoke adapter implementing Core interfaces) → App (minimal WinUI 3 shell wiring hotkeys + tray). A separate TestApp spawns deterministic windows for manual/integration testing.

**Tech Stack:** C#, .NET 8, WinUI 3 / Windows App SDK for the App project, plain P/Invoke (`user32.dll`) for Platform, WinForms for TestApp (simplest way to spawn deterministic native windows), xUnit for tests.

## Global Constraints

- Target: Windows 11 24H2+, .NET 8.
- Core project (`WindowsSpaces.Core`) MUST NOT reference Win32 or any Windows-specific API — verified by having zero `using System.Runtime.InteropServices` / no `net8.0-windows` TFM on that project.
- Never manipulate windows directly from UI code — `WindowsSpaces.App` calls into `WorkspaceManager`/`HotkeyManager`, never raw `user32.dll` window functions itself.
- Never silently swallow Win32 errors — P/Invoke wrappers check return values and throw or log with `Marshal.GetLastWin32Error()`.
- Never use monitor array index as stable identity — `Monitor.Id` is derived from `MONITORINFOEX.szDevice`, not enumeration order.
- Never block the UI thread — WinEvent callbacks enqueue only; a background loop drains the queue.
- No persistence, no crash-recovery markers, no rule engine, no settings UI in this phase (see design doc "Out of scope").
- Run tests after every task; commit after every task.

---

### Task 1: Solution and project scaffold

**Files:**
- Create: `WindowsSpaces.sln`
- Create: `src/WindowsSpaces.Core/WindowsSpaces.Core.csproj`
- Create: `src/WindowsSpaces.Platform/WindowsSpaces.Platform.csproj`
- Create: `src/WindowsSpaces.App/WindowsSpaces.App.csproj`
- Create: `src/WindowsSpaces.TestApp/WindowsSpaces.TestApp.csproj`
- Create: `src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj`
- Create: `.gitignore`

**Interfaces:**
- Produces: a buildable empty solution with 5 projects and correct project references (`Platform` → `Core`; `App` → `Core`, `Platform`; `TestApp` → none; `Tests` → `Core`, `Platform`).

- [ ] **Step 1: Create `.gitignore`**

```
bin/
obj/
*.user
.vs/
```

- [ ] **Step 2: Create the Core class library**

```bash
mkdir -p src/WindowsSpaces.Core
dotnet new classlib -n WindowsSpaces.Core -o src/WindowsSpaces.Core -f net8.0
```

Edit `src/WindowsSpaces.Core/WindowsSpaces.Core.csproj` to ensure it reads exactly:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>WindowsSpaces.Core</RootNamespace>
  </PropertyGroup>

</Project>
```

Delete the generated `Class1.cs`.

- [ ] **Step 3: Create the Platform class library**

```bash
dotnet new classlib -n WindowsSpaces.Platform -o src/WindowsSpaces.Platform -f net8.0-windows
dotnet add src/WindowsSpaces.Platform/WindowsSpaces.Platform.csproj reference src/WindowsSpaces.Core/WindowsSpaces.Core.csproj
```

Edit `src/WindowsSpaces.Platform/WindowsSpaces.Platform.csproj` to ensure it reads exactly:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>WindowsSpaces.Platform</RootNamespace>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\WindowsSpaces.Core\WindowsSpaces.Core.csproj" />
  </ItemGroup>

</Project>
```

Delete the generated `Class1.cs`.

- [ ] **Step 4: Create the TestApp**

```bash
dotnet new winforms -n WindowsSpaces.TestApp -o src/WindowsSpaces.TestApp -f net8.0-windows
```

Leave the generated `Form1.cs`/`Program.cs` in place; Task 10 rewrites them.

- [ ] **Step 5: Create the App (WinUI 3 console-hosted shell)**

WinUI 3 project templates require Visual Studio workload templates that may not be installed via `dotnet new`. To avoid that dependency, scaffold `WindowsSpaces.App` as a `net8.0-windows` WinExe project referencing the Windows App SDK NuGet packages directly:

```bash
dotnet new console -n WindowsSpaces.App -o src/WindowsSpaces.App -f net8.0-windows
dotnet add src/WindowsSpaces.App/WindowsSpaces.App.csproj reference src/WindowsSpaces.Core/WindowsSpaces.Core.csproj
dotnet add src/WindowsSpaces.App/WindowsSpaces.App.csproj reference src/WindowsSpaces.Platform/WindowsSpaces.Platform.csproj
dotnet add src/WindowsSpaces.App/WindowsSpaces.App.csproj package Microsoft.WindowsAppSDK --version 1.6.250108002
dotnet add src/WindowsSpaces.App/WindowsSpaces.App.csproj package Microsoft.Windows.SDK.BuildTools --version 10.0.26100.1742
```

Edit `src/WindowsSpaces.App/WindowsSpaces.App.csproj` to ensure it reads exactly:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>WindowsSpaces.App</RootNamespace>
    <UseWinUI>true</UseWinUI>
    <WindowsAppSDKSelfContained>false</WindowsAppSDKSelfContained>
    <Platforms>x64</Platforms>
    <RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.6.250108002" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.1742" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\WindowsSpaces.Core\WindowsSpaces.Core.csproj" />
    <ProjectReference Include="..\WindowsSpaces.Platform\WindowsSpaces.Platform.csproj" />
  </ItemGroup>

</Project>
```

Delete the generated `Program.cs`; Task 11 rewrites it.

- [ ] **Step 6: Create the Tests project**

```bash
dotnet new xunit -n WindowsSpaces.Tests -o src/WindowsSpaces.Tests -f net8.0
dotnet add src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj reference src/WindowsSpaces.Core/WindowsSpaces.Core.csproj
dotnet add src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj reference src/WindowsSpaces.Platform/WindowsSpaces.Platform.csproj
```

Delete the generated `UnitTest1.cs`.

- [ ] **Step 7: Create the solution file and add all projects**

```bash
dotnet new sln -n WindowsSpaces
dotnet sln add src/WindowsSpaces.Core/WindowsSpaces.Core.csproj
dotnet sln add src/WindowsSpaces.Platform/WindowsSpaces.Platform.csproj
dotnet sln add src/WindowsSpaces.App/WindowsSpaces.App.csproj
dotnet sln add src/WindowsSpaces.TestApp/WindowsSpaces.TestApp.csproj
dotnet sln add src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj
```

- [ ] **Step 8: Build to verify the scaffold compiles**

Run: `dotnet build WindowsSpaces.sln`
Expected: `Build succeeded.` with 0 errors (warnings about missing `Program.cs`/`Main` in App are expected and will be resolved in Task 11 — if build fails hard on App, temporarily add a placeholder `Program.cs` with `System.Console.WriteLine("placeholder");` so the solution builds; Task 11 will replace it).

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "Scaffold WindowsSpaces solution with Core, Platform, App, TestApp, Tests projects"
```

---

### Task 2: Core domain models

**Files:**
- Create: `src/WindowsSpaces.Core/Monitor.cs`
- Create: `src/WindowsSpaces.Core/Workspace.cs`
- Create: `src/WindowsSpaces.Core/WindowState.cs`
- Test: `src/WindowsSpaces.Tests/Core/DomainModelTests.cs`

**Interfaces:**
- Produces: `Monitor` (record), `Workspace` (record), `WindowState` (class, mutable) — used by every later Core task.

- [ ] **Step 1: Write the failing tests**

```csharp
// src/WindowsSpaces.Tests/Core/DomainModelTests.cs
using WindowsSpaces.Core;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class DomainModelTests
{
    [Fact]
    public void Monitor_WithSameId_AreEqual()
    {
        var a = new Monitor("MON-1", "\\\\.\\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), IsPrimary: true);
        var b = new Monitor("MON-1", "\\\\.\\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), IsPrimary: true);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Workspace_HasMonitorScopedIdentity()
    {
        var ws = new Workspace(Id: "MON-1:1", MonitorId: "MON-1", Name: "Development", Index: 0);

        Assert.Equal("MON-1", ws.MonitorId);
        Assert.Equal("MON-1:1", ws.Id);
    }

    [Fact]
    public void WindowState_TracksAssignmentAndBounds()
    {
        var state = new WindowState
        {
            Hwnd = (nint)12345,
            ProcessId = 999,
            MonitorId = "MON-1",
            WorkspaceId = "MON-1:1",
            IsVisible = true,
            IsMinimized = false,
            IsMaximized = false,
            NormalBounds = new System.Drawing.Rectangle(10, 10, 800, 600),
            LastUpdated = System.DateTimeOffset.UtcNow
        };

        Assert.Equal((nint)12345, state.Hwnd);
        Assert.Equal("MON-1:1", state.WorkspaceId);
        Assert.True(state.IsVisible);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter DomainModelTests`
Expected: FAIL — `Monitor`, `Workspace`, `WindowState` do not exist.

- [ ] **Step 3: Implement `Monitor.cs`**

```csharp
// src/WindowsSpaces.Core/Monitor.cs
using System.Drawing;

namespace WindowsSpaces.Core;

/// <summary>
/// Stable monitor identity. Id must be derived from device path/EDID data,
/// never from enumeration order, since order is not stable across reboots or reconnects.
/// </summary>
public sealed record Monitor(
    string Id,
    string DevicePath,
    Rectangle Bounds,
    bool IsPrimary);
```

- [ ] **Step 4: Implement `Workspace.cs`**

```csharp
// src/WindowsSpaces.Core/Workspace.cs
namespace WindowsSpaces.Core;

/// <summary>
/// A logical workspace scoped to a single monitor. Id is "{MonitorId}:{Index}".
/// </summary>
public sealed record Workspace(
    string Id,
    string MonitorId,
    string Name,
    int Index);
```

- [ ] **Step 5: Implement `WindowState.cs`**

```csharp
// src/WindowsSpaces.Core/WindowState.cs
using System.Drawing;

namespace WindowsSpaces.Core;

/// <summary>
/// Mutable runtime state for one tracked top-level window.
/// </summary>
public sealed class WindowState
{
    public required nint Hwnd { get; init; }
    public required int ProcessId { get; init; }
    public string? MonitorId { get; set; }
    public string? WorkspaceId { get; set; }
    public bool IsVisible { get; set; }
    public bool IsMinimized { get; set; }
    public bool IsMaximized { get; set; }
    public Rectangle NormalBounds { get; set; }
    public required DateTimeOffset LastUpdated { get; set; }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter DomainModelTests`
Expected: PASS (3 tests)

- [ ] **Step 7: Commit**

```bash
git add src/WindowsSpaces.Core/Monitor.cs src/WindowsSpaces.Core/Workspace.cs src/WindowsSpaces.Core/WindowState.cs src/WindowsSpaces.Tests/Core/DomainModelTests.cs
git commit -m "Add Core domain models: Monitor, Workspace, WindowState"
```

---

### Task 3: Core platform-abstraction interfaces

**Files:**
- Create: `src/WindowsSpaces.Core/IMonitorManager.cs`
- Create: `src/WindowsSpaces.Core/IWindowManager.cs`
- Create: `src/WindowsSpaces.Core/IWindowEventSource.cs`
- Create: `src/WindowsSpaces.Core/IHotkeyManager.cs`
- Create: `src/WindowsSpaces.Core/WindowEvent.cs`
- Test: none (interfaces only; exercised by later tests via fakes)

**Interfaces:**
- Consumes: `Monitor`, `WindowState` from Task 2.
- Produces: `IMonitorManager`, `IWindowManager`, `IWindowEventSource`, `IHotkeyManager`, `WindowEvent`, `WindowEventKind` — implemented by Platform (Tasks 6-9) and faked by Tests (Task 4-5).

- [ ] **Step 1: Implement `WindowEvent.cs`**

```csharp
// src/WindowsSpaces.Core/WindowEvent.cs
namespace WindowsSpaces.Core;

public enum WindowEventKind
{
    Created,
    Destroyed,
    Shown,
    Hidden,
    LocationChanged,
    ForegroundChanged
}

public readonly record struct WindowEvent(WindowEventKind Kind, nint Hwnd, DateTimeOffset Timestamp);
```

- [ ] **Step 2: Implement `IMonitorManager.cs`**

```csharp
// src/WindowsSpaces.Core/IMonitorManager.cs
namespace WindowsSpaces.Core;

public interface IMonitorManager
{
    IReadOnlyList<Monitor> GetMonitors();
    Monitor? GetMonitorForWindow(nint hwnd);
    event EventHandler? MonitorsChanged;
}
```

- [ ] **Step 3: Implement `IWindowManager.cs`**

```csharp
// src/WindowsSpaces.Core/IWindowManager.cs
using System.Drawing;

namespace WindowsSpaces.Core;

public interface IWindowManager
{
    IReadOnlyList<nint> EnumerateTopLevelWindows();
    WindowState? GetWindowState(nint hwnd);
    void Hide(nint hwnd);
    void Show(nint hwnd);
    void Move(nint hwnd, Rectangle bounds);
    void SetForeground(nint hwnd);
    nint GetForegroundWindow();
}
```

- [ ] **Step 4: Implement `IWindowEventSource.cs`**

```csharp
// src/WindowsSpaces.Core/IWindowEventSource.cs
namespace WindowsSpaces.Core;

public interface IWindowEventSource
{
    event EventHandler<WindowEvent>? WindowEvent;
    void Start();
    void Stop();
}
```

- [ ] **Step 5: Implement `IHotkeyManager.cs`**

```csharp
// src/WindowsSpaces.Core/IHotkeyManager.cs
namespace WindowsSpaces.Core;

public enum ModifierKeys
{
    Control = 0x2,
    Alt = 0x1,
    Shift = 0x4
}

public interface IHotkeyManager
{
    void Register(int id, ModifierKeys modifiers, int virtualKey, Action callback);
    void Unregister(int id);
}
```

- [ ] **Step 6: Build to verify it compiles**

Run: `dotnet build src/WindowsSpaces.Core/WindowsSpaces.Core.csproj`
Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```bash
git add src/WindowsSpaces.Core/IMonitorManager.cs src/WindowsSpaces.Core/IWindowManager.cs src/WindowsSpaces.Core/IWindowEventSource.cs src/WindowsSpaces.Core/IHotkeyManager.cs src/WindowsSpaces.Core/WindowEvent.cs
git commit -m "Add Core platform-abstraction interfaces"
```

---

### Task 4: WindowTracker

**Files:**
- Create: `src/WindowsSpaces.Core/WindowTracker.cs`
- Test: `src/WindowsSpaces.Tests/Core/WindowTrackerTests.cs`
- Test fake: `src/WindowsSpaces.Tests/Fakes/FakeWindowManager.cs`
- Test fake: `src/WindowsSpaces.Tests/Fakes/FakeWindowEventSource.cs`

**Interfaces:**
- Consumes: `IWindowManager`, `IWindowEventSource`, `WindowEvent`, `WindowState` from Tasks 2-3.
- Produces: `WindowTracker` with `IReadOnlyDictionary<nint, WindowState> TrackedWindows { get; }`, `void HandleEvent(WindowEvent evt)`, `void Rescan()` — consumed by `WorkspaceManager` in Task 5.

- [ ] **Step 1: Write the fakes**

```csharp
// src/WindowsSpaces.Tests/Fakes/FakeWindowManager.cs
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
```

```csharp
// src/WindowsSpaces.Tests/Fakes/FakeWindowEventSource.cs
using WindowsSpaces.Core;

namespace WindowsSpaces.Tests.Fakes;

public sealed class FakeWindowEventSource : IWindowEventSource
{
    public event EventHandler<WindowEvent>? WindowEvent;
    public bool Started { get; private set; }

    public void Start() => Started = true;
    public void Stop() => Started = false;

    public void Raise(WindowEvent evt) => WindowEvent?.Invoke(this, evt);
}
```

- [ ] **Step 2: Write the failing tests**

```csharp
// src/WindowsSpaces.Tests/Core/WindowTrackerTests.cs
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
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter WindowTrackerTests`
Expected: FAIL — `WindowTracker` does not exist.

- [ ] **Step 4: Implement `WindowTracker.cs`**

```csharp
// src/WindowsSpaces.Core/WindowTracker.cs
using System.Collections.Concurrent;

namespace WindowsSpaces.Core;

/// <summary>
/// Maintains the live WindowState collection from a stream of WindowEvents.
/// Event handling is synchronous and cheap by design — callers on Win32
/// WinEvent hooks must enqueue events elsewhere and dispatch them here
/// off the hook callback thread.
/// </summary>
public sealed class WindowTracker
{
    private readonly IWindowManager _windowManager;
    private readonly ConcurrentDictionary<nint, WindowState> _tracked = new();

    public WindowTracker(IWindowManager windowManager, IWindowEventSource eventSource)
    {
        _windowManager = windowManager;
        eventSource.WindowEvent += (_, evt) => HandleEvent(evt);
    }

    public IReadOnlyDictionary<nint, WindowState> TrackedWindows => _tracked;

    public void Rescan()
    {
        _tracked.Clear();
        foreach (var hwnd in _windowManager.EnumerateTopLevelWindows())
        {
            var state = _windowManager.GetWindowState(hwnd);
            if (state is not null)
            {
                _tracked[hwnd] = state;
            }
        }
    }

    public void HandleEvent(WindowEvent evt)
    {
        switch (evt.Kind)
        {
            case WindowEventKind.Destroyed:
                _tracked.TryRemove(evt.Hwnd, out _);
                break;

            case WindowEventKind.Created:
            case WindowEventKind.Shown:
            case WindowEventKind.Hidden:
            case WindowEventKind.LocationChanged:
            case WindowEventKind.ForegroundChanged:
                var state = _windowManager.GetWindowState(evt.Hwnd);
                if (state is not null)
                {
                    _tracked[evt.Hwnd] = state;
                }
                break;
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter WindowTrackerTests`
Expected: PASS (3 tests)

- [ ] **Step 6: Commit**

```bash
git add src/WindowsSpaces.Core/WindowTracker.cs src/WindowsSpaces.Tests/Core/WindowTrackerTests.cs src/WindowsSpaces.Tests/Fakes/FakeWindowManager.cs src/WindowsSpaces.Tests/Fakes/FakeWindowEventSource.cs
git commit -m "Add WindowTracker with event-driven state maintenance"
```

---

### Task 5: WorkspaceManager — switching algorithm, transition queue, operation guard

**Files:**
- Create: `src/WindowsSpaces.Core/WorkspaceManager.cs`
- Create: `src/WindowsSpaces.Core/OperationGuard.cs`
- Test: `src/WindowsSpaces.Tests/Core/WorkspaceManagerTests.cs`
- Modify: `src/WindowsSpaces.Tests/Fakes/FakeWindowManager.cs` (add helper for asserting operation order — already exposes `Operations`, no change needed)

**Interfaces:**
- Consumes: `IWindowManager`, `IMonitorManager`, `WindowTracker`, `Workspace`, `WindowState` from Tasks 2-4.
- Produces: `WorkspaceManager` with `void SwitchWorkspace(string monitorId, string workspaceId)`, `void AssignWindow(nint hwnd, string workspaceId)`, `void ShowAllWindows()`, `string? GetActiveWorkspace(string monitorId)` — consumed by `WindowsSpaces.App` in Task 11.

This is the core proof of the spike: independent per-monitor switching, latest-request-wins queuing, and the operation guard.

- [ ] **Step 1: Write the failing tests**

```csharp
// src/WindowsSpaces.Tests/Core/WorkspaceManagerTests.cs
using System.Drawing;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class WorkspaceManagerTests
{
    private static (WorkspaceManager mgr, FakeWindowManager wm, WindowTracker tracker) Build(
        params (nint hwnd, string monitorId, string workspaceId)[] windows)
    {
        var wm = new FakeWindowManager();
        foreach (var (hwnd, monitorId, workspaceId) in windows)
        {
            wm.Windows[hwnd] = new WindowState
            {
                Hwnd = hwnd,
                ProcessId = 1,
                MonitorId = monitorId,
                WorkspaceId = workspaceId,
                IsVisible = true,
                NormalBounds = new Rectangle(0, 0, 100, 100),
                LastUpdated = DateTimeOffset.UtcNow
            };
        }
        var events = new FakeWindowEventSource();
        var tracker = new WindowTracker(wm, events);
        tracker.Rescan();
        var mgr = new WorkspaceManager(wm, tracker);
        return (mgr, wm, tracker);
    }

    [Fact]
    public void SwitchWorkspace_OnMonitorA_DoesNotAffectMonitorB()
    {
        var hwndA1 = (nint)1;
        var hwndB1 = (nint)2;
        var (mgr, wm, _) = Build(
            (hwndA1, "MON-A", "MON-A:1"),
            (hwndB1, "MON-B", "MON-B:1"));

        mgr.SwitchWorkspace("MON-A", "MON-A:2");

        Assert.Equal("MON-A:2", mgr.GetActiveWorkspace("MON-A"));
        Assert.Equal("MON-B:1", mgr.GetActiveWorkspace("MON-B"));
        Assert.DoesNotContain(wm.Operations, op => op.Hwnd == hwndB1);
    }

    [Fact]
    public void SwitchWorkspace_HidesWindowsNotInTargetWorkspace_ShowsWindowsInTargetWorkspace()
    {
        var hwndDev = (nint)1;
        var hwndResearch = (nint)2;
        var (mgr, wm, _) = Build(
            (hwndDev, "MON-A", "MON-A:1"),
            (hwndResearch, "MON-A", "MON-A:2"));

        mgr.SwitchWorkspace("MON-A", "MON-A:2");

        Assert.Contains(wm.Operations, op => op.Hwnd == hwndDev && op.Op == "Hide");
        Assert.Contains(wm.Operations, op => op.Hwnd == hwndResearch && op.Op == "Show");
    }

    [Fact]
    public void RapidSwitching_CollapsesToLatestTarget()
    {
        var (mgr, wm, _) = Build();

        mgr.SwitchWorkspace("MON-A", "MON-A:2");
        mgr.SwitchWorkspace("MON-A", "MON-A:3");
        mgr.SwitchWorkspace("MON-A", "MON-A:2");

        Assert.Equal("MON-A:2", mgr.GetActiveWorkspace("MON-A"));
    }

    [Fact]
    public void AssignWindow_MovesWindowToNewWorkspace()
    {
        var hwnd = (nint)1;
        var (mgr, wm, _) = Build((hwnd, "MON-A", "MON-A:1"));

        mgr.AssignWindow(hwnd, "MON-A:2");

        Assert.Equal("MON-A:2", wm.Windows[hwnd].WorkspaceId);
    }

    [Fact]
    public void ShowAllWindows_ShowsEveryTrackedWindow_RegardlessOfWorkspace()
    {
        var hwnd1 = (nint)1;
        var hwnd2 = (nint)2;
        var (mgr, wm, _) = Build(
            (hwnd1, "MON-A", "MON-A:1"),
            (hwnd2, "MON-A", "MON-A:2"));
        mgr.SwitchWorkspace("MON-A", "MON-A:1");
        wm.Operations.Clear();

        mgr.ShowAllWindows();

        Assert.Contains(wm.Operations, op => op.Hwnd == hwnd1 && op.Op == "Show");
        Assert.Contains(wm.Operations, op => op.Hwnd == hwnd2 && op.Op == "Show");
    }

    [Fact]
    public void OperationGuard_SuppressesReentrantEventsDuringTransition()
    {
        var guard = new OperationGuard();
        Assert.False(guard.IsSuppressed((nint)1));

        using (guard.Suppress((nint)1))
        {
            Assert.True(guard.IsSuppressed((nint)1));
        }

        Assert.False(guard.IsSuppressed((nint)1));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter WorkspaceManagerTests`
Expected: FAIL — `WorkspaceManager`, `OperationGuard` do not exist.

- [ ] **Step 3: Implement `OperationGuard.cs`**

```csharp
// src/WindowsSpaces.Core/OperationGuard.cs
using System.Collections.Concurrent;

namespace WindowsSpaces.Core;

/// <summary>
/// Marks hwnds as "we just moved/hid/showed this ourselves" so the event
/// pipeline can distinguish our own SetWindowPos-driven notifications from
/// independent user actions and avoid feedback loops.
/// </summary>
public sealed class OperationGuard
{
    private readonly ConcurrentDictionary<nint, byte> _suppressed = new();

    public bool IsSuppressed(nint hwnd) => _suppressed.ContainsKey(hwnd);

    public IDisposable Suppress(nint hwnd)
    {
        _suppressed[hwnd] = 0;
        return new Scope(this, hwnd);
    }

    private sealed class Scope : IDisposable
    {
        private readonly OperationGuard _owner;
        private readonly nint _hwnd;
        public Scope(OperationGuard owner, nint hwnd) { _owner = owner; _hwnd = hwnd; }
        public void Dispose() => _owner._suppressed.TryRemove(_hwnd, out _);
    }
}
```

- [ ] **Step 4: Implement `WorkspaceManager.cs`**

```csharp
// src/WindowsSpaces.Core/WorkspaceManager.cs
using System.Collections.Concurrent;

namespace WindowsSpaces.Core;

/// <summary>
/// Owns per-monitor active-workspace state and the hide/show/move switching
/// algorithm. Each monitor has its own transition lock; a rapid sequence of
/// switch requests for the same monitor collapses to the latest target
/// ("latest request wins") rather than executing every intermediate step.
/// </summary>
public sealed class WorkspaceManager
{
    private readonly IWindowManager _windowManager;
    private readonly WindowTracker _tracker;
    private readonly OperationGuard _guard = new();
    private readonly ConcurrentDictionary<string, string> _activeWorkspaceByMonitor = new();
    private readonly ConcurrentDictionary<string, object> _monitorLocks = new();

    public WorkspaceManager(IWindowManager windowManager, WindowTracker tracker)
    {
        _windowManager = windowManager;
        _tracker = tracker;
    }

    public string? GetActiveWorkspace(string monitorId) =>
        _activeWorkspaceByMonitor.GetValueOrDefault(monitorId);

    /// <summary>
    /// Switches the given monitor to the target workspace. Safe to call
    /// rapidly and repeatedly for the same monitor: only the latest call's
    /// target is honored once the lock for that monitor is acquired.
    /// </summary>
    public void SwitchWorkspace(string monitorId, string targetWorkspaceId)
    {
        var monitorLock = _monitorLocks.GetOrAdd(monitorId, _ => new object());

        lock (monitorLock)
        {
            // Re-check: if a queued caller already moved us to this target
            // (or past it) under the lock, there is nothing left to do.
            if (_activeWorkspaceByMonitor.TryGetValue(monitorId, out var currentBeforeWait) &&
                currentBeforeWait == targetWorkspaceId)
            {
                return;
            }

            var windowsOnMonitor = _tracker.TrackedWindows.Values
                .Where(w => w.MonitorId == monitorId)
                .ToList();

            // Hide everything on this monitor not in the target workspace.
            foreach (var window in windowsOnMonitor.Where(w => w.WorkspaceId != targetWorkspaceId))
            {
                using (_guard.Suppress(window.Hwnd))
                {
                    _windowManager.Hide(window.Hwnd);
                }
            }

            // Show everything in the target workspace on this monitor.
            foreach (var window in windowsOnMonitor.Where(w => w.WorkspaceId == targetWorkspaceId))
            {
                using (_guard.Suppress(window.Hwnd))
                {
                    _windowManager.Move(window.Hwnd, window.NormalBounds);
                    _windowManager.Show(window.Hwnd);
                }
            }

            _activeWorkspaceByMonitor[monitorId] = targetWorkspaceId;
        }
    }

    /// <summary>
    /// Reassigns a window to a different workspace. If the window's monitor
    /// is currently showing that workspace, the window becomes visible;
    /// otherwise it is hidden until that workspace becomes active.
    /// </summary>
    public void AssignWindow(nint hwnd, string targetWorkspaceId)
    {
        var state = _windowManager.GetWindowState(hwnd);
        if (state is null) return;

        var monitorId = state.MonitorId;
        state.WorkspaceId = targetWorkspaceId;

        if (monitorId is null) return;

        var isTargetActive = GetActiveWorkspace(monitorId) == targetWorkspaceId;
        using (_guard.Suppress(hwnd))
        {
            if (isTargetActive)
            {
                _windowManager.Show(hwnd);
            }
            else
            {
                _windowManager.Hide(hwnd);
            }
        }
    }

    /// <summary>
    /// Emergency recovery: shows every tracked window regardless of
    /// workspace assignment, without altering assignments.
    /// </summary>
    public void ShowAllWindows()
    {
        foreach (var window in _tracker.TrackedWindows.Values)
        {
            using (_guard.Suppress(window.Hwnd))
            {
                _windowManager.Show(window.Hwnd);
            }
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter "WorkspaceManagerTests"`
Expected: PASS (6 tests)

- [ ] **Step 6: Run the full unit test suite so far**

Run: `dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj`
Expected: PASS (all tests in `Core/` namespace, 0 failures)

- [ ] **Step 7: Commit**

```bash
git add src/WindowsSpaces.Core/WorkspaceManager.cs src/WindowsSpaces.Core/OperationGuard.cs src/WindowsSpaces.Tests/Core/WorkspaceManagerTests.cs
git commit -m "Add WorkspaceManager with per-monitor switching, latest-wins queue, operation guard"
```

---

### Task 6: Platform — NativeMethods and MonitorApi

**Files:**
- Create: `src/WindowsSpaces.Platform/Win32/NativeMethods.cs`
- Create: `src/WindowsSpaces.Platform/Win32/MonitorApi.cs`
- Test: `src/WindowsSpaces.Tests/Integration/MonitorApiTests.cs`

**Interfaces:**
- Consumes: `IMonitorManager`, `Monitor` from Core.
- Produces: `MonitorApi : IMonitorManager` — consumed by `WindowsSpaces.App` in Task 11.

- [ ] **Step 1: Implement `NativeMethods.cs`**

```csharp
// src/WindowsSpaces.Platform/Win32/NativeMethods.cs
using System.Runtime.InteropServices;

namespace WindowsSpaces.Platform.Win32;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    internal const uint MONITORINFOF_PRIMARY = 0x1;

    internal delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    internal static extern nint GetWindow(nint hWnd, uint uCmd);

    internal const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    internal static extern int GetWindowLong(nint hWnd, int nIndex);

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public System.Drawing.Point ptMinPosition;
        public System.Drawing.Point ptMaxPosition;
        public RECT rcNormalPosition;
    }

    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int SW_SHOW = 5;
    internal const int SW_MINIMIZE = 6;
    internal const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    internal static extern bool GetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPlacement(nint hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("kernel32.dll")]
    internal static extern int GetLastError();
}
```

- [ ] **Step 2: Implement `MonitorApi.cs`**

```csharp
// src/WindowsSpaces.Platform/Win32/MonitorApi.cs
using System.Drawing;
using WindowsSpaces.Core;
using static WindowsSpaces.Platform.Win32.NativeMethods;

namespace WindowsSpaces.Platform.Win32;

public sealed class MonitorApi : IMonitorManager
{
    public event EventHandler? MonitorsChanged;

    public IReadOnlyList<Monitor> GetMonitors()
    {
        var monitors = new List<Monitor>();

        bool Callback(nint hMonitor, nint hdc, ref RECT rect, nint data)
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add(new Monitor(
                    Id: info.szDevice,
                    DevicePath: info.szDevice,
                    Bounds: Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
                    IsPrimary: (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }

        if (!EnumDisplayMonitors(0, 0, Callback, 0))
        {
            throw new InvalidOperationException($"EnumDisplayMonitors failed, Win32 error {GetLastError()}");
        }

        return monitors;
    }

    public Monitor? GetMonitorForWindow(nint hwnd)
    {
        var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == 0) return null;

        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(hMonitor, ref info)) return null;

        return new Monitor(
            Id: info.szDevice,
            DevicePath: info.szDevice,
            Bounds: Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
            IsPrimary: (info.dwFlags & MONITORINFOF_PRIMARY) != 0);
    }
}
```

- [ ] **Step 3: Write the manual integration test**

```csharp
// src/WindowsSpaces.Tests/Integration/MonitorApiTests.cs
using WindowsSpaces.Platform.Win32;
using Xunit;

namespace WindowsSpaces.Tests.Integration;

/// <summary>
/// Manual/local only: requires a real Windows session with attached
/// monitors. Not run in CI. Run with:
/// dotnet test --filter Category=Manual
/// </summary>
[Trait("Category", "Manual")]
public class MonitorApiTests
{
    [Fact]
    public void GetMonitors_ReturnsAtLeastOneMonitorWithNonEmptyId()
    {
        var api = new MonitorApi();

        var monitors = api.GetMonitors();

        Assert.NotEmpty(monitors);
        Assert.All(monitors, m => Assert.False(string.IsNullOrWhiteSpace(m.Id)));
    }

    [Fact]
    public void GetMonitors_OnMultiMonitorSetup_ReturnsDistinctIds()
    {
        var api = new MonitorApi();

        var monitors = api.GetMonitors();
        if (monitors.Count < 2)
        {
            return; // skip on single-monitor dev machines
        }

        var ids = monitors.Select(m => m.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
```

- [ ] **Step 4: Build and run the manual test locally (requires Windows with attached monitors)**

Run: `dotnet build WindowsSpaces.sln`
Expected: `Build succeeded.`

Run: `dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter Category=Manual\&FullyQualifiedName~MonitorApiTests`
Expected: PASS (2 tests) when run on the real Windows dev machine.

- [ ] **Step 5: Commit**

```bash
git add src/WindowsSpaces.Platform/Win32/NativeMethods.cs src/WindowsSpaces.Platform/Win32/MonitorApi.cs src/WindowsSpaces.Tests/Integration/MonitorApiTests.cs
git commit -m "Add Win32 MonitorApi implementing IMonitorManager"
```

---

### Task 7: Platform — WindowApi

**Files:**
- Create: `src/WindowsSpaces.Platform/Win32/WindowApi.cs`
- Test: `src/WindowsSpaces.Tests/Integration/WindowApiTests.cs`

**Interfaces:**
- Consumes: `IWindowManager`, `WindowState`, `NativeMethods` from prior tasks.
- Produces: `WindowApi : IWindowManager` — consumed by `WindowsSpaces.App` in Task 11.

- [ ] **Step 1: Implement `WindowApi.cs`**

```csharp
// src/WindowsSpaces.Platform/Win32/WindowApi.cs
using System.Drawing;
using System.Text;
using WindowsSpaces.Core;
using static WindowsSpaces.Platform.Win32.NativeMethods;

namespace WindowsSpaces.Platform.Win32;

public sealed class WindowApi : IWindowManager
{
    public IReadOnlyList<nint> EnumerateTopLevelWindows()
    {
        var result = new List<nint>();

        bool Callback(nint hWnd, nint lParam)
        {
            if (IsManagedTopLevelWindow(hWnd))
            {
                result.Add(hWnd);
            }
            return true;
        }

        if (!EnumWindows(Callback, 0))
        {
            throw new InvalidOperationException($"EnumWindows failed, Win32 error {GetLastError()}");
        }

        return result;
    }

    private static bool IsManagedTopLevelWindow(nint hWnd)
    {
        if (!IsWindowVisible(hWnd)) return false;
        if (GetWindow(hWnd, GW_OWNER) != 0) return false;
        if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        if (GetWindowTextLength(hWnd) == 0) return false;
        return true;
    }

    public WindowState? GetWindowState(nint hwnd)
    {
        if (!IsWindow(hwnd)) return null;

        var placement = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref placement))
        {
            throw new InvalidOperationException($"GetWindowPlacement failed for {hwnd}, Win32 error {GetLastError()}");
        }

        GetWindowThreadProcessId(hwnd, out var processId);

        var normal = placement.rcNormalPosition;

        return new WindowState
        {
            Hwnd = hwnd,
            ProcessId = (int)processId,
            IsVisible = IsWindowVisible(hwnd),
            IsMinimized = placement.showCmd == SW_MINIMIZE,
            IsMaximized = placement.showCmd == 3, // SW_SHOWMAXIMIZED
            NormalBounds = Rectangle.FromLTRB(normal.Left, normal.Top, normal.Right, normal.Bottom),
            LastUpdated = DateTimeOffset.UtcNow
        };
    }

    public void Hide(nint hwnd)
    {
        if (!ShowWindow(hwnd, SW_HIDE))
        {
            // ShowWindow returns previous visibility state, not a success flag;
            // a false return here just means the window was already hidden.
        }
    }

    public void Show(nint hwnd)
    {
        var placement = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hwnd, ref placement);

        var showCmd = placement.showCmd switch
        {
            2 => 2,   // SW_SHOWMINIMIZED -> keep minimized
            3 => 3,   // SW_SHOWMAXIMIZED -> keep maximized
            _ => SW_SHOWNOACTIVATE
        };

        ShowWindow(hwnd, showCmd);
    }

    public void Move(nint hwnd, Rectangle bounds)
    {
        if (!SetWindowPos(hwnd, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE))
        {
            throw new InvalidOperationException($"SetWindowPos failed for {hwnd}, Win32 error {GetLastError()}");
        }
    }

    public void SetForeground(nint hwnd)
    {
        SetForegroundWindow(hwnd);
    }

    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();
}
```

- [ ] **Step 2: Write the manual integration test**

```csharp
// src/WindowsSpaces.Tests/Integration/WindowApiTests.cs
using WindowsSpaces.Platform.Win32;
using Xunit;

namespace WindowsSpaces.Tests.Integration;

/// <summary>
/// Manual/local only: requires a real Windows session. Not run in CI.
/// Launch WindowsSpaces.TestApp before running so there are known windows
/// to enumerate. Run with: dotnet test --filter Category=Manual
/// </summary>
[Trait("Category", "Manual")]
public class WindowApiTests
{
    [Fact]
    public void EnumerateTopLevelWindows_ReturnsAtLeastOneWindow()
    {
        var api = new WindowApi();

        var windows = api.EnumerateTopLevelWindows();

        Assert.NotEmpty(windows);
    }

    [Fact]
    public void HideThenShow_RoundTripsVisibility()
    {
        var api = new WindowApi();
        var hwnd = api.EnumerateTopLevelWindows().First();

        api.Hide(hwnd);
        var hiddenState = api.GetWindowState(hwnd);

        api.Show(hwnd);
        var shownState = api.GetWindowState(hwnd);

        Assert.False(hiddenState!.IsVisible);
        Assert.True(shownState!.IsVisible);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build WindowsSpaces.sln`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/WindowsSpaces.Platform/Win32/WindowApi.cs src/WindowsSpaces.Tests/Integration/WindowApiTests.cs
git commit -m "Add Win32 WindowApi implementing IWindowManager"
```

---

### Task 8: Platform — WinEventHook

**Files:**
- Create: `src/WindowsSpaces.Platform/Win32/WinEventHook.cs`

**Interfaces:**
- Consumes: `IWindowEventSource`, `WindowEvent`, `WindowEventKind` from Core.
- Produces: `WinEventHook : IWindowEventSource` — consumed by `WindowsSpaces.App` in Task 11.

WinEvent hooks require a live Win32 message loop to deliver callbacks, which only exists once `WindowsSpaces.App` is running — so this task has no automated test; it is exercised manually as part of the Task 11/12 acceptance pass.

- [ ] **Step 1: Add WinEvent P/Invoke declarations to `NativeMethods.cs`**

```csharp
// Append to src/WindowsSpaces.Platform/Win32/NativeMethods.cs, inside the NativeMethods class

internal delegate void WinEventDelegate(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

[DllImport("user32.dll")]
internal static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

[DllImport("user32.dll")]
internal static extern bool UnhookWinEvent(nint hWinEventHook);

internal const uint EVENT_OBJECT_CREATE = 0x8000;
internal const uint EVENT_OBJECT_DESTROY = 0x8001;
internal const uint EVENT_OBJECT_SHOW = 0x8002;
internal const uint EVENT_OBJECT_HIDE = 0x8003;
internal const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
internal const int OBJID_WINDOW = 0;

[DllImport("user32.dll")]
internal static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

[DllImport("user32.dll")]
internal static extern bool TranslateMessage(ref MSG lpMsg);

[DllImport("user32.dll")]
internal static extern nint DispatchMessage(ref MSG lpMsg);

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public Point pt;
}
```

Add `using System.Drawing;` to the top of `NativeMethods.cs` if not already present (needed for `Point`).

- [ ] **Step 2: Implement `WinEventHook.cs`**

```csharp
// src/WindowsSpaces.Platform/Win32/WinEventHook.cs
using System.Collections.Concurrent;
using WindowsSpaces.Core;
using static WindowsSpaces.Platform.Win32.NativeMethods;

namespace WindowsSpaces.Platform.Win32;

/// <summary>
/// Wraps SetWinEventHook. The hook callback only enqueues events into a
/// ConcurrentQueue; a background thread drains the queue and raises
/// WindowEvent, keeping the hook callback itself lightweight per the
/// parent spec's requirement.
/// </summary>
public sealed class WinEventHook : IWindowEventSource, IDisposable
{
    private readonly ConcurrentQueue<WindowEvent> _queue = new();
    private readonly List<nint> _hooks = new();
    private readonly WinEventDelegate _callback;
    private Thread? _dispatchThread;
    private volatile bool _running;

    public event EventHandler<WindowEvent>? WindowEvent;

    public WinEventHook()
    {
        _callback = OnWinEvent;
    }

    public void Start()
    {
        _hooks.Add(SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE, 0, _callback, 0, 0, WINEVENT_OUTOFCONTEXT));
        _hooks.Add(SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, 0, _callback, 0, 0, WINEVENT_OUTOFCONTEXT));
        _hooks.Add(SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _callback, 0, 0, WINEVENT_OUTOFCONTEXT));

        _running = true;
        _dispatchThread = new Thread(DispatchLoop) { IsBackground = true, Name = "WindowsSpaces.EventDispatch" };
        _dispatchThread.Start();
    }

    public void Stop()
    {
        _running = false;
        foreach (var hook in _hooks)
        {
            if (hook != 0) UnhookWinEvent(hook);
        }
        _hooks.Clear();
    }

    private void OnWinEvent(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != OBJID_WINDOW || hwnd == 0) return;

        var kind = eventType switch
        {
            EVENT_OBJECT_CREATE => WindowEventKind.Created,
            EVENT_OBJECT_DESTROY => WindowEventKind.Destroyed,
            EVENT_OBJECT_SHOW => WindowEventKind.Shown,
            EVENT_OBJECT_HIDE => WindowEventKind.Hidden,
            EVENT_OBJECT_LOCATIONCHANGE => WindowEventKind.LocationChanged,
            EVENT_SYSTEM_FOREGROUND => WindowEventKind.ForegroundChanged,
            _ => (WindowEventKind?)null
        };

        if (kind is null) return;

        _queue.Enqueue(new WindowEvent(kind.Value, hwnd, DateTimeOffset.UtcNow));
    }

    private void DispatchLoop()
    {
        while (_running)
        {
            if (_queue.TryDequeue(out var evt))
            {
                WindowEvent?.Invoke(this, evt);
            }
            else
            {
                Thread.Sleep(10);
            }
        }
    }

    public void Dispose() => Stop();
}
```

Note: `SetWinEventHook` requires a Win32 message loop pumping messages on the thread that installed the hook for `WINEVENT_OUTOFCONTEXT` callbacks to fire reliably. `WindowsSpaces.App` (Task 11) supplies this loop.

- [ ] **Step 3: Build**

Run: `dotnet build WindowsSpaces.sln`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/WindowsSpaces.Platform/Win32/WinEventHook.cs src/WindowsSpaces.Platform/Win32/NativeMethods.cs
git commit -m "Add WinEventHook implementing IWindowEventSource with queued dispatch"
```

---

### Task 9: Platform — HotkeyManager

**Files:**
- Create: `src/WindowsSpaces.Platform/Win32/HotkeyManager.cs`

**Interfaces:**
- Consumes: `IHotkeyManager`, `ModifierKeys` from Core.
- Produces: `HotkeyManager : IHotkeyManager` — consumed by `WindowsSpaces.App` in Task 11.

Like WinEventHook, `RegisterHotKey`/`WM_HOTKEY` delivery requires a message loop, so this is manually verified in Task 12, not unit tested.

- [ ] **Step 1: Add hotkey P/Invoke declarations to `NativeMethods.cs`**

```csharp
// Append to src/WindowsSpaces.Platform/Win32/NativeMethods.cs, inside the NativeMethods class

[DllImport("user32.dll")]
internal static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

[DllImport("user32.dll")]
internal static extern bool UnregisterHotKey(nint hWnd, int id);

internal const uint WM_HOTKEY = 0x0312;
```

- [ ] **Step 2: Implement `HotkeyManager.cs`**

```csharp
// src/WindowsSpaces.Platform/Win32/HotkeyManager.cs
using WindowsSpaces.Core;
using static WindowsSpaces.Platform.Win32.NativeMethods;

namespace WindowsSpaces.Platform.Win32;

/// <summary>
/// Wraps RegisterHotKey/WM_HOTKEY. Must be constructed with the hwnd of a
/// window pumping messages on the thread that calls Register — WM_HOTKEY
/// is delivered to that window's message queue. WindowsSpaces.App feeds
/// WM_HOTKEY messages from its loop into HandleMessage.
/// </summary>
public sealed class HotkeyManager : IHotkeyManager, IDisposable
{
    private readonly nint _hwnd;
    private readonly Dictionary<int, Action> _callbacks = new();

    public HotkeyManager(nint hwnd)
    {
        _hwnd = hwnd;
    }

    public void Register(int id, ModifierKeys modifiers, int virtualKey, Action callback)
    {
        if (!RegisterHotKey(_hwnd, id, (uint)modifiers, (uint)virtualKey))
        {
            throw new InvalidOperationException($"RegisterHotKey failed for id {id}, Win32 error {GetLastError()}");
        }
        _callbacks[id] = callback;
    }

    public void Unregister(int id)
    {
        UnregisterHotKey(_hwnd, id);
        _callbacks.Remove(id);
    }

    /// <summary>
    /// Call from the App's message loop for every received message.
    /// Invokes the registered callback when the message is WM_HOTKEY.
    /// </summary>
    public void HandleMessage(uint message, nint wParam)
    {
        if (message != WM_HOTKEY) return;

        var id = (int)wParam;
        if (_callbacks.TryGetValue(id, out var callback))
        {
            callback();
        }
    }

    public void Dispose()
    {
        foreach (var id in _callbacks.Keys.ToList())
        {
            UnregisterHotKey(_hwnd, id);
        }
        _callbacks.Clear();
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build WindowsSpaces.sln`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/WindowsSpaces.Platform/Win32/HotkeyManager.cs src/WindowsSpaces.Platform/Win32/NativeMethods.cs
git commit -m "Add HotkeyManager implementing IHotkeyManager"
```

---

### Task 10: WindowsSpaces.TestApp — deterministic test windows

**Files:**
- Modify: `src/WindowsSpaces.TestApp/Program.cs`
- Create: `src/WindowsSpaces.TestApp/TestWindowForm.cs`
- Delete: `src/WindowsSpaces.TestApp/Form1.cs`, `src/WindowsSpaces.TestApp/Form1.Designer.cs`, `src/WindowsSpaces.TestApp/Form1.resx` (generated by the winforms template, unused)

**Interfaces:**
- Produces: a standalone executable that opens 4 windows with known titles: `SpacesTest-Normal-1`, `SpacesTest-Normal-2`, `SpacesTest-Maximized-1`, `SpacesTest-Minimized-1`, `SpacesTest-AlwaysOnTop-1` — used manually and by integration tests in Task 12.

- [ ] **Step 1: Delete the template-generated form files**

```bash
rm -f src/WindowsSpaces.TestApp/Form1.cs src/WindowsSpaces.TestApp/Form1.Designer.cs src/WindowsSpaces.TestApp/Form1.resx
```

- [ ] **Step 2: Implement `TestWindowForm.cs`**

```csharp
// src/WindowsSpaces.TestApp/TestWindowForm.cs
namespace WindowsSpaces.TestApp;

public sealed class TestWindowForm : Form
{
    public TestWindowForm(string title, Size size, Point location, FormWindowState startState = FormWindowState.Normal, bool alwaysOnTop = false)
    {
        Text = title;
        Size = size;
        StartPosition = FormStartPosition.Manual;
        Location = location;
        WindowState = startState;
        TopMost = alwaysOnTop;

        var label = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 16)
        };
        Controls.Add(label);
    }
}
```

- [ ] **Step 3: Implement `Program.cs`**

```csharp
// src/WindowsSpaces.TestApp/Program.cs
namespace WindowsSpaces.TestApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var windows = new List<Form>
        {
            new TestWindowForm("SpacesTest-Normal-1", new Size(500, 400), new Point(100, 100)),
            new TestWindowForm("SpacesTest-Normal-2", new Size(500, 400), new Point(650, 100)),
            new TestWindowForm("SpacesTest-Maximized-1", new Size(500, 400), new Point(100, 550), FormWindowState.Maximized),
            new TestWindowForm("SpacesTest-Minimized-1", new Size(500, 400), new Point(650, 550), FormWindowState.Minimized),
            new TestWindowForm("SpacesTest-AlwaysOnTop-1", new Size(300, 200), new Point(1200, 100), alwaysOnTop: true),
        };

        foreach (var window in windows)
        {
            window.Show();
        }

        Application.Run();
    }
}
```

- [ ] **Step 4: Build and manually verify**

Run: `dotnet run --project src/WindowsSpaces.TestApp/WindowsSpaces.TestApp.csproj`
Expected: 5 windows appear with the titles above; one maximized, one minimized, one always-on-top. Close all windows manually (or `Ctrl+C` the process) after verifying.

- [ ] **Step 5: Commit**

```bash
git add src/WindowsSpaces.TestApp
git commit -m "Add deterministic test windows to WindowsSpaces.TestApp"
```

---

### Task 11: WindowsSpaces.App — wire hotkeys, tray icon, emergency recovery

**Files:**
- Create: `src/WindowsSpaces.App/Program.cs`
- Create: `src/WindowsSpaces.App/AppHost.cs`
- Create: `src/WindowsSpaces.App/TrayIcon.cs`

**Interfaces:**
- Consumes: `WorkspaceManager`, `WindowTracker`, `IMonitorManager`, `IWindowManager`, `IWindowEventSource`, `IHotkeyManager`, `ModifierKeys` from Core/Platform. `MonitorApi`, `WindowApi`, `WinEventHook`, `HotkeyManager` from Platform.
- Produces: the running application. Nothing downstream consumes this — it is the composition root.

Per the Global Constraints, this project never calls raw `user32.dll` window-management functions directly — only `WorkspaceManager` and the `IHotkeyManager`/tray plumbing. The tray icon uses `Shell_NotifyIcon`, which is UI chrome, not window management, and is implemented locally in `TrayIcon.cs` for that reason.

- [ ] **Step 1: Implement `TrayIcon.cs`**

```csharp
// src/WindowsSpaces.App/TrayIcon.cs
using System.Runtime.InteropServices;

namespace WindowsSpaces.App;

/// <summary>
/// Minimal Shell_NotifyIcon wrapper for a status-only tray icon. This is
/// UI chrome (not window management), so it is intentionally kept local
/// to the App project rather than routed through Core/Platform.
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    private NOTIFYICONDATA _data;
    private bool _added;

    public TrayIcon(nint hwnd)
    {
        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_TIP,
            uCallbackMessage = 0x8000, // WM_APP
            hIcon = 0,
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

    public void Dispose()
    {
        if (_added) Shell_NotifyIcon(NIM_DELETE, ref _data);
    }
}
```

- [ ] **Step 2: Implement `AppHost.cs`**

```csharp
// src/WindowsSpaces.App/AppHost.cs
using WindowsSpaces.Core;
using WindowsSpaces.Platform.Win32;

namespace WindowsSpaces.App;

/// <summary>
/// Composition root: wires Platform implementations into Core, sets up
/// two workspaces per monitor, registers hotkeys, and starts tracking.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly MonitorApi _monitorApi = new();
    private readonly WindowApi _windowApi = new();
    private readonly WinEventHook _eventSource = new();
    private readonly WindowTracker _tracker;
    private readonly WorkspaceManager _workspaceManager;
    private HotkeyManager? _hotkeys;
    private TrayIcon? _trayIcon;

    public AppHost()
    {
        _tracker = new WindowTracker(_windowApi, _eventSource);
        _workspaceManager = new WorkspaceManager(_windowApi, _tracker);
    }

    public void Start(nint messageWindowHwnd)
    {
        _tracker.Rescan();
        _eventSource.Start();

        foreach (var monitor in _monitorApi.GetMonitors())
        {
            _workspaceManager.SwitchWorkspace(monitor.Id, $"{monitor.Id}:1");
        }

        _hotkeys = new HotkeyManager(messageWindowHwnd);
        RegisterHotkeys();

        _trayIcon = new TrayIcon(messageWindowHwnd);
        _trayIcon.Show();
    }

    private void RegisterHotkeys()
    {
        const int VK_1 = 0x31;
        const int VK_2 = 0x32;

        _hotkeys!.Register(1, ModifierKeys.Control | ModifierKeys.Alt, VK_1, () => SwitchCurrentMonitor(1));
        _hotkeys.Register(2, ModifierKeys.Control | ModifierKeys.Alt, VK_2, () => SwitchCurrentMonitor(2));
        _hotkeys.Register(3, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, VK_1, () => MoveActiveWindow(1));
        _hotkeys.Register(4, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, VK_2, () => MoveActiveWindow(2));
        // Emergency show-all: Ctrl+Alt+Shift+Escape (VK_ESCAPE = 0x1B)
        _hotkeys.Register(5, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x1B, ShowAllWindows);
    }

    private void SwitchCurrentMonitor(int workspaceIndex)
    {
        var foreground = _windowApi.GetForegroundWindow();
        var monitor = _monitorApi.GetMonitorForWindow(foreground);
        if (monitor is null) return;

        _workspaceManager.SwitchWorkspace(monitor.Id, $"{monitor.Id}:{workspaceIndex}");
        _trayIcon?.SetTooltip($"Windows Spaces — {monitor.Id} on space {workspaceIndex}");
    }

    private void MoveActiveWindow(int workspaceIndex)
    {
        var foreground = _windowApi.GetForegroundWindow();
        var monitor = _monitorApi.GetMonitorForWindow(foreground);
        if (monitor is null) return;

        _workspaceManager.AssignWindow(foreground, $"{monitor.Id}:{workspaceIndex}");
    }

    public void ShowAllWindows() => _workspaceManager.ShowAllWindows();

    public void HandleMessage(uint message, nint wParam) => _hotkeys?.HandleMessage(message, wParam);

    public void Dispose()
    {
        _eventSource.Stop();
        _hotkeys?.Dispose();
        _trayIcon?.Dispose();
    }
}
```

- [ ] **Step 3: Implement `Program.cs` with a native message-only window and loop**

```csharp
// src/WindowsSpaces.App/Program.cs
using System.Runtime.InteropServices;
using WindowsSpaces.App;

internal static class Program
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_DESTROY = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    private const nint HWND_MESSAGE = -3;

    private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public System.Drawing.Point pt;
    }

    private static AppHost? _host;
    private static WndProc? _wndProcDelegate;

    [STAThread]
    private static void Main()
    {
        _wndProcDelegate = WndProcHandler;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProcDelegate,
            lpszClassName = "WindowsSpacesMessageWindow",
            lpszMenuName = string.Empty
        };
        RegisterClassEx(ref wc);

        var hwnd = CreateWindowEx(0, "WindowsSpacesMessageWindow", "WindowsSpaces", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, 0, 0);
        if (hwnd == 0)
        {
            throw new InvalidOperationException("Failed to create message-only window for hotkey/tray hosting.");
        }

        _host = new AppHost();
        _host.Start(hwnd);

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY)
            {
                _host.HandleMessage(msg.message, msg.wParam);
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        _host.Dispose();
    }

    private static nint WndProcHandler(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_DESTROY)
        {
            _host?.Dispose();
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build WindowsSpaces.sln`
Expected: `Build succeeded.`

- [ ] **Step 5: Manual smoke test**

Run: `dotnet run --project src/WindowsSpaces.App/WindowsSpaces.App.csproj` (with `WindowsSpaces.TestApp` also running from Task 10)
Expected: no crash on startup; a tray icon titled "Windows Spaces" appears; pressing `Ctrl+Alt+Shift+Esc` does not error (full switching behavior verified in Task 12's acceptance pass).

- [ ] **Step 6: Commit**

```bash
git add src/WindowsSpaces.App
git commit -m "Wire AppHost: hotkeys, tray icon, message loop for WindowsSpaces.App"
```

---

### Task 12: Manual acceptance pass and deliverable report

**Files:**
- Create: `docs/superpowers/reports/2026-08-14-phase0-spike-report.md`

**Interfaces:**
- Consumes: the full running system from Tasks 1-11.
- Produces: the go/no-go deliverable required by the parent spec §20.

- [ ] **Step 1: Run the full automated test suite**

Run: `dotnet test WindowsSpaces.sln --filter "Category!=Manual"`
Expected: PASS, 0 failures (all Core unit tests from Tasks 2, 4, 5).

- [ ] **Step 2: Run the manual integration tests on the real dev machine**

Run: `dotnet test WindowsSpaces.sln --filter Category=Manual`
Expected: PASS (MonitorApiTests, WindowApiTests from Tasks 6-7).

- [ ] **Step 3: Run the manual acceptance pass**

With 2 real monitors attached:

1. Launch `WindowsSpaces.TestApp` (Task 10) — 5 windows appear.
2. Launch `WindowsSpaces.App` (Task 11).
3. Move 2 test windows to Monitor A, 2 to Monitor B, using OS window drag (not the app, since window→workspace UI doesn't exist yet — manually position them and confirm `AppHost` picks up their monitor from `WindowTracker`/`Rescan`).
4. Focus a window on Monitor A, press `Ctrl+Alt+2`. Confirm: Monitor A's windows hide/show per workspace 2; Monitor B's windows are untouched (AC-001).
5. Focus a window on Monitor B, press `Ctrl+Alt+2`. Confirm both monitors are now on workspace 2 independently (AC-002).
6. Focus a window, press `Ctrl+Alt+Shift+1` to move it to workspace 1 on its monitor. Switch that monitor to workspace 1 and confirm the window reappears there (AC-003).
7. Press `Ctrl+Alt+Shift+Esc` (emergency show-all). Confirm every managed window becomes visible regardless of workspace (AC-007).
8. Record pass/fail for each step.

- [ ] **Step 4: Write the deliverable report**

```markdown
<!-- docs/superpowers/reports/2026-08-14-phase0-spike-report.md -->
# Windows Spaces — Phase 0 Spike Report

**Date:** 2026-08-14

## Architecture summary
[Fill in: 2-3 sentences confirming Core/Platform/App layering held, Core had zero Win32 references, etc.]

## Files created
[Fill in: list generated via `git log --stat` across this branch, or `git diff --stat <first-commit>..HEAD`]

## Win32 APIs used
EnumDisplayMonitors, GetMonitorInfo, MonitorFromWindow, EnumWindows, IsWindowVisible, IsWindow, GetWindowText(Length), GetWindowThreadProcessId, GetWindow, GetWindowLong, GetWindowRect, GetWindowPlacement, SetWindowPlacement, ShowWindow, SetWindowPos, SetForegroundWindow, GetForegroundWindow, SetWinEventHook, UnhookWinEvent, RegisterHotKey, UnregisterHotKey, Shell_NotifyIcon, CreateWindowEx, RegisterClassEx, GetMessage/TranslateMessage/DispatchMessage.

## Test results
[Fill in: automated unit test count/pass, manual integration test pass/fail, acceptance pass step-by-step results from Step 3 above]

## Known Windows limitations
[Fill in based on what was actually observed — e.g., WinEvent hook reliability, focus-stealing on Show, elevated-app restrictions]

## Compatibility issues
[Fill in: any app that misbehaved during the acceptance pass — Electron/UWP/games if tested]

## Crash/recovery analysis
Bounded to emergency show-all only in this phase (no persistence/crash markers in scope). [Fill in observed behavior.]

## Performance observations
[Fill in: subjective switch latency, idle CPU/memory if measured via Task Manager]

## Recommendation
[Fill in: proceed to full Phase 1 / do not proceed and why, per parent spec §20's instruction to stop and document blockers if the approach is unsound]
```

- [ ] **Step 5: Fill in the report based on actual results from Steps 1-3, then commit**

```bash
git add docs/superpowers/reports/2026-08-14-phase0-spike-report.md
git commit -m "Add Phase 0 spike deliverable report"
```

---

## Self-Review Notes

- **Spec coverage:** every "in scope" bullet in the design doc maps to a task — monitor enumeration (6), window enumeration/hide/show/move (7), tracking via WinEvent (4, 8), independent switching + latest-wins + operation guard (5), hotkeys (9, 11), emergency recovery (5, 11), test app (10), unit + manual integration tests (2, 4, 5, 6, 7), deliverable report (12).
- **Type consistency checked:** `WorkspaceManager` constructor signature `(IWindowManager, WindowTracker)` used consistently in Task 5 tests and Task 11 `AppHost`. `IHotkeyManager.Register(int id, ModifierKeys modifiers, int virtualKey, Action callback)` matches its Task 9 implementation and Task 11 call sites. `WindowEvent`/`WindowEventKind` fields match between Task 3 definition, Task 4 consumer, and Task 8 producer.
- **No placeholders** remain in code steps; the deliverable report template in Task 12 is explicitly a fill-in-after-running document, not a plan placeholder.
