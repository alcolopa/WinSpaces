# Phase 2 — Product UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the Phase 1 persistence gap (workspace count/names were hardcoded, nothing survived restart) and build Phase 2's Product UI — settings, shortcut configuration, workspace UI (tray menu), diagnostics — per `windows_spaces_technical_documentation.md` §17.

**Architecture:** New `AppConfiguration`/`HotkeyBinding`/`WorkspaceDefinition` models and an `IConfigurationStore` interface land in `WindowsSpaces.Core` (no Win32/UI dependency). A new `WindowsSpaces.Persistence` project implements the interface as fail-open JSON file storage. `WindowsSpaces.App` becomes a WinUI3 app: `AppHost` is extended to load config at startup and expose `ApplyConfiguration`/`GetDiagnosticsSnapshot`; `TrayIcon` grows a real context menu; three small windows (Settings, Shortcuts, Diagnostics) bind to plain C# view-models that contain all the testable logic.

**Tech Stack:** C#, .NET (net8.0 / net8.0-windows / net8.0-windows10.0.19041.0, per existing per-project TFMs), xUnit, Windows App SDK / WinUI3 (`Microsoft.WindowsAppSDK` NuGet, unpackaged deployment — no MSIX).

**Spec:** `docs/superpowers/specs/2026-08-15-phase2-product-ui-design.md`

## Global Constraints

- The app is never launched/run as part of this work. Verification is `dotnet build` + `dotnet test` only (per project, then whole-solution). Use `export PATH="/c/Program Files/dotnet:$PATH"` in Bash before any `dotnet` command — the SDK isn't on the default PATH in this shell.
- `Core` must never reference `Platform`, `Persistence`, or `App`. `Persistence` must reference only `Core`.
- Never identify a monitor by array index — `MonitorWorkspaceConfig.MonitorId` matches `Monitor.Id` (already stable per `Monitor.cs`).
- `JsonConfigurationStore.Load()` must never throw — any failure (missing file, bad JSON, failed validation, unknown schema version) returns `null` ("fail open"); callers fall back to `AppConfiguration.CreateDefault`.
- Workspace names non-empty and unique within a monitor; 1–9 workspaces per monitor (hotkeys are single-digit); no two `HotkeyBinding`s share the same `(Modifiers, VirtualKey)` pair. Enforced by one `AppConfiguration.Validate()` used by both the store and the settings view-models.
- Existing default behavior (2 workspaces per monitor named "Space 1"/"Space 2", the 5 current hotkey bindings) must be reproduced exactly by `AppConfiguration.CreateDefault` so a fresh/missing config doesn't change today's behavior.
- Every new project/class follows the existing style: `sealed record` for immutable data, `sealed class` for services, XML-doc summary only on non-obvious classes (see `WorkspaceManager.cs`, `WindowTracker.cs` for the bar).

---

### Task 1: Core — HotkeyAction enum and HotkeyBinding record

**Files:**
- Create: `src/WindowsSpaces.Core/HotkeyAction.cs`
- Create: `src/WindowsSpaces.Core/HotkeyBinding.cs`
- Test: `src/WindowsSpaces.Tests/Core/HotkeyBindingTests.cs`

**Interfaces:**
- Consumes: `WindowsSpaces.Core.ModifierKeys` (existing, `IHotkeyManager.cs`).
- Produces: `HotkeyAction` enum with values `SwitchWorkspace = 0, MoveToWorkspace = 1, ShowAllWindows = 2`; `HotkeyBinding` record `(HotkeyAction Action, int WorkspaceIndex, ModifierKeys Modifiers, int VirtualKey)` where `WorkspaceIndex` is 1-based and ignored (must be 0) for `ShowAllWindows`. `HotkeyBinding.ConflictsWith(HotkeyBinding other)` — true when `Modifiers` and `VirtualKey` both match.

- [ ] **Step 1: Write the failing test**

```csharp
using WindowsSpaces.Core;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class HotkeyBindingTests
{
    [Fact]
    public void ConflictsWith_SameModifiersAndKey_ReturnsTrue()
    {
        var a = new HotkeyBinding(HotkeyAction.SwitchWorkspace, WorkspaceIndex: 1, ModifierKeys.Control | ModifierKeys.Alt, VirtualKey: 0x31);
        var b = new HotkeyBinding(HotkeyAction.MoveToWorkspace, WorkspaceIndex: 2, ModifierKeys.Control | ModifierKeys.Alt, VirtualKey: 0x31);

        Assert.True(a.ConflictsWith(b));
    }

    [Fact]
    public void ConflictsWith_DifferentKey_ReturnsFalse()
    {
        var a = new HotkeyBinding(HotkeyAction.SwitchWorkspace, WorkspaceIndex: 1, ModifierKeys.Control | ModifierKeys.Alt, VirtualKey: 0x31);
        var b = new HotkeyBinding(HotkeyAction.SwitchWorkspace, WorkspaceIndex: 2, ModifierKeys.Control | ModifierKeys.Alt, VirtualKey: 0x32);

        Assert.False(a.ConflictsWith(b));
    }

    [Fact]
    public void ConflictsWith_DifferentModifiers_ReturnsFalse()
    {
        var a = new HotkeyBinding(HotkeyAction.SwitchWorkspace, WorkspaceIndex: 1, ModifierKeys.Control | ModifierKeys.Alt, VirtualKey: 0x31);
        var b = new HotkeyBinding(HotkeyAction.SwitchWorkspace, WorkspaceIndex: 1, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, VirtualKey: 0x31);

        Assert.False(a.ConflictsWith(b));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter HotkeyBindingTests`
Expected: FAIL to build — `HotkeyBinding`/`HotkeyAction` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/WindowsSpaces.Core/HotkeyAction.cs
namespace WindowsSpaces.Core;

public enum HotkeyAction
{
    SwitchWorkspace,
    MoveToWorkspace,
    ShowAllWindows
}
```

```csharp
// src/WindowsSpaces.Core/HotkeyBinding.cs
namespace WindowsSpaces.Core;

