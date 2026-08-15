# Windows Spaces

A native Windows 11 utility that gives **each physical monitor its own
independent set of virtual workspaces ("Spaces")**. Switching the workspace
on one monitor never affects any other monitor — unlike Windows' built-in
Virtual Desktops, which are shared across the whole desktop.

```
Monitor A                         Monitor B
┌─────────────────────┐           ┌─────────────────────┐
│ Space 1: Development│           │ Space 1: Chat       │
│ VS Code + Terminal  │           │ Discord + Slack     │
└─────────────────────┘           └─────────────────────┘

                Switch Monitor A → Space 2

┌─────────────────────┐           ┌─────────────────────┐
│ Space 2: Research   │           │ Space 1: Chat       │
│ Chrome + Docs       │           │ Discord + Slack     │  (unchanged)
└─────────────────────┘           └─────────────────────┘
```

The full product spec, requirements, and architecture rationale (including
ADRs) live in [`windows_spaces_technical_documentation.md`](windows_spaces_technical_documentation.md).
`CLAUDE.md` has the condensed engineering-agent-facing version of the same
information.

## Features

- Independent workspace switching per physical monitor
- Global keyboard shortcuts for switching/moving windows between workspaces
- Application rules — auto-assign windows to a monitor/workspace by process
  path, window class, or title
- Workspace profiles — launch a saved set of applications and restore their
  window positions/workspace assignments
- Tray icon with Settings, Shortcuts, Rules, Profiles, and Diagnostics
  windows, plus an emergency "Show All Windows" recovery action
- A standalone CLI (`ws.exe`) for scripting/automation against the running app
- Config persisted as JSON and hot-reloaded when edited externally
- Survives monitor disconnect/reconnect without losing workspace identity

## Requirements

