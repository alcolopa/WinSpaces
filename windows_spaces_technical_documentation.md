# Windows Spaces — Technical Requirements & Engineering Specification

**Version:** 1.0  
**Status:** Engineering starting point / source of truth  
**Target:** Windows 11  
**Stack:** C# + WinUI 3 + Windows App SDK + Win32/P/Invoke

## 1. Product Definition

A native Windows utility that gives **each physical monitor its own independent set of virtual workspaces ("Spaces")**.

### Core behavior

```text
Monitor A                         Monitor B
┌─────────────────────┐           ┌─────────────────────┐
│ Space 1: Development│           │ Space 1: Chat       │
│ VS Code + Terminal  │           │ Discord + Slack     │
└─────────────────────┘           └─────────────────────┘

Switch Monitor A → Space 2

┌─────────────────────┐           ┌─────────────────────┐
│ Space 2: Research   │           │ Space 1: Chat       │
│ Chrome + Docs       │           │ Discord + Slack     │
└─────────────────────┘           └─────────────────────┘
```

**Critical invariant:** switching the workspace on Monitor A must never switch Monitor B.

---

# 2. Problem & Opportunity

Windows' native Virtual Desktop abstraction does not directly provide the desired independent-per-monitor Space behavior.

The product should therefore not be positioned initially as a generic window manager. The focused proposition is:

> **Mac-style independent Spaces for Windows multi-monitor users.**

Existing products such as DisplayFusion, FancyWM and komorebi demonstrate that there is an established Windows power-user/window-management category. The opportunity is to focus tightly on the independent Spaces experience.

---

# 3. Scope

## MVP

- Independent workspaces per physical monitor
- Workspace switching
- Window assignment
- Window movement between workspaces
- Keyboard shortcuts
- Workspace persistence
- Monitor detection
- Monitor disconnect/reconnect handling
- Crash recovery
- Emergency "Show All Windows"
- Diagnostics
- Automated tests

## Phase 2

- Mission Control-style workspace overview
- Workspace thumbnails
- Drag/drop between workspaces
- Per-application rules
- Workspace profiles
- Better visual transitions

## Phase 3

- Application launching
- Complete environment restoration
- Automation
- CLI
- Local IPC
- Optional configuration synchronization

## Explicitly excluded from MVP

- Custom Windows shell
- Custom taskbar
- AI
- Cloud dependency
- Launcher
- Full tiling window manager

---

# 4. Technical Feasibility

| Area | Assessment | Notes |
|---|---|---|
| Monitor detection | High | Stable Win32 display APIs |
| Window enumeration | High | `EnumWindows` and User32 |
| Window placement | High | `SetWindowPos`, placement APIs |
| Window lifecycle tracking | High | WinEvent hooks |
| Independent Spaces | High | Application-level workspace layer |
| Crash recovery | Medium/High | Requires careful fail-open design |
| Fullscreen/game compatibility | Medium | Special handling required |
| Undocumented Virtual Desktop APIs | Risky | Do not make them core |

## Key architectural conclusion

Do **not** build the MVP around undocumented Windows Virtual Desktop COM internals.

Instead, implement logical workspaces at the application level by:

1. Tracking windows.
2. Assigning them to logical monitor/workspace pairs.
3. Hiding inactive workspace windows.
4. Showing active workspace windows.
5. Moving windows between monitors when necessary.
6. Restoring their geometry/state.

The fundamental technical spike must prove this works reliably before building the product UI.

---

# 5. Recommended Technology Stack

- **Language:** C#
- **Runtime:** Current supported .NET version at implementation time
- **UI:** WinUI 3
- **Desktop framework:** Windows App SDK
- **Native integration:** Win32 / P/Invoke
- **Initial target:** Windows 11 24H2+
- **Storage:** Local JSON/SQLite depending on state complexity
- **Testing:** xUnit/NUnit + Windows integration tests
- **Packaging:** MSIX initially; evaluate installer options later

---

# 6. Architecture

```text
┌──────────────────────────────────────────────┐
│                 WinUI 3 UI                   │
│ Settings · Overview · Tray · Shortcuts       │
├──────────────────────────────────────────────┤
│              Application Services            │
├──────────────────────────────────────────────┤
│                Workspace Core                │
│ WorkspaceManager · WindowTracker             │
│ MonitorManager · RuleEngine · Persistence    │
├──────────────────────────────────────────────┤
│             Platform Abstractions             │
│ IWindowManager · IMonitorManager              │
│ IWindowEventSource · IHotkeyManager           │
├──────────────────────────────────────────────┤
│              Windows Adapter                 │
│ User32 · WinEvent · DWM · Display APIs       │
└──────────────────────────────────────────────┘
```

