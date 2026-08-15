# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Product

Windows Spaces: a native Windows 11 utility giving each physical monitor its own
independent set of virtual workspaces ("Spaces"). Switching the workspace on one
monitor must never change another monitor's workspace. The full spec, requirements,
architecture rationale, and ADRs live in `windows_spaces_technical_documentation.md` —
read it before making architectural decisions.

## Build / test commands

Requires the .NET SDK pinned in `global.json` (10.0.400) and Windows (WinUI/Win32 APIs
throughout — this does not build or run on non-Windows).

```
dotnet build WindowsSpaces.sln
dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj
dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter "FullyQualifiedName~WorkspaceManagerTests"
dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter "FullyQualifiedName~WorkspaceManagerTests.SwitchWorkspace_LatestRequestWins"
```

`WindowsSpaces.App` targets `net8.0-windows10.0.19041.0` with `UseWinUI=true` and
builds x64-only (`Platforms=x64`); the test project references it with
`<SetPlatform>Platform=x64</SetPlatform>`. If a build fails with a platform-mapping
error, check the `ProjectConfigurationPlatforms` section of `WindowsSpaces.sln` rather
than editing csproj platform properties directly.

Unpackaged WinUI3 apps require the compiled resource index to be named literally
`resources.pri` next to the exe (`ProjectPriFileName` in the App csproj handles this) —
don't remove that property, the app crashes on launch inside `Microsoft.UI.Xaml.dll`
without it.

There's no lint command; treat compiler warnings (nullable, etc.) as the linting signal.

## Architecture

Strict layering, enforced by project references (not just convention):

```
WindowsSpaces.App (WinUI3 UI, tray, hotkeys, IPC server)
        |
WindowsSpaces.Persistence (JSON config store)
        |
WindowsSpaces.Platform (Win32/P-Invoke adapters — net8.0-windows)
        |
WindowsSpaces.Core (pure logic — net8.0, no Windows/Win32 reference)
```

`WindowsSpaces.Core` must never reference Win32 or `WindowsSpaces.Platform`. All Win32
access happens behind `Core` interfaces (`IWindowManager`, `IMonitorManager`,
`IWindowEventSource`, `IHotkeyManager`, `IProcessManager`), implemented in
`WindowsSpaces.Platform/Win32/*` (`WindowApi`, `MonitorApi`, `WinEventHook`,
`HotkeyManager`, `DwmApi`) and `WindowsSpaces.Platform/ProcessManager.cs`. This split is
what makes `Core` unit-testable without a live Windows session — see
`src/WindowsSpaces.Tests/Core` (pure logic, fakes) vs
`src/WindowsSpaces.Tests/Integration` (real Win32 adapters, needs Windows).

Additional projects:
- `WindowsSpaces.Cli` — standalone CLI that talks to the running app over a named pipe
  (`WindowsSpaces_IPC_Pipe`); request/response contract is `IpcRequest`/`IpcResponse` in
  `WindowsSpaces.Core/IpcMessages.cs`, served by `WindowsSpaces.App/IpcServer.cs`.
- `WindowsSpaces.TestApp` — deterministic WinForms app that spawns controllable test
  windows (normal/maximized/minimized/always-on-top/etc.) for manual and integration
  testing of the window engine.

### Composition root

`WindowsSpaces.App/AppHost.cs` wires everything: constructs the Platform
implementations, loads `AppConfiguration` (persisted JSON, falling back to defaults per
monitor), builds one `WorkspaceManager` and one shared `WindowTracker`/`OperationGuard`,
registers hotkeys, starts the tray icon and IPC server, and watches the config file for
external edits. `AppHost.ApplyConfiguration` is the single path for applying a
Settings/Shortcuts/Rules/Profiles save at runtime — it must never throw (return
`(bool, error)` on failure), and it rolls back to the previous hotkey set if the new
bindings fail to register, so a failed save can't leave the app in a half-applied state.

### Core engine (`WindowsSpaces.Core`)

- **`WindowTracker`** — owns the live table of tracked windows (`WindowState`), fed by
  `IWindowEventSource` (WinEvent hooks) and periodic rescans. Applies `ApplicationRule`
  matching and restoration lookups when new windows appear.
- **`WorkspaceManager`** — owns per-monitor active-workspace state and the
  hide/show/move switching algorithm (`ApplyWorkspaceSwitch`). Key invariants:
  - **Latest-request-wins concurrency**: one transition worker per monitor
    (`MonitorTransitionState`); a switch requested while a transition is already running
    for that monitor overwrites the pending target rather than queuing, so a rapid burst
    (1→2→3→2) collapses to a single execution of the final target.
  - **Feedback-loop prevention**: every window operation the manager itself performs
    (`Hide`/`Show`/`Move`) is wrapped in `OperationGuard.Suppress(hwnd)` so the resulting
    WinEvent notifications aren't misread as independent user actions by the tracker.
  - **Workspace profiles** (`ApplyProfile`) can launch missing applications and restore
    them to their saved window/workspace/bounds once they appear — coordinated via
    `TryConsumeRestoration`/`_pendingRestorations`, matched on process path + window
    class + fuzzy title.
- Monitor identity, window identity, and reliability rules follow the spec in
  `windows_spaces_technical_documentation.md` §8–§11 — notably: never use monitor array
  index as stable identity, never identify a window solely by title, and prefer a window
  visible-in-the-wrong-workspace over a permanently lost window (`ShowAllWindows` is the
  emergency recovery path and must never alter workspace assignments, only visibility).

### Persistence

`WindowsSpaces.Persistence/JsonConfigurationStore.cs` implements `IConfigurationStore`.
`Load()` is fail-open by design (returns `null`/defaults rather than throwing on missing
or corrupt config, per ADR/engineering rule "prefer fail-open recovery" — see recent
history), while `Save()` can throw and callers (`AppHost.ApplyConfiguration`) must catch
it explicitly.

## Engineering rules (from the project spec, still binding)

- Keep `Core` independent of Win32; put platform access behind interfaces.
- Never manipulate windows directly from UI/ViewModel code — go through
  `WorkspaceManager`/`WindowTracker`.
- Never silently swallow Win32 errors.
- Never use a monitor's array index as its stable identity (docking/reconnect/reboot
  must not change identity).
- Never block the UI thread with window operations.
- Do not make undocumented Windows Virtual Desktop APIs (`IVirtualDesktopManager`, etc.)
  a core dependency — they may only be an optional integration.
- Run the test suite after any change to `Core` or `Platform` switching/tracking logic.