- Windows 11
- [.NET SDK 10.0.400](https://dotnet.microsoft.com/) (pinned in `global.json`)
- The app is Windows-only (WinUI 3 / Win32 APIs throughout) — it will not
  build or run on macOS/Linux.

## Build & run

Build the whole solution:

```powershell
dotnet build WindowsSpaces.sln
```

`WindowsSpaces.App` is x64-only (`Platforms=x64`, `RuntimeIdentifier=win-x64`)
and uses the Windows App SDK in "self-contained" mode — if a build fails with
a platform-mapping error, check the `ProjectConfigurationPlatforms` section
of `WindowsSpaces.sln` rather than editing csproj platform properties
directly.

### Run the main app

```powershell
dotnet run --project src\WindowsSpaces.App
```

or run the built exe directly:

```powershell
dotnet build src\WindowsSpaces.App
.\src\WindowsSpaces.App\bin\x64\Debug\net8.0-windows10.0.19041.0\WindowsSpaces.App.exe
```

The app runs from the system tray — right-click the tray icon for Settings,
Shortcuts, Rules, Profiles, Diagnostics, and "Show All Windows". There's no
visible main window at launch; if you don't see a tray icon, check the
hidden-icons overflow area of the taskbar.

On first launch (no `%APPDATA%\WindowsSpaces\config.json` yet) the app
creates a default config with two workspaces per detected monitor and the
[default shortcuts](#default-keyboard-shortcuts) below.

### Run the CLI

The CLI talks to a *running* instance of the app over a named pipe, so start
the app first:

```powershell
dotnet run --project src\WindowsSpaces.Cli -- status
```

or, once built, invoke the exe directly (see [CLI](#cli-wsexe) below):

```powershell
.\src\WindowsSpaces.Cli\bin\Debug\net8.0\WindowsSpaces.Cli.exe status
```

### Run the test-window harness

`WindowsSpaces.TestApp` spawns controllable WinForms windows (normal,
maximized, minimized, always-on-top, etc.) — useful for exercising the
window engine manually without needing real applications open:

```powershell
dotnet run --project src\WindowsSpaces.TestApp
```

### Release builds

```powershell
dotnet build WindowsSpaces.sln -c Release
```

Release binaries land under each project's `bin\x64\Release\...` (App) or
`bin\Release\...` (Cli/TestApp/Core/Platform/Persistence) folder.

## Test

```powershell
dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj

# Run a subset:
dotnet test src/WindowsSpaces.Tests/WindowsSpaces.Tests.csproj --filter "FullyQualifiedName~WorkspaceManagerTests"
```

`src/WindowsSpaces.Tests/Core` contains pure-logic tests that run anywhere;
`src/WindowsSpaces.Tests/Integration` exercises the real Win32 adapters and
needs a live Windows session.

## Default keyboard shortcuts

Shortcuts are per-monitor-agnostic — they act on whichever monitor currently
has focus — and fully rebindable from the tray's **Shortcuts** window.

| Shortcut                   | Action                                   |
|-----------------------------|-------------------------------------------|
| `Ctrl+Alt+1` / `Ctrl+Alt+2`  | Switch the focused monitor to Space 1 / 2 |
| `Ctrl+Alt+Shift+1` / `+2`   | Move the focused window to Space 1 / 2    |
| `Ctrl+Alt+Shift+Esc`        | Emergency "Show All Windows" recovery     |
| `Ctrl+Alt+Up`               | Show workspace overview                   |

## Configuration

Config is stored as JSON at:

```
%APPDATA%\WindowsSpaces\config.json
```

It's loaded fail-open (a missing or corrupt file falls back to defaults
rather than crashing the app) and is watched for external edits, so you can
hand-edit it while the app is running. Use the tray UI (Settings / Shortcuts
/ Rules / Profiles) to change it safely instead where possible — those flows
validate the result and roll back if hotkey registration fails.

## CLI (`ws.exe`)

`WindowsSpaces.Cli` talks to the running app over a named pipe
(`WindowsSpaces_IPC_Pipe`) — the app must already be running.

```
ws.exe status                             Show active monitors, workspaces, and tracked windows
ws.exe switch <monitorId> <workspaceId>   Switch active workspace for a monitor
ws.exe profile <profileName>              Apply a workspace profile
ws.exe move-window <hwnd> <workspaceId>   Move a window to a different workspace
ws.exe rules                              List active rules
ws.exe sync                               Trigger configuration reload
ws.exe restore                            Emergency show-all-windows recovery

Options:
  --json        Output results in raw JSON format
  -h, --help    Show usage
```

## Architecture

Strict layering, enforced by project references:

```
WindowsSpaces.App (WinUI3 UI, tray, hotkeys, IPC server)
        |
WindowsSpaces.Persistence (JSON config store)
        |
WindowsSpaces.Platform (Win32/P-Invoke adapters)
        |
WindowsSpaces.Core (pure logic — no Windows/Win32 reference)
```

- **`WindowsSpaces.Core`** — pure logic: `WindowTracker` (live window table,
  rule matching), `WorkspaceManager` (per-monitor active-workspace state and
  the hide/show/move switching algorithm), configuration/rule/profile models.
  Never references Win32 — all platform access happens behind interfaces
  (`IWindowManager`, `IMonitorManager`, `IWindowEventSource`, `IHotkeyManager`,
  `IProcessManager`) so this layer is unit-testable without a live Windows
  session.
- **`WindowsSpaces.Platform`** — the Win32 implementations of those
  interfaces (`WindowApi`, `MonitorApi`, `WinEventHook`, `HotkeyManager`,
  `DwmApi`, `ProcessManager`).
- **`WindowsSpaces.Persistence`** — `JsonConfigurationStore`, the
  `IConfigurationStore` implementation.
- **`WindowsSpaces.App`** — the WinUI 3 shell: tray icon, hotkey
  registration, IPC server, and the Settings/Shortcuts/Rules/Profiles/
  Overview windows. `AppHost.cs` is the composition root — it wires up
  every layer, and `AppHost.ApplyConfiguration` is the single path for
  applying a runtime settings change (never throws; rolls back hotkeys on
  failure).
- **`WindowsSpaces.Cli`** — the standalone `ws.exe` client described above.
- **`WindowsSpaces.TestApp`** — a deterministic WinForms app that spawns
  controllable test windows (normal/maximized/minimized/always-on-top/etc.)
  for manual and integration testing of the window engine.

### Key invariants

- A monitor's array index is never used as its stable identity — monitors
  keep their identity across docking/reconnect/reboot.
- A window is never identified solely by its title.
- **Latest-request-wins concurrency**: a workspace switch requested while a
  transition is already running for that monitor overwrites the pending
  target rather than queuing, so a rapid burst of switches collapses to one
  execution of the final target.
- **Feedback-loop prevention**: every window operation the manager performs
  itself is wrapped in `OperationGuard.Suppress` so its own WinEvent
  notifications aren't misread as independent user actions.
- "Show All Windows" is the emergency recovery path — it only changes
  visibility, never workspace assignment, so a window visible in the wrong
  workspace is always preferred over a permanently lost one.

See `windows_spaces_technical_documentation.md` §8–§11 for the full
reliability rules these invariants come from.