## Repository structure

```text
WindowsSpaces.sln

src/
  WindowsSpaces.App/
    Views/
    ViewModels/
    Assets/

  WindowsSpaces.Core/
    WorkspaceManager.cs
    Workspace.cs
    WorkspaceState.cs
    WindowState.cs
    Monitor.cs
    Rule.cs

  WindowsSpaces.Platform/
    Win32/
      WindowApi.cs
      MonitorApi.cs
      WinEventHook.cs
      HotkeyManager.cs
      DwmApi.cs

  WindowsSpaces.Persistence/
    ConfigurationStore.cs
    StateStore.cs
    Migrations/

  WindowsSpaces.Tests/
    Core/
    Integration/

  WindowsSpaces.TestApp/
```

---

# 7. Functional Requirements

## FR-001 — Independent workspaces

- Each physical monitor MUST have its own workspace collection.
- Each monitor MUST have an independently active workspace.
- Changing Monitor A's workspace MUST NOT change Monitor B.
- Workspace count MUST be configurable.
- Workspace names MUST be configurable.

## FR-002 — Window assignment

- Every managed top-level application window MUST have a logical monitor/workspace assignment.
- Assignment MUST survive workspace switching.
- Window position MUST be restorable.
- Maximized/minimized state MUST be restorable.
- User-driven movement between monitors MUST be respected.

## FR-003 — Switching

- Switching MUST be keyboard accessible.
- Switching MUST operate independently per monitor.
- Only one transition may execute per monitor at a time.
- Rapid switching SHOULD use a latest-request-wins strategy.

## FR-004 — Persistence

- Configuration MUST survive application restarts.
- Monitor identity MUST NOT rely solely on display index.
- State files MUST be versioned.
- Configuration migrations MUST be supported.

## FR-005 — Recovery

- An emergency "Show All Windows" operation MUST exist.
- A crash MUST NOT permanently leave windows hidden.
- Interrupted transitions MUST recover to a safe visible state.

---

# 8. Window Engine

The Window Engine handles discovery, classification, tracking and manipulation of top-level windows.

## Relevant Win32 APIs

```text
EnumWindows
GetWindowLongPtr
IsWindow
IsWindowVisible
GetWindowThreadProcessId
GetWindowPlacement
GetWindowRect
MonitorFromWindow
GetWindowText
GetClassName
OpenProcess
QueryFullProcessImageName
ShowWindow
SetWindowPos
SetWindowPlacement
RegisterHotKey
```

## Window identity

Runtime identity should use HWND plus lifecycle tracking.

Do not identify windows solely by:

- title
- executable name
- window class

Application rules can use:

- executable/process path
- AppUserModelID
- window class
- optional title matching

## Window state

```text
WindowState
├── Hwnd
├── ProcessId
├── ProcessPath
├── AppIdentity
├── MonitorId
├── WorkspaceId
├── IsVisible
├── IsMinimized
├── IsMaximized
├── IsFullscreen
├── IsAlwaysOnTop
├── NormalBounds
├── LastObservedBounds
└── LastUpdated
```

## WinEvent tracking

Investigate/use relevant WinEvent events such as:

- EVENT_OBJECT_CREATE
- EVENT_OBJECT_DESTROY
- EVENT_OBJECT_SHOW
- EVENT_OBJECT_HIDE
- EVENT_OBJECT_LOCATIONCHANGE
- EVENT_SYSTEM_FOREGROUND

Callbacks should be lightweight and enqueue events for serialized processing rather than performing complex workspace operations directly.

---

# 9. Workspace Engine

## Workspace model

```text
Workspace
├── Id
├── MonitorId
├── Name
├── Index
├── Icon
├── Metadata
└── Active
```

## Switching algorithm

```text
Current Workspace
      ↓
Capture current window state
      ↓
Persist critical transition state
      ↓
Hide/park current workspace windows
      ↓
Resolve target workspace windows
      ↓
Move target windows to target monitor
      ↓
Restore geometry and state
      ↓
Show target windows
      ↓
Restore appropriate focus
      ↓
Complete
```

## Concurrency

Only one workspace transition may execute per monitor.

Recommended behavior:

**Latest request wins.**

Example:

```text
User presses 1 → 2 → 3 → 2 rapidly

Instead of executing every transition:
1 → 2 → 3 → 2

Collapse toward:
current → 2
```

## Feedback-loop prevention

The product's own `SetWindowPos`/move operations will generate Windows events.