/// <summary>
/// One configured hotkey. WorkspaceIndex is 1-based and only meaningful for
/// SwitchWorkspace/MoveToWorkspace (must be 0 for ShowAllWindows).
/// </summary>
public sealed record HotkeyBinding(
    HotkeyAction Action,
    int WorkspaceIndex,
    ModifierKeys Modifiers,
    int VirtualKey)
{
    public bool ConflictsWith(HotkeyBinding other) =>
        Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter HotkeyBindingTests`
Expected: PASS, 3/3.

- [ ] **Step 5: Commit**

```bash
git add src/WindowsSpaces.Core/HotkeyAction.cs src/WindowsSpaces.Core/HotkeyBinding.cs src/WindowsSpaces.Tests/Core/HotkeyBindingTests.cs
git commit -m "Add HotkeyAction/HotkeyBinding domain model"
```

---

### Task 2: Core — WorkspaceDefinition, MonitorWorkspaceConfig, AppConfiguration

**Files:**
- Create: `src/WindowsSpaces.Core/WorkspaceDefinition.cs`
- Create: `src/WindowsSpaces.Core/MonitorWorkspaceConfig.cs`
- Create: `src/WindowsSpaces.Core/AppConfiguration.cs`
- Create: `src/WindowsSpaces.Core/IConfigurationStore.cs`
- Test: `src/WindowsSpaces.Tests/Core/AppConfigurationTests.cs`

**Interfaces:**
- Consumes: `HotkeyAction`, `HotkeyBinding`, `ModifierKeys` (Task 1), `Monitor` (existing).
- Produces: `WorkspaceDefinition(string Id, string Name, int Index)`; `MonitorWorkspaceConfig(string MonitorId, IReadOnlyList<WorkspaceDefinition> Workspaces)`; `AppConfiguration(int SchemaVersion, IReadOnlyList<MonitorWorkspaceConfig> Monitors, IReadOnlyList<HotkeyBinding> Hotkeys)` with:
  - `const int CurrentSchemaVersion = 1;`
  - `static AppConfiguration CreateDefault(IEnumerable<Monitor> monitors)`
  - `bool Validate(out string? error)` — instance method, returns true/false and sets `error` to the first violation found or null.
  - `IConfigurationStore` interface: `AppConfiguration? Load(); void Save(AppConfiguration config);` (`Load` returns `null`, never throws, per Global Constraints).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using WindowsSpaces.Core;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.Core;

public class AppConfigurationTests
{
    private static readonly Monitor MonA = new("MON-A", "\\\\.\\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true);
    private static readonly Monitor MonB = new("MON-B", "\\\\.\\DISPLAY2", new Rectangle(1920, 0, 1920, 1080), IsPrimary: false);

    [Fact]
    public void CreateDefault_GivesTwoWorkspacesPerMonitor_NamedSpace1AndSpace2()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA, MonB });

        Assert.Equal(2, config.Monitors.Count);
        var monA = config.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.Equal(new[] { "Space 1", "Space 2" }, monA.Workspaces.Select(w => w.Name));
        Assert.Equal(new[] { "MON-A:1", "MON-A:2" }, monA.Workspaces.Select(w => w.Id));
    }

    [Fact]
    public void CreateDefault_GivesTheFiveExistingHotkeyBindings()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });

        Assert.Equal(5, config.Hotkeys.Count);
        Assert.Contains(config.Hotkeys, h => h.Action == HotkeyAction.SwitchWorkspace && h.WorkspaceIndex == 1
            && h.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && h.VirtualKey == 0x31);
        Assert.Contains(config.Hotkeys, h => h.Action == HotkeyAction.SwitchWorkspace && h.WorkspaceIndex == 2
            && h.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && h.VirtualKey == 0x32);
        Assert.Contains(config.Hotkeys, h => h.Action == HotkeyAction.MoveToWorkspace && h.WorkspaceIndex == 1
            && h.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift) && h.VirtualKey == 0x31);
        Assert.Contains(config.Hotkeys, h => h.Action == HotkeyAction.MoveToWorkspace && h.WorkspaceIndex == 2
            && h.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift) && h.VirtualKey == 0x32);
        Assert.Contains(config.Hotkeys, h => h.Action == HotkeyAction.ShowAllWindows
            && h.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift) && h.VirtualKey == 0x1B);
    }

    [Fact]
    public void Validate_EmptyWorkspaceName_Fails()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with
        {
            Monitors = new[]
            {
                new MonitorWorkspaceConfig("MON-A", new[]
                {
                    new WorkspaceDefinition("MON-A:1", "", 1)
                })
            }
        };

        Assert.False(config.Validate(out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_DuplicateWorkspaceNameOnSameMonitor_Fails()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with
        {
            Monitors = new[]
            {
                new MonitorWorkspaceConfig("MON-A", new[]
                {
                    new WorkspaceDefinition("MON-A:1", "Dup", 1),
                    new WorkspaceDefinition("MON-A:2", "Dup", 2)
                })
            }
        };

        Assert.False(config.Validate(out _));
    }

    [Fact]
    public void Validate_TenWorkspacesOnOneMonitor_Fails()
    {
        var workspaces = Enumerable.Range(1, 10)
            .Select(i => new WorkspaceDefinition($"MON-A:{i}", $"Space {i}", i))
            .ToArray();
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with
        {
            Monitors = new[] { new MonitorWorkspaceConfig("MON-A", workspaces) }
        };

        Assert.False(config.Validate(out _));
    }

    [Fact]
    public void Validate_ZeroWorkspacesOnOneMonitor_Fails()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with
        {
            Monitors = new[] { new MonitorWorkspaceConfig("MON-A", System.Array.Empty<WorkspaceDefinition>()) }
        };

        Assert.False(config.Validate(out _));
    }

    [Fact]
    public void Validate_ConflictingHotkeys_Fails()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with
        {
            Hotkeys = new[]
            {
                new HotkeyBinding(HotkeyAction.SwitchWorkspace, 1, ModifierKeys.Control, 0x31),
                new HotkeyBinding(HotkeyAction.MoveToWorkspace, 1, ModifierKeys.Control, 0x31)
            }
        };

        Assert.False(config.Validate(out _));
    }

    [Fact]
    public void Validate_DefaultConfig_Passes()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA, MonB });

        Assert.True(config.Validate(out var error));
        Assert.Null(error);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter AppConfigurationTests`
Expected: FAIL to build — types don't exist yet.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/WindowsSpaces.Core/WorkspaceDefinition.cs
namespace WindowsSpaces.Core;

/// <summary>Configured (not yet live) workspace: Id is "{MonitorId}:{Index}", matching Workspace.Id.</summary>
public sealed record WorkspaceDefinition(string Id, string Name, int Index);
```

```csharp
// src/WindowsSpaces.Core/MonitorWorkspaceConfig.cs
namespace WindowsSpaces.Core;

public sealed record MonitorWorkspaceConfig(string MonitorId, IReadOnlyList<WorkspaceDefinition> Workspaces);
```

```csharp
// src/WindowsSpaces.Core/IConfigurationStore.cs
namespace WindowsSpaces.Core;

/// <summary>
/// Load() must never throw: any missing/corrupt/invalid/future-schema config
/// returns null so callers fall back to AppConfiguration.CreateDefault
/// ("never require perfect previous state to start").
/// </summary>
public interface IConfigurationStore
{
    AppConfiguration? Load();
    void Save(AppConfiguration config);
}
```

```csharp
// src/WindowsSpaces.Core/AppConfiguration.cs
namespace WindowsSpaces.Core;

public sealed record AppConfiguration(
    int SchemaVersion,
    IReadOnlyList<MonitorWorkspaceConfig> Monitors,
    IReadOnlyList<HotkeyBinding> Hotkeys)
{
    public const int CurrentSchemaVersion = 1;
    private const int MaxWorkspacesPerMonitor = 9;

    public static AppConfiguration CreateDefault(IEnumerable<Monitor> monitors)
    {
        var monitorConfigs = monitors
            .Select(m => new MonitorWorkspaceConfig(m.Id, new[]
            {
                new WorkspaceDefinition($"{m.Id}:1", "Space 1", 1),
                new WorkspaceDefinition($"{m.Id}:2", "Space 2", 2)
            }))
            .ToList();

        var hotkeys = new List<HotkeyBinding>
        {
            new(HotkeyAction.SwitchWorkspace, 1, ModifierKeys.Control | ModifierKeys.Alt, 0x31),
            new(HotkeyAction.SwitchWorkspace, 2, ModifierKeys.Control | ModifierKeys.Alt, 0x32),
            new(HotkeyAction.MoveToWorkspace, 1, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x31),
            new(HotkeyAction.MoveToWorkspace, 2, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x32),
            new(HotkeyAction.ShowAllWindows, 0, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x1B)
        };

        return new AppConfiguration(CurrentSchemaVersion, monitorConfigs, hotkeys);
    }

    public bool Validate(out string? error)
    {
        foreach (var monitor in Monitors)
        {
            if (monitor.Workspaces.Count is < 1 or > MaxWorkspacesPerMonitor)
            {
                error = $"Monitor {monitor.MonitorId} must have between 1 and {MaxWorkspacesPerMonitor} workspaces.";
                return false;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var workspace in monitor.Workspaces)
            {
                if (string.IsNullOrWhiteSpace(workspace.Name))
                {
                    error = $"Monitor {monitor.MonitorId} has a workspace with an empty name.";
                    return false;
                }

                if (!names.Add(workspace.Name))
                {
                    error = $"Monitor {monitor.MonitorId} has duplicate workspace name '{workspace.Name}'.";
                    return false;
                }
            }
        }

        for (var i = 0; i < Hotkeys.Count; i++)
        {
            for (var j = i + 1; j < Hotkeys.Count; j++)
            {
                if (Hotkeys[i].ConflictsWith(Hotkeys[j]))
                {
                    error = $"Hotkeys for {Hotkeys[i].Action} and {Hotkeys[j].Action} use the same key combination.";
                    return false;
                }
            }
        }

        error = null;
        return true;
    }
}
```

Add `using System.Linq;` at the top of `AppConfiguration.cs` if `ImplicitUsings` doesn't cover it (it does, per the `.csproj` — `ImplicitUsings=enable` includes `System.Linq`).

- [ ] **Step 4: Run test to verify it passes**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter AppConfigurationTests`
Expected: PASS, 8/8.

- [ ] **Step 5: Commit**

```bash
git add src/WindowsSpaces.Core/WorkspaceDefinition.cs src/WindowsSpaces.Core/MonitorWorkspaceConfig.cs src/WindowsSpaces.Core/AppConfiguration.cs src/WindowsSpaces.Core/IConfigurationStore.cs src/WindowsSpaces.Tests/Core/AppConfigurationTests.cs
git commit -m "Add AppConfiguration model, validation, and IConfigurationStore"
```

---

### Task 3: Persistence project — JsonConfigurationStore

**Files:**
- Create: `src/WindowsSpaces.Persistence/WindowsSpaces.Persistence.csproj`
- Create: `src/WindowsSpaces.Persistence/JsonConfigurationStore.cs`
- Modify: `WindowsSpaces.sln` (add project + build config lines)
- Test: `src/WindowsSpaces.Tests/Persistence/JsonConfigurationStoreTests.cs`
- Modify: `src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj` (add ProjectReference to Persistence)

**Interfaces:**
- Consumes: `IConfigurationStore`, `AppConfiguration` (Task 2).
- Produces: `JsonConfigurationStore : IConfigurationStore`, constructor `JsonConfigurationStore(string filePath)`.

- [ ] **Step 1: Create the project file**

```xml
<!-- src/WindowsSpaces.Persistence/WindowsSpaces.Persistence.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>WindowsSpaces.Persistence</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\WindowsSpaces.Core\WindowsSpaces.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add the project to the solution**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet sln WindowsSpaces.sln add src/WindowsSpaces.Persistence/WindowsSpaces.Persistence.csproj`
Expected: `Project ... added to the solution.`

- [ ] **Step 3: Add the Tests -> Persistence project reference**

```xml
<!-- src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj, inside the existing <ItemGroup> with ProjectReferences -->
<ProjectReference Include="..\WindowsSpaces.Persistence\WindowsSpaces.Persistence.csproj" />
```

- [ ] **Step 4: Write the failing test**

```csharp
using System;
using System.IO;
using WindowsSpaces.Core;
using WindowsSpaces.Persistence;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.Persistence;

public class JsonConfigurationStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public JsonConfigurationStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WindowsSpacesTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static readonly Monitor MonA = new("MON-A", "\\\\.\\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), IsPrimary: true);

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var store = new JsonConfigurationStore(_path);

        Assert.Null(store.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new JsonConfigurationStore(_path);
        var original = AppConfiguration.CreateDefault(new[] { MonA });

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(original, loaded);
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        var nestedPath = Path.Combine(_tempDir, "nested", "sub", "config.json");
        var store = new JsonConfigurationStore(nestedPath);

        store.Save(AppConfiguration.CreateDefault(new[] { MonA }));

        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNull()
    {
        File.WriteAllText(_path, "{ not valid json ");
        var store = new JsonConfigurationStore(_path);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_ValidJsonButFailsValidation_ReturnsNull()
    {
        File.WriteAllText(_path, """
        {
          "SchemaVersion": 1,
          "Monitors": [ { "MonitorId": "MON-A", "Workspaces": [] } ],
          "Hotkeys": []
        }
        """);
        var store = new JsonConfigurationStore(_path);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_UnknownFutureSchemaVersion_ReturnsNull()
    {
        var store = new JsonConfigurationStore(_path);
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with { SchemaVersion = AppConfiguration.CurrentSchemaVersion + 1 };
        store.Save(config);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_DoesNotLeaveTempFileBehind()
    {
        var store = new JsonConfigurationStore(_path);
        store.Save(AppConfiguration.CreateDefault(new[] { MonA }));

        var leftoverTempFiles = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.Empty(leftoverTempFiles);
    }
}
```

- [ ] **Step 5: Run test to verify it fails**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter JsonConfigurationStoreTests`
Expected: FAIL to build — `JsonConfigurationStore` doesn't exist.

- [ ] **Step 6: Write minimal implementation**

```csharp
// src/WindowsSpaces.Persistence/JsonConfigurationStore.cs
using System.Text.Json;
using WindowsSpaces.Core;

namespace WindowsSpaces.Persistence;

/// <summary>
/// JSON-file-backed IConfigurationStore. Load() fails open (returns null)
/// on any error — missing file, corrupt JSON, failed Validate(), or a
/// SchemaVersion this build doesn't understand — per the "never require
/// perfect previous state to start" recovery rule. Save() writes to a
/// temp file and swaps it in with File.Move(overwrite: true) so a crash
/// mid-write can't corrupt the existing config.
/// </summary>
public sealed class JsonConfigurationStore : IConfigurationStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _filePath;

    public JsonConfigurationStore(string filePath)
    {
        _filePath = filePath;
    }

    public AppConfiguration? Load()
    {
        if (!File.Exists(_filePath)) return null;

        AppConfiguration? config;
        try
        {
            var json = File.ReadAllText(_filePath);
            config = JsonSerializer.Deserialize<AppConfiguration>(json, Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }

        if (config is null) return null;
        if (config.SchemaVersion != AppConfiguration.CurrentSchemaVersion) return null;
        if (!config.Validate(out _)) return null;

        return config;
    }

    public void Save(AppConfiguration config)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(config, Options));
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
```

Note: `AppConfiguration` and its record members (`MonitorWorkspaceConfig`, `WorkspaceDefinition`, `HotkeyBinding`) all use `IReadOnlyList<T>` properties with positional-record constructors, which `System.Text.Json` deserializes correctly by matching constructor parameter names case-insensitively (default `System.Text.Json` behavior since .NET 7) — no custom converters needed.

- [ ] **Step 7: Run test to verify it passes**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter JsonConfigurationStoreTests`
Expected: PASS, 7/7. If `SaveThenLoad_RoundTrips` fails on list equality, check that `AppConfiguration`'s `IReadOnlyList<T>` properties deserialize into a type with structural equality (e.g. `List<T>` compares by reference, not value, under `record` equality) — if so, change `Monitors`/`Hotkeys`/`Workspaces` properties to compare via `IReadOnlyList<T>` sequence equality by overriding `Equals`/`GetHashCode` on `AppConfiguration` and `MonitorWorkspaceConfig` using `Enumerable.SequenceEqual`, or simplest: change the test assertion to compare `JsonSerializer.Serialize(original)` vs `JsonSerializer.Serialize(loaded)` instead of record equality.

- [ ] **Step 8: Full solution build check**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet build WindowsSpaces.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 9: Commit**

```bash
git add src/WindowsSpaces.Persistence WindowsSpaces.sln src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj src/WindowsSpaces.Tests/Persistence/JsonConfigurationStoreTests.cs
git commit -m "Add WindowsSpaces.Persistence with fail-open JsonConfigurationStore"
```

---

### Task 4: AppHost — config-driven startup, ApplyConfiguration, diagnostics snapshot

**Files:**
- Modify: `src/WindowsSpaces.App/AppHost.cs`
- Create: `src/WindowsSpaces.Core/DiagnosticsSnapshot.cs`
- Test: `src/WindowsSpaces.Tests/Core/WorkspaceManagerConfigTests.cs` (tests the Core-level pieces AppHost relies on: applying a `MonitorWorkspaceConfig` onto `WorkspaceManager`/`WindowTracker` state)
- Modify: `src/WindowsSpaces.Core/WorkspaceManager.cs` (add `RenameWorkspace`, `GetWorkspaceName`/expose active names — see below)

**Interfaces:**
- Consumes: `AppConfiguration`, `IConfigurationStore`, `HotkeyBinding`, `HotkeyAction` (Tasks 1–2); `JsonConfigurationStore` (Task 3, wired only in `AppHost`, the composition root).
- Produces: `DiagnosticsSnapshot(IReadOnlyList<WindowSnapshot> Windows, IReadOnlyList<MonitorSnapshot> Monitors)` with `WindowSnapshot(nint Hwnd, int ProcessId, string? MonitorId, string? WorkspaceId, bool IsVisible, bool IsMinimized, bool IsMaximized)` and `MonitorSnapshot(string MonitorId, string? ActiveWorkspaceId)`; `WorkspaceManager.RenameWorkspace(string workspaceId, string newName)`, `WorkspaceManager.GetWorkspaceNames(string monitorId)` returning `IReadOnlyDictionary<string, string>` (workspaceId -> name) for the diagnostics/settings display; `AppHost.ApplyConfiguration(AppConfiguration config)`; `AppHost.GetDiagnosticsSnapshot()`.

This task doesn't yet touch `WindowsSpaces.App`'s WinUI3 conversion (Task 7) — it only prepares `AppHost` and `Core` so later UI tasks have something to call. `WorkspaceManager` currently has no concept of workspace *names* (only ids used for hide/show routing) — add a lightweight name table since Settings/Diagnostics need to display names.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Drawing;
using WindowsSpaces.Core;
using WindowsSpaces.Tests.Fakes;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class WorkspaceManagerConfigTests
{
    [Fact]
    public void RenameWorkspace_ThenGetWorkspaceNames_ReflectsNewName()
    {
        var windowManager = new FakeWindowManager();
        var monitorManager = new FakeMonitorManager();
        var eventSource = new FakeWindowEventSource();
        var guard = new OperationGuard();
        var tracker = new WindowTracker(windowManager, eventSource, monitorManager, guard);
        var manager = new WorkspaceManager(windowManager, tracker, guard);

        manager.RenameWorkspace("MON-1:1", "Development");

        var names = manager.GetWorkspaceNames("MON-1");
        Assert.Equal("Development", names["MON-1:1"]);
    }

    [Fact]
    public void GetWorkspaceNames_UnknownWorkspace_DefaultsToNotPresent()
    {
        var windowManager = new FakeWindowManager();
        var monitorManager = new FakeMonitorManager();
        var eventSource = new FakeWindowEventSource();
        var guard = new OperationGuard();
        var tracker = new WindowTracker(windowManager, eventSource, monitorManager, guard);
        var manager = new WorkspaceManager(windowManager, tracker, guard);

        var names = manager.GetWorkspaceNames("MON-1");

        Assert.Empty(names);
    }
}
```

Check `src/WindowsSpaces.Tests/Fakes/FakeWindowManager.cs`, `FakeMonitorManager.cs`, `FakeWindowEventSource.cs` constructors match this usage (parameterless) before writing — read them first; if any fake requires constructor arguments, adjust the test to match rather than changing the fakes.

- [ ] **Step 2: Run test to verify it fails**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter WorkspaceManagerConfigTests`
Expected: FAIL to build — `RenameWorkspace`/`GetWorkspaceNames` don't exist.

- [ ] **Step 3: Write minimal implementation**

Add to `src/WindowsSpaces.Core/WorkspaceManager.cs`, inside the `WorkspaceManager` class (near `GetActiveWorkspace`):

```csharp
    private readonly ConcurrentDictionary<string, string> _workspaceNames = new();

    public void RenameWorkspace(string workspaceId, string name) => _workspaceNames[workspaceId] = name;

    /// <summary>Names for all known workspaces on a monitor, keyed by workspace id. Only includes workspaces that have been named via RenameWorkspace.</summary>
    public IReadOnlyDictionary<string, string> GetWorkspaceNames(string monitorId) =>
        _workspaceNames
            .Where(kv => kv.Key.StartsWith(monitorId + ":", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
```

`System.Linq` is available via `ImplicitUsings`; `ConcurrentDictionary` is already imported via `using System.Collections.Concurrent;` at the top of the file.

Create `src/WindowsSpaces.Core/DiagnosticsSnapshot.cs`:

```csharp
namespace WindowsSpaces.Core;

public sealed record WindowSnapshot(
    nint Hwnd,
    int ProcessId,
    string? MonitorId,
    string? WorkspaceId,
    bool IsVisible,
    bool IsMinimized,
    bool IsMaximized);

public sealed record MonitorSnapshot(string MonitorId, string? ActiveWorkspaceId);

public sealed record DiagnosticsSnapshot(
    IReadOnlyList<WindowSnapshot> Windows,
    IReadOnlyList<MonitorSnapshot> Monitors);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter WorkspaceManagerConfigTests`
Expected: PASS, 2/2.

- [ ] **Step 5: Wire AppHost to load config, apply it, and expose ApplyConfiguration/GetDiagnosticsSnapshot**

Read the current `src/WindowsSpaces.App/AppHost.cs` in full before editing (already shown above in this conversation) — replace its constructor/`Start`/hotkey-registration logic. This step has no unit test of its own (`AppHost` wires real Win32 types and isn't unit-testable per the existing pattern — `Program.cs`/`AppHost.cs` are covered only by the manual/hardware acceptance test) — verify by `dotnet build` only.

```csharp
// src/WindowsSpaces.App/AppHost.cs
using WindowsSpaces.Core;
using WindowsSpaces.Persistence;
using WindowsSpaces.Platform.Win32;

namespace WindowsSpaces.App;

public sealed class AppHost : IDisposable
{
    private readonly MonitorApi _monitorApi = new();
    private readonly WindowApi _windowApi = new();
    private readonly WinEventHook _eventSource = new();
    private readonly OperationGuard _guard = new();
    private readonly WindowTracker _tracker;
    private readonly WorkspaceManager _workspaceManager;
    private readonly IConfigurationStore _configStore;
    private HotkeyManager? _hotkeys;
    private TrayIcon? _trayIcon;
    private AppConfiguration _config = null!;
    private nint _messageWindowHwnd;

    public AppHost() : this(new JsonConfigurationStore(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsSpaces", "config.json")))
    {
    }

    public AppHost(IConfigurationStore configStore)
    {
        _configStore = configStore;
        _tracker = new WindowTracker(_windowApi, _eventSource, _monitorApi, _guard);
        _workspaceManager = new WorkspaceManager(_windowApi, _tracker, _guard);
    }

    public void Start(nint messageWindowHwnd)
    {
        _messageWindowHwnd = messageWindowHwnd;
        _tracker.Rescan();
        _eventSource.Start();

        var monitors = _monitorApi.GetMonitors();
        _config = LoadOrDefaultConfiguration(monitors);

        foreach (var monitorConfig in _config.Monitors)
        {
            foreach (var workspace in monitorConfig.Workspaces)
            {
                _workspaceManager.RenameWorkspace(workspace.Id, workspace.Name);
            }
            _workspaceManager.SwitchWorkspace(monitorConfig.MonitorId, monitorConfig.Workspaces[0].Id);
        }

        _hotkeys = new HotkeyManager(messageWindowHwnd);
        RegisterHotkeys(_config.Hotkeys);

        _trayIcon = new TrayIcon(messageWindowHwnd);
        _trayIcon.Show();
    }

    /// <summary>
    /// Combines saved config with fresh defaults for any monitor missing
    /// from it (new/reconnected monitor never seen before), so a partial
    /// or missing config never leaves a monitor unconfigured.
    /// </summary>
    private AppConfiguration LoadOrDefaultConfiguration(IReadOnlyList<Monitor> monitors)
    {
        var saved = _configStore.Load();
        var defaults = AppConfiguration.CreateDefault(monitors);

        if (saved is null) return defaults;

        var savedMonitorIds = saved.Monitors.Select(m => m.MonitorId).ToHashSet();
        var missingMonitors = defaults.Monitors.Where(m => !savedMonitorIds.Contains(m.MonitorId));

        return saved with { Monitors = saved.Monitors.Concat(missingMonitors).ToList() };
    }

    /// <summary>
    /// Applies a Settings/Shortcuts save: renames workspaces live and
    /// re-registers hotkeys. Adding/removing workspaces for a monitor is
    /// not applied live — the caller's UI must tell the user to restart.
    /// </summary>
    public void ApplyConfiguration(AppConfiguration config)
    {
        _config = config;
        _configStore.Save(config);

        foreach (var monitorConfig in config.Monitors)
        {
            foreach (var workspace in monitorConfig.Workspaces)
            {
                _workspaceManager.RenameWorkspace(workspace.Id, workspace.Name);
            }
        }

        _hotkeys?.Dispose();
        _hotkeys = new HotkeyManager(_messageWindowHwnd);
        RegisterHotkeys(config.Hotkeys);
    }

    public AppConfiguration GetConfiguration() => _config;

    public DiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        var windows = _tracker.TrackedWindows.Values
            .Select(w => new WindowSnapshot(w.Hwnd, w.ProcessId, w.MonitorId, w.WorkspaceId, w.IsVisible, w.IsMinimized, w.IsMaximized))
            .ToList();

        var monitors = _monitorApi.GetMonitors()
            .Select(m => new MonitorSnapshot(m.Id, _workspaceManager.GetActiveWorkspace(m.Id)))
            .ToList();

        return new DiagnosticsSnapshot(windows, monitors);
    }

    private void RegisterHotkeys(IReadOnlyList<HotkeyBinding> bindings)
    {
        var id = 1;
        foreach (var binding in bindings)
        {
            var boundId = id++;
            Action callback = binding.Action switch
            {
                HotkeyAction.SwitchWorkspace => () => SwitchCurrentMonitor(binding.WorkspaceIndex),
                HotkeyAction.MoveToWorkspace => () => MoveActiveWindow(binding.WorkspaceIndex),
                HotkeyAction.ShowAllWindows => ShowAllWindows,
                _ => throw new InvalidOperationException($"Unhandled hotkey action {binding.Action}")
            };
            _hotkeys!.Register(boundId, binding.Modifiers, binding.VirtualKey, callback);
        }
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

Add a `ProjectReference` from `WindowsSpaces.App` to `WindowsSpaces.Persistence` in `src/WindowsSpaces.App/WindowsSpaces.App.csproj`:

```xml
<ProjectReference Include="..\WindowsSpaces.Persistence\WindowsSpaces.Persistence.csproj" />
```

- [ ] **Step 6: Build check**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet build WindowsSpaces.sln`
Expected: Build succeeded, 0 errors. If `Environment.GetFolderPath`/`Path.Combine` calls need `using System.IO;` / `using System.Linq;` — `ImplicitUsings` covers both for a `net8.0-windows10.0.19041.0` TFM, but confirm by checking the build output; add explicit `using` statements at the top of `AppHost.cs` if the build reports missing symbols.

- [ ] **Step 7: Run full test suite**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj`
Expected: same pass count as before plus the 2 new tests (only the pre-existing manual-hardware acceptance test fails, as before this plan started).

- [ ] **Step 8: Commit**

```bash
git add src/WindowsSpaces.Core/WorkspaceManager.cs src/WindowsSpaces.Core/DiagnosticsSnapshot.cs src/WindowsSpaces.App/AppHost.cs src/WindowsSpaces.App/WindowsSpaces.App.csproj src/WindowsSpaces.Tests/Core/WorkspaceManagerConfigTests.cs
git commit -m "Wire AppHost to load/apply AppConfiguration and expose diagnostics snapshot"
```

---

### Task 5: TrayIcon — context menu

**Files:**
- Modify: `src/WindowsSpaces.App/TrayIcon.cs`
- Modify: `src/WindowsSpaces.App/AppHost.cs` (route the tray's callback message + menu command messages)
- Modify: `src/WindowsSpaces.App/Program.cs` (forward the tray callback message, not just `WM_HOTKEY`)

**Interfaces:**
- Consumes: `TrayIcon` (existing, Task-untouched constructor signature).
- Produces: `TrayIcon.MenuItemInvoked` event `EventHandler<TrayMenuCommand>`; `enum TrayMenuCommand { Settings, Shortcuts, Diagnostics, ShowAllWindows, Exit }`; `TrayIcon.HandleMessage(uint message, nint wParam, nint lParam)` (mirrors `HotkeyManager.HandleMessage`'s shape for consistency).

No unit test here — `TrayIcon` is pure Win32 interop (`Shell_NotifyIcon`/`TrackPopupMenu`), same category as the existing untested `TrayIcon.cs`/`Program.cs`. Verify via `dotnet build` only, consistent with how `HotkeyManager`'s actual `RegisterHotKey` call is only integration-tested (and that integration test needs real hardware, per `MonitorApiTests`/`WindowApiTests`).

- [ ] **Step 1: Rewrite TrayIcon.cs with a context menu**

```csharp
// src/WindowsSpaces.App/TrayIcon.cs
using System.Runtime.InteropServices;

namespace WindowsSpaces.App;

public enum TrayMenuCommand
{
    Settings,
    Shortcuts,
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
    private const int NIF_TIP = 0x4;
    private const int NIM_ADD = 0x0;
    private const int NIM_MODIFY = 0x1;
    private const int NIM_DELETE = 0x2;

    private const uint WM_APP = 0x8000;
    private const uint TrayCallbackMessage = WM_APP;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_COMMAND = 0x0111;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

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
        (TrayMenuCommand.Diagnostics, "Diagnostics"),
        (TrayMenuCommand.ShowAllWindows, "Show All Windows"),
        (TrayMenuCommand.Exit, "Exit")
    };

    private readonly nint _hwnd;
    private NOTIFYICONDATA _data;
    private bool _added;

    public event EventHandler<TrayMenuCommand>? MenuItemInvoked;

    public TrayIcon(nint hwnd)
    {
        _hwnd = hwnd;
        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_TIP,
            uCallbackMessage = (int)TrayCallbackMessage,
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

    /// <summary>Call from the App's message loop for every received message.</summary>
    public void HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (message != TrayCallbackMessage) return;

        var mouseMessage = (uint)lParam;
        if (mouseMessage is not (WM_RBUTTONUP or WM_LBUTTONUP)) return;

        ShowContextMenuAndInvoke();
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
    }
}
```

- [ ] **Step 2: Wire AppHost to handle tray commands**

In `src/WindowsSpaces.App/AppHost.cs`, in `Start`, after `_trayIcon.Show();`, add:

```csharp
        _trayIcon.MenuItemInvoked += OnTrayMenuItemInvoked;
```

Add the handler and forward the tray's `HandleMessage`:

```csharp
    private void OnTrayMenuItemInvoked(object? sender, TrayMenuCommand command)
    {
        switch (command)
        {
            case TrayMenuCommand.ShowAllWindows:
                ShowAllWindows();
                break;
            case TrayMenuCommand.Exit:
                Environment.Exit(0);
                break;
            case TrayMenuCommand.Settings:
            case TrayMenuCommand.Shortcuts:
            case TrayMenuCommand.Diagnostics:
                // Wired to open the corresponding window in Task 8-10.
                break;
        }
    }

    public void HandleTrayMessage(uint message, nint wParam, nint lParam) => _trayIcon?.HandleMessage(message, wParam, lParam);
```

- [ ] **Step 3: Forward the tray callback message from Program.cs's message loop**

In `src/WindowsSpaces.App/Program.cs`, the loop currently only special-cases `WM_HOTKEY`. Add the tray callback constant and forward it too:

```csharp
    private const uint WM_APP = 0x8000;
```

Change the loop body:

```csharp
        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY)
            {
                _host.HandleMessage(msg.message, msg.wParam);
            }
            else if (msg.message == WM_APP)
            {
                _host.HandleTrayMessage(msg.message, msg.wParam, msg.lParam);
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
```

- [ ] **Step 4: Build check**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet build WindowsSpaces.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/WindowsSpaces.App/TrayIcon.cs src/WindowsSpaces.App/AppHost.cs src/WindowsSpaces.App/Program.cs
git commit -m "Add tray icon context menu (Settings/Shortcuts/Diagnostics/Show All/Exit)"
```

---

### Task 6: App view-models — Settings, Shortcuts, Diagnostics (pure C#, unit-tested)

**Files:**
- Create: `src/WindowsSpaces.App/ViewModels/SettingsViewModel.cs`
- Create: `src/WindowsSpaces.App/ViewModels/ShortcutSettingsViewModel.cs`
- Create: `src/WindowsSpaces.App/ViewModels/DiagnosticsViewModel.cs`
- Test: `src/WindowsSpaces.Tests/App/SettingsViewModelTests.cs`
- Test: `src/WindowsSpaces.Tests/App/ShortcutSettingsViewModelTests.cs`
- Test: `src/WindowsSpaces.Tests/App/DiagnosticsViewModelTests.cs`
- Modify: `src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj` (add ProjectReference to `WindowsSpaces.App`)

**Interfaces:**
- Consumes: `AppConfiguration`, `MonitorWorkspaceConfig`, `WorkspaceDefinition`, `HotkeyBinding`, `HotkeyAction`, `IConfigurationStore` (Tasks 1–3); `DiagnosticsSnapshot` (Task 4).
- Produces:
  - `SettingsViewModel(AppConfiguration current)` with `IReadOnlyList<MonitorWorkspaceConfig> Monitors { get; private set; }`, `void AddWorkspace(string monitorId)`, `void RemoveWorkspace(string monitorId, string workspaceId)`, `void RenameWorkspace(string monitorId, string workspaceId, string newName)`, `bool TrySave(out AppConfiguration updated, out string? error)`.
  - `ShortcutSettingsViewModel(AppConfiguration current)` with `IReadOnlyList<HotkeyBinding> Bindings { get; private set; }`, `void Rebind(HotkeyAction action, int workspaceIndex, ModifierKeys modifiers, int virtualKey)`, `bool TrySave(out AppConfiguration updated, out string? error)`.
  - `DiagnosticsViewModel` with `void UpdateSnapshot(DiagnosticsSnapshot snapshot)`, `IReadOnlyList<WindowSnapshot> Windows { get; }`, `IReadOnlyList<MonitorSnapshot> Monitors { get; }`.

- [ ] **Step 1: Add WindowsSpaces.Tests -> WindowsSpaces.App project reference**

```xml
<!-- src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj -->
<ProjectReference Include="..\WindowsSpaces.App\WindowsSpaces.App.csproj" />
```

`WindowsSpaces.App` targets `net8.0-windows10.0.19041.0` (a superset of `WindowsSpaces.Tests`'s `net8.0-windows`), so the reference is valid.

- [ ] **Step 2: Write the failing tests**

```csharp
// src/WindowsSpaces.Tests/App/SettingsViewModelTests.cs
using System.Drawing;
using System.Linq;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.App;

public class SettingsViewModelTests
{
    private static readonly Monitor MonA = new("MON-A", "\\\\.\\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true);

    [Fact]
    public void AddWorkspace_AppendsWithNextIndexAndDefaultName()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);

        vm.AddWorkspace("MON-A");

        var monitor = vm.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.Equal(3, monitor.Workspaces.Count);
        Assert.Equal("MON-A:3", monitor.Workspaces[2].Id);
        Assert.Equal("Space 3", monitor.Workspaces[2].Name);
    }

    [Fact]
    public void RemoveWorkspace_RemovesIt()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);

        vm.RemoveWorkspace("MON-A", "MON-A:2");

        var monitor = vm.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.Single(monitor.Workspaces);
        Assert.Equal("MON-A:1", monitor.Workspaces[0].Id);
    }

    [Fact]
    public void RenameWorkspace_ChangesName()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);

        vm.RenameWorkspace("MON-A", "MON-A:1", "Development");

        var monitor = vm.Monitors.Single(m => m.MonitorId == "MON-A");
        Assert.Equal("Development", monitor.Workspaces[0].Name);
    }

    [Fact]
    public void TrySave_ValidState_ReturnsTrueWithUpdatedConfig()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);
        vm.RenameWorkspace("MON-A", "MON-A:1", "Development");

        var saved = vm.TrySave(out var updated, out var error);

        Assert.True(saved);
        Assert.Null(error);
        Assert.Equal("Development", updated.Monitors.Single().Workspaces[0].Name);
    }

    [Fact]
    public void TrySave_DuplicateNames_ReturnsFalseWithError()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);
        vm.RenameWorkspace("MON-A", "MON-A:1", "Same");
        vm.RenameWorkspace("MON-A", "MON-A:2", "Same");

        var saved = vm.TrySave(out _, out var error);

        Assert.False(saved);
        Assert.NotNull(error);
    }

    [Fact]
    public void RemoveWorkspace_LastOneOnMonitor_TrySaveFails()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new SettingsViewModel(config);
        vm.RemoveWorkspace("MON-A", "MON-A:1");
        vm.RemoveWorkspace("MON-A", "MON-A:2");

        var saved = vm.TrySave(out _, out var error);

        Assert.False(saved);
        Assert.NotNull(error);
    }
}
```

```csharp
// src/WindowsSpaces.Tests/App/ShortcutSettingsViewModelTests.cs
using System.Linq;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.App;

public class ShortcutSettingsViewModelTests
{
    private static readonly Monitor MonA = new("MON-A", "\\\\.\\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), IsPrimary: true);

    [Fact]
    public void Rebind_ChangesTheMatchingBinding()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new ShortcutSettingsViewModel(config);

        vm.Rebind(HotkeyAction.SwitchWorkspace, workspaceIndex: 1, ModifierKeys.Control, virtualKey: 0x39);

        var binding = vm.Bindings.Single(b => b.Action == HotkeyAction.SwitchWorkspace && b.WorkspaceIndex == 1);
        Assert.Equal(ModifierKeys.Control, binding.Modifiers);
        Assert.Equal(0x39, binding.VirtualKey);
    }

    [Fact]
    public void TrySave_ConflictingRebind_ReturnsFalseWithError()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new ShortcutSettingsViewModel(config);

        vm.Rebind(HotkeyAction.SwitchWorkspace, 1, ModifierKeys.Control | ModifierKeys.Alt, 0x32);

        var saved = vm.TrySave(out _, out var error);

        Assert.False(saved);
        Assert.NotNull(error);
    }

    [Fact]
    public void TrySave_NonConflictingRebind_ReturnsTrue()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new ShortcutSettingsViewModel(config);

        vm.Rebind(HotkeyAction.SwitchWorkspace, 1, ModifierKeys.Control, 0x39);

        var saved = vm.TrySave(out var updated, out var error);

        Assert.True(saved);
        Assert.Null(error);
        Assert.Contains(updated.Hotkeys, b => b.Action == HotkeyAction.SwitchWorkspace && b.WorkspaceIndex == 1 && b.VirtualKey == 0x39);
    }
}
```

```csharp
// src/WindowsSpaces.Tests/App/DiagnosticsViewModelTests.cs
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;
using Xunit;

namespace WindowsSpaces.Tests.App;

public class DiagnosticsViewModelTests
{
    [Fact]
    public void UpdateSnapshot_ReplacesWindowsAndMonitors()
    {
        var vm = new DiagnosticsViewModel();
        var snapshot = new DiagnosticsSnapshot(
            new[] { new WindowSnapshot((nint)1, 100, "MON-A", "MON-A:1", true, false, false) },
            new[] { new MonitorSnapshot("MON-A", "MON-A:1") });

        vm.UpdateSnapshot(snapshot);

        Assert.Single(vm.Windows);
        Assert.Single(vm.Monitors);
        Assert.Equal("MON-A:1", vm.Windows[0].WorkspaceId);
    }

    [Fact]
    public void InitialState_IsEmpty()
    {
        var vm = new DiagnosticsViewModel();

        Assert.Empty(vm.Windows);
        Assert.Empty(vm.Monitors);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter "SettingsViewModelTests|ShortcutSettingsViewModelTests|DiagnosticsViewModelTests"`
Expected: FAIL to build — the `ViewModels` types don't exist.

- [ ] **Step 4: Write minimal implementation**

```csharp
// src/WindowsSpaces.App/ViewModels/SettingsViewModel.cs
using WindowsSpaces.Core;

namespace WindowsSpaces.App.ViewModels;

public sealed class SettingsViewModel
{
    private readonly AppConfiguration _original;
    private List<MonitorWorkspaceConfig> _monitors;

    public SettingsViewModel(AppConfiguration current)
    {
        _original = current;
        _monitors = current.Monitors.Select(m => m with { Workspaces = m.Workspaces.ToList() }).ToList();
    }

    public IReadOnlyList<MonitorWorkspaceConfig> Monitors => _monitors;

    public void AddWorkspace(string monitorId)
    {
        var index = IndexOf(monitorId);
        var monitor = _monitors[index];
        var nextIndex = monitor.Workspaces.Count == 0 ? 1 : monitor.Workspaces.Max(w => w.Index) + 1;
        var workspaces = monitor.Workspaces.ToList();
        workspaces.Add(new WorkspaceDefinition($"{monitorId}:{nextIndex}", $"Space {nextIndex}", nextIndex));
        _monitors[index] = monitor with { Workspaces = workspaces };
    }

    public void RemoveWorkspace(string monitorId, string workspaceId)
    {
        var index = IndexOf(monitorId);
        var monitor = _monitors[index];
        var workspaces = monitor.Workspaces.Where(w => w.Id != workspaceId).ToList();
        _monitors[index] = monitor with { Workspaces = workspaces };
    }

    public void RenameWorkspace(string monitorId, string workspaceId, string newName)
    {
        var index = IndexOf(monitorId);
        var monitor = _monitors[index];
        var workspaces = monitor.Workspaces
            .Select(w => w.Id == workspaceId ? w with { Name = newName } : w)
            .ToList();
        _monitors[index] = monitor with { Workspaces = workspaces };
    }

    public bool TrySave(out AppConfiguration updated, out string? error)
    {
        var candidate = _original with { Monitors = _monitors };
        if (!candidate.Validate(out error))
        {
            updated = _original;
            return false;
        }

        updated = candidate;
        return true;
    }

    private int IndexOf(string monitorId) =>
        _monitors.FindIndex(m => m.MonitorId == monitorId) is var i and >= 0
            ? i
            : throw new ArgumentException($"Unknown monitor '{monitorId}'", nameof(monitorId));
}
```

```csharp
// src/WindowsSpaces.App/ViewModels/ShortcutSettingsViewModel.cs
using WindowsSpaces.Core;

namespace WindowsSpaces.App.ViewModels;

public sealed class ShortcutSettingsViewModel
{
    private readonly AppConfiguration _original;
    private List<HotkeyBinding> _bindings;

    public ShortcutSettingsViewModel(AppConfiguration current)
    {
        _original = current;
        _bindings = current.Hotkeys.ToList();
    }

    public IReadOnlyList<HotkeyBinding> Bindings => _bindings;

    public void Rebind(HotkeyAction action, int workspaceIndex, ModifierKeys modifiers, int virtualKey)
    {
        var index = _bindings.FindIndex(b => b.Action == action && b.WorkspaceIndex == workspaceIndex);
        if (index < 0)
        {
            throw new ArgumentException($"No existing binding for {action}/{workspaceIndex}");
        }

        _bindings[index] = _bindings[index] with { Modifiers = modifiers, VirtualKey = virtualKey };
    }

    public bool TrySave(out AppConfiguration updated, out string? error)
    {
        var candidate = _original with { Hotkeys = _bindings };
        if (!candidate.Validate(out error))
        {
            updated = _original;
            return false;
        }

        updated = candidate;
        return true;
    }
}
```

```csharp
// src/WindowsSpaces.App/ViewModels/DiagnosticsViewModel.cs
using WindowsSpaces.Core;

namespace WindowsSpaces.App.ViewModels;

public sealed class DiagnosticsViewModel
{
    public IReadOnlyList<WindowSnapshot> Windows { get; private set; } = Array.Empty<WindowSnapshot>();
    public IReadOnlyList<MonitorSnapshot> Monitors { get; private set; } = Array.Empty<MonitorSnapshot>();

    public void UpdateSnapshot(DiagnosticsSnapshot snapshot)
    {
        Windows = snapshot.Windows;
        Monitors = snapshot.Monitors;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter "SettingsViewModelTests|ShortcutSettingsViewModelTests|DiagnosticsViewModelTests"`
Expected: PASS, 11/11.

- [ ] **Step 6: Full solution build + full test suite**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet build WindowsSpaces.sln && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj`
Expected: build succeeds; only the pre-existing manual/hardware acceptance test fails.

- [ ] **Step 7: Commit**

```bash
git add src/WindowsSpaces.App/ViewModels src/WindowsSpaces.Tests/App src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj
git commit -m "Add Settings/Shortcuts/Diagnostics view-models with unit tests"
```

---

### Task 7: WinUI3 bootstrap — add Windows App SDK to WindowsSpaces.App

**Files:**
- Modify: `src/WindowsSpaces.App/WindowsSpaces.App.csproj`
- Create: `src/WindowsSpaces.App/App.xaml`
- Create: `src/WindowsSpaces.App/App.xaml.cs`
- Create: `src/WindowsSpaces.App/app.manifest`
- Modify: `src/WindowsSpaces.App/Program.cs`

No unit tests — this task is pure bootstrap plumbing (framework initialization) with nothing to assert against; verified by `dotnet build` only. **This is the highest-risk task in the plan**: Windows App SDK unpackaged deployment requires the `Microsoft.WindowsAppSDK` NuGet package plus `<WindowsPackageType>None</WindowsPackageType>` and a self-contained runtime identifier, and first-time restore of `Microsoft.WindowsAppSDK` can be slow (~5+ min) or hit network/proxy issues in this environment — if `dotnet restore` fails or hangs past 5 minutes, stop and report the exact error rather than retrying blindly.

- [ ] **Step 1: Add the Windows App SDK package reference and required properties**

```xml
<!-- src/WindowsSpaces.App/WindowsSpaces.App.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>WindowsSpaces.App</RootNamespace>
    <Platforms>x64</Platforms>
    <RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
    <UseWinUI>true</UseWinUI>
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <SelfContained>false</SelfContained>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.6.250228001" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.1742" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\WindowsSpaces.Core\WindowsSpaces.Core.csproj" />
    <ProjectReference Include="..\WindowsSpaces.Platform\WindowsSpaces.Platform.csproj" />
    <ProjectReference Include="..\WindowsSpaces.Persistence\WindowsSpaces.Persistence.csproj" />
  </ItemGroup>

</Project>
```

If `dotnet build` reports the `Microsoft.WindowsAppSDK` version above is unavailable/deprecated, run `dotnet package search Microsoft.WindowsAppSDK` (or check https://www.nuget.org/packages/Microsoft.WindowsAppSDK) and use the latest stable 1.6.x or 1.7.x version instead — do not guess further versions blindly.

- [ ] **Step 2: Add app.manifest (per-monitor DPI awareness, required by FR/§10 DPI requirements)**

```xml
<!-- src/WindowsSpaces.App/app.manifest -->
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

- [ ] **Step 3: Add App.xaml / App.xaml.cs**

```xml
<!-- src/WindowsSpaces.App/App.xaml -->
<Application
    x:Class="WindowsSpaces.App.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
</Application>
```

```csharp
// src/WindowsSpaces.App/App.xaml.cs
using Microsoft.UI.Xaml;

namespace WindowsSpaces.App;

public partial class App : Application
{
    private AppHost? _host;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _host = new AppHost();
        // The existing Win32 message-only window + hotkey pump in Program.cs
        // still owns hotkey/tray message delivery; AppHost.Start is called
        // from there, not here, so this hook exists only for future WinUI
        // window-lifetime coordination (Tasks 8-10 open windows on demand).
    }
}
```

- [ ] **Step 4: Update Program.cs to bootstrap the WinUI3 dispatcher alongside the existing message loop**

Read the current `src/WindowsSpaces.App/Program.cs` in full (already shown above) before editing. Replace the `Main` method body — keep the message-only window and `GetMessage` loop exactly as-is (hotkeys/tray still depend on it), but wrap the app lifetime with `Microsoft.UI.Xaml.Application.Start` so WinUI windows (Tasks 8-10) can be created on this thread:

```csharp
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using WindowsSpaces.App;

internal static class Program
{
    // ... existing WM_HOTKEY/WM_DESTROY consts, DllImports, structs unchanged ...

    [STAThread]
    private static void Main(string[] args)
    {
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
            RunMessageWindowLoop();
        });
    }

    private static void RunMessageWindowLoop()
    {
        _wndProcDelegate = WndProcHandler;
        var hInstance = GetModuleHandle(null);

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProcDelegate,
            hInstance = hInstance,
            lpszClassName = "WindowsSpacesMessageWindow",
            lpszMenuName = string.Empty
        };
        if (RegisterClassEx(ref wc) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed, Win32 error {Marshal.GetLastWin32Error()}");
        }

        var hwnd = CreateWindowEx(0, "WindowsSpacesMessageWindow", "WindowsSpaces", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, hInstance, 0);
        if (hwnd == 0)
        {
            throw new InvalidOperationException($"Failed to create message-only window for hotkey/tray hosting, Win32 error {Marshal.GetLastWin32Error()}");
        }

        _host = new AppHost();
        _host.Start(hwnd);

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY)
            {
                _host.HandleMessage(msg.message, msg.wParam);
            }
            else if (msg.message == WM_APP)
            {
                _host.HandleTrayMessage(msg.message, msg.wParam, msg.lParam);
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        _host.Dispose();
    }

    // ... existing WndProcHandler unchanged ...
}
```

This keeps `AppHost` construction/`Start` exactly where it was (inside the same raw `GetMessage` loop) so hotkeys/tray behavior is unaffected; the only change is that the whole thing now runs inside `Application.Start`'s callback so `Microsoft.UI.Xaml.Window` instances (Tasks 8-10) can be created later on this same thread via its dispatcher.

- [ ] **Step 5: Build check**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet build WindowsSpaces.sln`
Expected: Build succeeded. This is the step most likely to surface Windows App SDK version/tooling issues — if it fails, capture the full error text before attempting any fix, since the fix depends entirely on what fails (missing workload, wrong SDK version, missing `Microsoft.Windows.SDK.BuildTools`, etc.).

- [ ] **Step 6: Run full test suite (regression check — this task shouldn't change test behavior)**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj`
Expected: same pass/fail counts as Task 6's end state.

- [ ] **Step 7: Commit**

```bash
git add src/WindowsSpaces.App/WindowsSpaces.App.csproj src/WindowsSpaces.App/App.xaml src/WindowsSpaces.App/App.xaml.cs src/WindowsSpaces.App/app.manifest src/WindowsSpaces.App/Program.cs
git commit -m "Bootstrap WinUI3 (Windows App SDK, unpackaged) alongside existing Win32 message loop"
```

---

### Task 8: SettingsWindow (XAML) wired to SettingsViewModel

**Files:**
- Create: `src/WindowsSpaces.App/Views/SettingsWindow.xaml`
- Create: `src/WindowsSpaces.App/Views/SettingsWindow.xaml.cs`
- Modify: `src/WindowsSpaces.App/AppHost.cs` (open the window on `TrayMenuCommand.Settings`)

No new unit tests — `SettingsViewModel` is already fully tested (Task 6); this task only adds thin binding code. Verify via `dotnet build` only.

- [ ] **Step 1: Add the XAML window**

```xml
<!-- src/WindowsSpaces.App/Views/SettingsWindow.xaml -->
<Window
    x:Class="WindowsSpaces.App.Views.SettingsWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <Grid Padding="16" RowSpacing="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <ListView x:Name="MonitorsList" Grid.Row="0"/>

        <StackPanel Grid.Row="1" Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right">
            <TextBlock x:Name="ErrorText" Foreground="Red" VerticalAlignment="Center"/>
            <Button x:Name="SaveButton" Content="Save" Click="OnSaveClicked"/>
            <Button x:Name="CancelButton" Content="Cancel" Click="OnCancelClicked"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 2: Add the code-behind**

```csharp
// src/WindowsSpaces.App/Views/SettingsWindow.xaml.cs
using Microsoft.UI.Xaml;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly Action<AppConfiguration> _onSaved;

    public SettingsWindow(AppConfiguration current, Action<AppConfiguration> onSaved)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(current);
        _onSaved = onSaved;
        MonitorsList.ItemsSource = _viewModel.Monitors;
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TrySave(out var updated, out var error))
        {
            _onSaved(updated);
            Close();
        }
        else
        {
            ErrorText.Text = error;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
```

Note: `MonitorsList` binds to `IReadOnlyList<MonitorWorkspaceConfig>` for display only in this task — per-workspace add/remove/rename controls inside the `ListView`'s `ItemTemplate` (calling `_viewModel.AddWorkspace`/`RemoveWorkspace`/`RenameWorkspace` and refreshing `MonitorsList.ItemsSource`) are a UI-polish follow-up, not required for the window to build and open; `SettingsViewModel`'s methods are already fully covered by Task 6's unit tests regardless of how much of the XAML surface is wired up.

- [ ] **Step 3: Wire AppHost to open the window**

In `src/WindowsSpaces.App/AppHost.cs`, add `using WindowsSpaces.App.Views;` and change the `Settings` case in `OnTrayMenuItemInvoked`:

```csharp
            case TrayMenuCommand.Settings:
                new SettingsWindow(_config, ApplyConfiguration).Activate();
                break;
```

- [ ] **Step 4: Build check**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet build WindowsSpaces.sln`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/WindowsSpaces.App/Views/SettingsWindow.xaml src/WindowsSpaces.App/Views/SettingsWindow.xaml.cs src/WindowsSpaces.App/AppHost.cs
git commit -m "Add SettingsWindow bound to SettingsViewModel"
```

---

### Task 9: ShortcutSettingsWindow (XAML) wired to ShortcutSettingsViewModel

**Files:**
- Create: `src/WindowsSpaces.App/Views/ShortcutSettingsWindow.xaml`
- Create: `src/WindowsSpaces.App/Views/ShortcutSettingsWindow.xaml.cs`
- Modify: `src/WindowsSpaces.App/AppHost.cs` (open the window on `TrayMenuCommand.Shortcuts`)

No new unit tests — same rationale as Task 8; `ShortcutSettingsViewModel` is fully covered by Task 6.

- [ ] **Step 1: Add the XAML window**

```xml
<!-- src/WindowsSpaces.App/Views/ShortcutSettingsWindow.xaml -->
<Window
    x:Class="WindowsSpaces.App.Views.ShortcutSettingsWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <Grid Padding="16" RowSpacing="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <ListView x:Name="BindingsList" Grid.Row="0"/>

        <StackPanel Grid.Row="1" Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right">
            <TextBlock x:Name="ErrorText" Foreground="Red" VerticalAlignment="Center"/>
            <Button x:Name="SaveButton" Content="Save" Click="OnSaveClicked"/>
            <Button x:Name="CancelButton" Content="Cancel" Click="OnCancelClicked"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 2: Add the code-behind**

```csharp
// src/WindowsSpaces.App/Views/ShortcutSettingsWindow.xaml.cs
using Microsoft.UI.Xaml;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;

namespace WindowsSpaces.App.Views;

public sealed partial class ShortcutSettingsWindow : Window
{
    private readonly ShortcutSettingsViewModel _viewModel;
    private readonly Action<AppConfiguration> _onSaved;

    public ShortcutSettingsWindow(AppConfiguration current, Action<AppConfiguration> onSaved)
    {
        InitializeComponent();
        _viewModel = new ShortcutSettingsViewModel(current);
        _onSaved = onSaved;
        BindingsList.ItemsSource = _viewModel.Bindings;
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TrySave(out var updated, out var error))
        {
            _onSaved(updated);
            Close();
        }
        else
        {
            ErrorText.Text = error;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
```

Note: as in Task 8, the "press new combination" key-capture control (calling `_viewModel.Rebind` per row) is a UI-polish follow-up on top of this scaffold — `Rebind`'s logic is already fully unit-tested in Task 6.

- [ ] **Step 3: Wire AppHost to open the window**

```csharp
            case TrayMenuCommand.Shortcuts:
                new ShortcutSettingsWindow(_config, ApplyConfiguration).Activate();
                break;
```

- [ ] **Step 4: Build check**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet build WindowsSpaces.sln`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/WindowsSpaces.App/Views/ShortcutSettingsWindow.xaml src/WindowsSpaces.App/Views/ShortcutSettingsWindow.xaml.cs src/WindowsSpaces.App/AppHost.cs
git commit -m "Add ShortcutSettingsWindow bound to ShortcutSettingsViewModel"
```

---

### Task 10: DiagnosticsWindow (XAML) wired to DiagnosticsViewModel

**Files:**
- Create: `src/WindowsSpaces.App/Views/DiagnosticsWindow.xaml`
- Create: `src/WindowsSpaces.App/Views/DiagnosticsWindow.xaml.cs`
- Modify: `src/WindowsSpaces.App/AppHost.cs` (open the window on `TrayMenuCommand.Diagnostics`)

No new unit tests — `DiagnosticsViewModel` is fully covered by Task 6.

- [ ] **Step 1: Add the XAML window**

```xml
<!-- src/WindowsSpaces.App/Views/DiagnosticsWindow.xaml -->
<Window
    x:Class="WindowsSpaces.App.Views.DiagnosticsWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <Grid Padding="16" RowSpacing="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <ListView x:Name="MonitorsList" Grid.Row="0"/>
        <ListView x:Name="WindowsList" Grid.Row="1"/>
    </Grid>
</Window>
```

- [ ] **Step 2: Add the code-behind, polling AppHost.GetDiagnosticsSnapshot on a 1s DispatcherTimer while open**

```csharp
// src/WindowsSpaces.App/Views/DiagnosticsWindow.xaml.cs
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WindowsSpaces.App.ViewModels;

namespace WindowsSpaces.App.Views;

public sealed partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticsViewModel _viewModel = new();
    private readonly Func<WindowsSpaces.Core.DiagnosticsSnapshot> _getSnapshot;
    private readonly DispatcherQueueTimer _timer;

    public DiagnosticsWindow(Func<WindowsSpaces.Core.DiagnosticsSnapshot> getSnapshot)
    {
        InitializeComponent();
        _getSnapshot = getSnapshot;

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => RefreshSnapshot();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();

        RefreshSnapshot();
    }

    private void RefreshSnapshot()
    {
        _viewModel.UpdateSnapshot(_getSnapshot());
        MonitorsList.ItemsSource = _viewModel.Monitors;
        WindowsList.ItemsSource = _viewModel.Windows;
    }
}
```

- [ ] **Step 3: Wire AppHost to open the window**

```csharp
            case TrayMenuCommand.Diagnostics:
                new DiagnosticsWindow(GetDiagnosticsSnapshot).Activate();
                break;
```

- [ ] **Step 4: Build check**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet build WindowsSpaces.sln`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/WindowsSpaces.App/Views/DiagnosticsWindow.xaml src/WindowsSpaces.App/Views/DiagnosticsWindow.xaml.cs src/WindowsSpaces.App/AppHost.cs
git commit -m "Add DiagnosticsWindow bound to DiagnosticsViewModel with 1s live polling"
```

---

### Task 11: Final full-solution verification and progress ledger update

**Files:**
- Modify: `.superpowers/sdd/progress.md`

- [ ] **Step 1: Full clean build**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet clean WindowsSpaces.sln && dotnet build WindowsSpaces.sln`
Expected: Build succeeded, 0 errors, for all 7 projects (Core, Platform, Persistence, App, TestApp, Tests, plus the new Persistence project).

- [ ] **Step 2: Full test run**

Run: `export PATH="/c/Program Files/dotnet:$PATH"; cd "F:\MultiMonitor Project" && dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj -v normal`
Expected: All tests pass except the one pre-existing manual/hardware acceptance test (`WorkspaceManagerAcceptanceTests`) that requires `WindowsSpaces.TestApp` running — same single known failure as at the start of this plan, nothing new.

- [ ] **Step 3: Update the progress ledger**

Read `.superpowers/sdd/progress.md` first, then append a new section describing Phase 2 completion: projects touched, test counts (unit vs. the one manual/hardware exception), and explicitly note the app was never launched during this work — diagnostics/settings/shortcuts windows are build-verified and unit-tested via their view-models only, not visually verified.

- [ ] **Step 4: Commit**

```bash
git add .superpowers/sdd/progress.md
git commit -m "Record Phase 2 (Product UI) completion in progress ledger"
```
