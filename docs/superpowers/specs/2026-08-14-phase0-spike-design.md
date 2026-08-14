# Windows Spaces — Phase 0 Technical Spike Design

**Date:** 2026-08-14
**Status:** Approved for implementation
**Parent spec:** `windows_spaces_technical_documentation.md`

## Purpose

Prove the independent-per-monitor workspace mechanism works reliably on Windows 11 before any product UI, persistence, or advanced features are built. This follows the parent spec's explicit instruction (§20, §24): do not build the full application first.

**Definition of success (from parent spec §22):** two monitors, two spaces each, independent switching, zero lost windows.

## Scope

In scope:

- 2 monitors × 2 workspaces each, 4+ deterministic test windows
- Monitor enumeration and identification
- Top-level window enumeration, classification, and lifecycle tracking via WinEvent hooks
- Hide/show of windows by workspace, with geometry, maximized/minimized state preserved
- Move/resize when a window changes monitor/workspace assignment
- Independent per-monitor switching — switching Monitor A never affects Monitor B
- One transition in flight per monitor at a time, latest-request-wins on rapid repeated input
- Global hotkeys: `Ctrl+Alt+1`/`Ctrl+Alt+2` switch the current monitor's workspace (current monitor = monitor of the foreground window); `Ctrl+Alt+Shift+1`/`Ctrl+Alt+Shift+2` move the active window to a workspace
- Emergency "Show All Windows" action — unconditionally reveals all managed windows and halts hiding
- Operation-guard to suppress self-generated `SetWindowPos`/move events from being misread as independent user actions

Out of scope (deferred to real Phase 1+):

- Persistence / state files / configuration migrations
- Monitor-identity-survives-reboot logic (EDID/device-path based identity)
- Crash-recovery markers and startup recovery detection
- DPI awareness testing, portrait/mixed orientation testing
- Rule engine, application matching by executable/AppUserModelID
- Settings UI, workspace list UI, diagnostics UI
- MSIX packaging

The spike only needs to prove the mechanism works while the app is running in the current session; state does not need to survive a restart.

## Architecture

Layering follows the parent spec's Core → Platform Abstractions → Windows Adapter → UI structure. **Core must not reference Win32.**

```
WindowsSpaces.sln

src/
  WindowsSpaces.Core/              (no Win32 references)
    Monitor.cs
    Workspace.cs
    WindowState.cs
    IMonitorManager.cs
    IWindowManager.cs
    IWindowEventSource.cs
    IHotkeyManager.cs
    WorkspaceManager.cs           — switching algorithm, per-monitor transition queue (latest-wins), operation guard
    WindowTracker.cs              — maintains WindowState collection from the event stream

  WindowsSpaces.Platform/          (Win32 P/Invoke adapter, implements Core interfaces)
    Win32/
      MonitorApi.cs
      WindowApi.cs
      WinEventHook.cs
      HotkeyManager.cs

  WindowsSpaces.App/               (minimal WinUI 3 shell)
    — hosts the message loop and a tray icon showing the active workspace per monitor
    — wires global hotkeys to WorkspaceManager
    — no settings/workspace UI in this phase

  WindowsSpaces.TestApp/
    — spawns deterministic test windows: normal, maximized, minimized, always-on-top
    — known titles/classes for reliable identification in tests

  WindowsSpaces.Tests/
    Core/                          — unit tests against fake IMonitorManager/IWindowManager, no real windows needed, runs in CI
    Integration/                   — real Win32 adapter tests, run manually on a dev machine with real monitors/windows (not CI-friendly)
```

## Components

**Monitor / Workspace / WindowState** — data models per parent spec §9 and §8's `WindowState` shape, trimmed to fields the spike actually uses (drop `AppIdentity`-based rule fields not needed yet, keep `Hwnd`, `ProcessId`, `MonitorId`, `WorkspaceId`, `IsVisible`, `IsMinimized`, `IsMaximized`, `NormalBounds`, `LastUpdated`).

**WindowTracker** — consumes queued WinEvent notifications (`EVENT_OBJECT_CREATE/DESTROY/SHOW/HIDE/LOCATIONCHANGE`, `EVENT_SYSTEM_FOREGROUND`) and updates the `WindowState` collection. WinEvent callbacks stay lightweight; they enqueue, a separate processing loop drains the queue.

**WorkspaceManager** — implements the switching algorithm from parent spec §9: capture state → hide current workspace windows → resolve target windows → move to target monitor if needed → restore geometry/state → show → restore focus. Enforces one transition per monitor via a queue that collapses rapid requests to the latest target ("latest request wins", per §9 example).

**Operation guard** — a suppression scope the WorkspaceManager holds during its own `SetWindowPos`/show/hide calls, so the resulting WinEvent notifications are not re-interpreted as user-driven moves.

**HotkeyManager** — registers global hotkeys via `RegisterHotKey`, forwards to WorkspaceManager. "Current monitor" resolves via the monitor of the foreground window.

**Emergency recovery** — a single operation, exposed via hotkey and tray menu, that enumerates all managed windows and shows them, and disables the WorkspaceManager's hiding behavior until re-armed. No persisted state is required to run this in the spike (no persistence in scope), so recovery is simply "stop hiding, show everything."

## Data Flow

Switching algorithm — unchanged from parent spec §9:

```
Current Workspace
  → Capture current window state
  → Hide/park current workspace windows
  → Resolve target workspace windows
  → Move target windows to target monitor (if needed)
  → Restore geometry and state
  → Show target windows
  → Restore appropriate focus
  → Complete
```

## Testing

- **Unit tests (Core):** workspace state machine, transition queuing/latest-wins collapsing, window assignment logic — all against fakes, run in CI, no real windows or monitors required.
- **Integration tests (Platform):** real monitor enumeration, real window enumeration, hide/show, move/resize, WinEvent processing, global hotkey registration. Flagged for manual/local execution against the `WindowsSpaces.TestApp` windows on real hardware (2+ monitors), not run in CI.
- **Manual acceptance pass:** run through parent spec AC-001, AC-002, AC-003, AC-007 (independent switching, independent second switch, window movement, emergency recovery) using `WindowsSpaces.TestApp` windows across 2 real monitors.

## Deliverable

At the end of the spike, produce a report (per parent spec §20):

- Architecture summary
- Files created
- Win32 APIs used
- Test results (unit + manual integration pass)
- Known Windows limitations encountered
- Compatibility issues encountered
- Crash/recovery analysis (bounded to what's in scope: emergency show-all only)
- Performance observations
- Recommendation on whether to proceed to full Phase 1

If the approach proves unsound, stop and document the blocker rather than compensating with undocumented APIs or workarounds.