The engine therefore needs an internal operation guard/suppression scope so these events are not interpreted as independent user actions.

---

# 10. Monitor Engine

Monitor identity must survive:

- display ordering changes
- docking
- undocking
- reconnection
- reboot

Prefer:

- EDID information
- display device path
- manufacturer
- model
- serial

Use topology-aware fallback where necessary.

## Requirements

- Detect monitor additions/removals.
- Detect docking/undocking.
- Preserve disconnected monitor workspace configuration.
- Reconcile affected windows.
- Restore logical workspace state when a display returns.
- Support mixed DPI.
- Support portrait displays.
- Support mixed orientations.

## DPI

The application must be per-monitor DPI aware.

Test at:

- 100%
- 125%
- 150%
- 200%
- mixed DPI across monitors

Never assume logical pixels and physical pixels are equivalent.

---

# 11. Reliability & Recovery

## Primary reliability rule

> A window being visible in the wrong workspace is preferable to a window becoming permanently inaccessible.

## Emergency recovery

```text
Show All Windows
    ↓
Enumerate top-level windows
    ↓
Show managed windows
    ↓
Stop workspace hiding
    ↓
Preserve configuration
    ↓
Return to safe state
```

## Crash recovery

The product should:

1. Persist critical state before transitions.
2. Record an interrupted-transition/recovery marker.
3. Detect incomplete transitions at startup.
4. Make managed windows visible before rebuilding workspace state.
5. Reconstruct logical assignments afterward.
6. Never require perfect previous state to start.

## Monitor disconnect

When a monitor disappears:

1. Detect removal.
2. Preserve its logical workspace configuration.
3. Reconcile its windows onto available monitors.
4. Preserve workspace assignments.
5. Restore them when the display returns where possible.

---

# 12. Compatibility

| Category | Initial policy |
|---|---|
| Normal Win32 applications | Fully supported target |
| Electron/Chromium | Supported with testing |
| WinUI/UWP | Supported with testing |
| Maximized windows | Preserve state |
| Minimized windows | Preserve state |
| Always-on-top | Respect by default |
| Elevated applications | Graceful failure if restricted |
| Games/exclusive fullscreen | Best effort |
| Remote Desktop/security surfaces | Compatibility testing required |

## Native Windows Virtual Desktops

Windows exposes `IVirtualDesktopManager` for native virtual desktop operations.

It may be investigated as an optional integration.

It must **not** become the core dependency.

---

# 13. UI/UX

The application should be quiet, native and keyboard-first.

## MVP UI

- System tray
- Settings
- Shortcut editor
- Workspace list
- Diagnostics

## Future Mission Control-style overview

```text
Monitor 1

┌────────────┐ ┌────────────┐ ┌────────────┐
│ Development│ │ Research   │ │ Meetings   │
│ VS Code    │ │ Chrome     │ │ Teams      │
└────────────┘ └────────────┘ └────────────┘

Monitor 2

┌────────────┐ ┌────────────┐
│ Chat       │ │ Media      │
│ Discord    │ │ Spotify    │
└────────────┘ └────────────┘
```

## Suggested shortcuts

| Shortcut | Action |
|---|---|
| Ctrl + Alt + 1/2/3 | Switch current monitor workspace |
| Ctrl + Alt + Left/Right | Previous/next workspace |
| Ctrl + Alt + Shift + 1/2/3 | Move active window to workspace |

"Current monitor" should default to the monitor containing the foreground window.

An optional cursor-based mode can be added later.

---

# 14. Testing Strategy

## Unit tests

Test:

- workspace state machine
- monitor/workspace mapping
- window assignment
- rule matching
- serialization
- migrations
- recovery
- transition queuing

## Integration tests

Test:

- monitor enumeration
- window enumeration
- hide/show
- move/resize
- WinEvent processing
- global hotkeys

## Dedicated test application

Create a deterministic `WindowsSpaces.TestApp` that creates controllable windows:

- normal
- maximized
- minimized
- always-on-top
- rapidly created
- rapidly destroyed
- fullscreen simulation
- multiple processes/windows

## Test matrix

| Dimension | Cases |
|---|---|
| Monitors | 1, 2, 3, 4 |
| Resolution | 1080p, 1440p, 4K, mixed |
| DPI | 100%, 125%, 150%, 200%, mixed |
| Refresh | 60, 120, 144, 240 Hz |
| Orientation | landscape, portrait, mixed |
| Power | sleep, wake, hibernate, restart |
| Topology | dock, undock, disconnect, reconnect |
| Failure | crash during transition, Explorer restart, rapid switching |

---

# 15. Performance Requirements

| Metric | Target |
|---|---|
| Normal workspace switch | <100 ms perceived; <250 ms acceptable |
| Idle CPU | <1% average target |
| Active CPU | <5% typical target |
| Memory | <150 MB target |
| UI thread | Never block on window operations |

Use event-driven tracking instead of continuous polling where possible.

Avoid continuous high-resolution screenshot capture.

---

# 16. Security & Privacy

- Core functionality must work offline.
- No server dependency.
- No DLL injection as a default architecture.
- Do not require administrator privileges for normal operation.
- Minimize process privileges.
- Code-sign release builds.
- Telemetry OFF by default.
- If telemetry is later added, make it opt-in.
- Never collect window titles, URLs, document names or command lines by default.

---

# 17. Development Roadmap

## Phase 0 — Technical spike

Prove:

- monitor enumeration
- window enumeration
- window → monitor identification
- hide/show
- move/resize
- hotkeys
- WinEvent hook
- independent two-monitor switching

**Do not build the full UI before this works.**

## Phase 1 — Core engine

Build:

- MonitorManager
- WindowManager
- WindowTracker
- WorkspaceManager
- EventProcessor
- Persistence
- Recovery

## Phase 2 — Product UI

Build:

- tray
- settings
- shortcut configuration
- workspace UI
- diagnostics

## Phase 3 — Advanced UX

Build:

- Mission Control
- thumbnails
- drag/drop
- app rules
- profiles
- transitions

## Phase 4 — Power features

Build:

- application launching
- complete environment restoration
- automation
- CLI
- local IPC
- optional configuration sync

---

# 18. Product Validation

Do not build a large commercial product before proving the core behavior has demand.

## Validation process

1. Build the two-monitor MVP.
2. Record a short demonstration.
3. Give it to developers and multi-monitor power users.
4. Measure daily usage.
5. Measure switching frequency.
6. Track crashes and recovery failures.
7. Ask users which advanced features they would pay for.
8. Test pricing.

## Potential pricing model

A one-time license is worth testing because this is primarily a local desktop utility.

Possible structure:

### Free

- Core independent Spaces
- Basic shortcuts
- Limited workspaces

### Pro

- Unlimited workspaces
- App rules
- Workspace profiles
- Environment restoration
- Advanced Mission Control
- Automation

Do not commit to pricing until validated.

---

# 19. Killer Future Feature — Workspace Profiles

Example:

```text
Profile: Development

Monitor 1
  Coding
    VS Code
    Terminal
    Browser

Monitor 2
  Services
    Docker
    Database
    Logs
```

Activating the profile:

```text
Activate Development
      ↓
Launch missing applications
      ↓
Detect windows
      ↓
Assign windows
      ↓
Move windows
      ↓
Resize windows
      ↓
Restore environment
```

This can become a major differentiator beyond simply switching Spaces.

---

# 20. Claude Implementation Brief

Give Claude the following as the first implementation instruction:

```text
You are the lead Windows systems engineer for this project.

Read the entire technical specification before modifying the repository.

Do NOT immediately build the complete application.

First perform a technical feasibility spike for the independent-per-monitor workspace engine.

CORE REQUIREMENT:

Each physical monitor must have independent virtual workspaces.

Switching the workspace on Monitor A must NOT change the workspace on Monitor B.

Use:

- C#
- WinUI 3
- Windows App SDK
- stable Win32 APIs
- P/Invoke where required

Do NOT make undocumented Windows Virtual Desktop APIs a core dependency.

ARCHITECTURE:

Core
→ Platform Abstractions
→ Win32 Implementation
→ UI

The Core layer must not reference Win32.

Before building the UI, prove on Windows 11 that:

1. Physical monitors can be enumerated.
2. Top-level application windows can be enumerated.
3. Windows can be assigned to logical monitor/workspace pairs.
4. Inactive workspace windows can be hidden.
5. Active workspace windows can be shown.
6. One monitor can switch independently.
7. Another monitor remains unchanged.
8. A window can be moved between workspaces.
9. Window geometry is preserved.
10. Maximized/minimized state is preserved.
11. An emergency Show All Windows operation can recover the environment.

Build a deterministic test application containing multiple test windows.

Write:

- unit tests for the workspace state machine
- integration tests for the Win32 adapter
- tests for monitor changes
- tests for rapid switching
- tests for crash/recovery behavior

Do not add unrelated features.

At the end of the technical spike produce:

- architecture summary
- files created
- Win32 APIs used
- test results
- known Windows limitations
- compatibility issues
- crash/recovery analysis
- performance observations
- recommendation on whether to proceed

If the approach is technically unsound, STOP and document the blocker.

Do not compensate for fundamental problems with random dependencies or undocumented APIs.

Reliability is more important than visual polish.
```

---

# 21. Claude Engineering Rules

Claude should follow these rules throughout development:

- Read this specification before coding.
- Do not make large changes without tests.
- Keep Core independent from Win32.
- Use interfaces around platform APIs.
- Never manipulate windows directly from UI code.
- Document risky architecture decisions with ADRs.
- Never silently swallow Win32 errors.
- Prefer fail-open recovery.
- Never use monitor array index as stable identity.
- Never block the UI thread.
- Run tests after meaningful changes.
- Do not add features outside the current phase.
- Do not replace a difficult technical problem with an undocumented Windows API without documenting the trade-off.

---

# 22. Acceptance Criteria

## AC-001 — Independent switching

```text
Initial:
Monitor A = Space 1
Monitor B = Space 1

Action:
Switch Monitor A → Space 2

Expected:
Monitor A = Space 2
Monitor B = Space 1
```

## AC-002 — Independent second switch

```text
Action:
Switch Monitor B → Space 2

Expected:
Monitor A = Space 2
Monitor B = Space 2
```

## AC-003 — Window movement

A window moved from A/Space 1 to B/Space 2 must appear when B/Space 2 is active and must not unexpectedly appear in unrelated workspaces.

## AC-004 — Monitor removal

Disconnecting a monitor must not permanently lose windows or delete its logical workspace configuration.

## AC-005 — Restart

Restarting Windows should preserve workspace configuration and restore managed windows where possible.

## AC-006 — Crash during transition

Terminating the product during a transition must leave a recovery path that makes windows visible again.

## AC-007 — Emergency recovery

"Show All Windows" must recover managed windows without deleting workspace configuration.

### Definition of technical success

> Two monitors, two spaces each, independent switching, stable persistence and zero lost windows.

Everything else is secondary until this works reliably.

---

# 23. Architecture Decision Records

## ADR-001 — Workspace mechanism

**Decision:** Application-level window visibility and placement management.

**Rejected:** Native Windows Virtual Desktops as the primary mechanism.

**Reason:** Native desktops do not directly provide the desired independent-per-monitor semantics.

## ADR-002 — UI

**Decision:** WinUI 3 + Windows App SDK.

**Reason:** Native Windows desktop UI and current Microsoft tooling.

## ADR-003 — Language

**Decision:** C#.

**Reason:** Fast development and strong Win32 interoperability without prematurely adding C++ complexity.

## ADR-004 — Target

**Decision:** Windows 11 24H2+ initially.

**Reason:** Reduce the compatibility matrix and focus on current Windows power users.

## ADR-005 — Undocumented APIs

**Decision:** Do not make undocumented Virtual Desktop APIs a core dependency.

**Reason:** Windows-update compatibility risk and unnecessary dependence for the fundamental logical-workspace approach.

---

# 24. Recommended First Milestone

Do not start with:

- settings
- animations
- marketing page
- Mission Control
- workspace thumbnails
- profiles
- cloud sync

Start with this:

```text
Two monitors
+
Two workspaces per monitor
+
Four or more test windows
+
Keyboard switching
+
Window tracking
+
Hide/show
+
Move/resize
+
Emergency recovery
+
Automated tests
```

If this works reliably, the rest of the product becomes an incremental engineering problem.

If this cannot be made reliable, the project should be reconsidered before significant UI/product development.

---

# 25. Reference Material

- Microsoft Windows desktop development:
  https://learn.microsoft.com/en-us/windows/apps/desktop/

- WinUI 3:
  https://learn.microsoft.com/en-us/windows/apps/winui/winui3/

- Windows App SDK:
  https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/

- IVirtualDesktopManager:
  https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ivirtualdesktopmanager

- PowerToys discussion around independent per-monitor virtual desktops:
  https://github.com/microsoft/PowerToys/issues/39839

- DisplayFusion:
  https://www.displayfusion.com/

- komorebi:
  https://github.com/LGUG2Z/komorebi

- FancyWM:
  https://github.com/FancyWM/fancywm

---

## Final engineering principle

**Prove the independent-per-monitor workspace engine first.**

The product's real technical risk is not the WinUI interface. It is reliably managing arbitrary Windows applications across multiple monitors without losing windows, breaking geometry, creating event loops, or failing during monitor changes and application crashes.

Everything else should be built around that proven core.
