# Phase 2 — Product UI: Design Spec

**Status:** Approved for implementation
**Scope:** Per `windows_spaces_technical_documentation.md` §17 Phase 2 — tray, settings, shortcut configuration, workspace UI, diagnostics. Also closes the FR-004 persistence gap left open from Phase 1 (workspace count/names were hardcoded, nothing survived restart).

## Environment note

The dev machine had only the .NET runtime, no SDK. Installed .NET SDK 10.0.400 (current LTS) via winget and repointed `global.json` from `8.0.424` to `10.0.400`. Existing projects keep targeting `net8.0`/`net8.0-windows` (multi-targeting via the SDK 10 toolchain) — no reason to bump target frameworks as part of this work. Build and full test suite verified green (21/22; the 1 failure is the pre-existing hardware/manual acceptance test that requires `WindowsSpaces.TestApp` running, same as before the SDK swap).

The app itself will not be run/launched during this work (per instruction). Verification is via `dotnet build` + `dotnet test` only. UI code (XAML/code-behind) is kept minimal and pushed behind plain C# view-models so the real logic is unit-testable without running the app; XAML itself is not visually verified.

## Projects touched, in build order

1. **WindowsSpaces.Core** — add persistence-related domain types and interface (no Win32/WinUI dependency).
2. **WindowsSpaces.Persistence** (new project) — JSON-backed implementation of the Core interface.
3. **WindowsSpaces.App** — convert to WinUI3, add Settings/Shortcuts/Diagnostics windows, wire persistence into `AppHost`, add tray context menu.
4. **WindowsSpaces.Tests** — new tests alongside each of the above, in the same order.

Each project must build and its tests must pass before moving to the next.

## 1. Core additions

- `AppConfiguration` record: `SchemaVersion` (int), `Monitors: IReadOnlyList<MonitorWorkspaceConfig>`, `Hotkeys: IReadOnlyList<HotkeyBinding>`.
- `MonitorWorkspaceConfig` record: `MonitorId`, `Workspaces: IReadOnlyList<WorkspaceDefinition>`.
- `WorkspaceDefinition` record: `Id`, `Name`, `Index`.
- `HotkeyBinding` record: `Action` (enum `HotkeyAction { SwitchWorkspace1, SwitchWorkspace2, ..., MoveToWorkspace1, MoveToWorkspace2, ..., ShowAllWindows }`), `Modifiers` (existing `ModifierKeys`), `VirtualKey` (int). Workspace-index actions are generated dynamically up to the configured max workspace count (cap at 9, matching single-digit hotkeys) rather than being a fixed 1/2.
- `IConfigurationStore` interface: `AppConfiguration Load()`, `void Save(AppConfiguration config)`. Lives in Core so App can depend on the interface without depending on Persistence's file-format details; `AppHost` (composition root) is what wires the concrete `JsonConfigurationStore` in.
- `AppConfiguration.CreateDefault(IEnumerable<Monitor> monitors)` static helper producing today's implicit behavior: 2 workspaces per monitor named "Space 1"/"Space 2", and the 5 existing hotkey bindings — this is the fallback whenever no saved config exists or a newly-connected monitor has no entry, and it's what keeps the migration from Phase 0/1 non-breaking.

Validation rules (enforced in a `AppConfiguration.Validate()` used by both the store's load path and the settings view-model before save):
- Each monitor has at least 1 and at most 9 workspaces.
- Workspace names non-empty, unique within a monitor.
- No two `HotkeyBinding`s share the same (Modifiers, VirtualKey) pair.

## 2. Persistence project

- New `WindowsSpaces.Persistence` project (net8.0, no Windows-only dependency — pure JSON I/O), referencing only `WindowsSpaces.Core`.
- `JsonConfigurationStore : IConfigurationStore`, constructed with a file path (default `%AppData%\WindowsSpaces\config.json`, but path is a constructor parameter so tests use a temp file).
- `Load()`: if the file is missing, unreadable, or fails JSON deserialization, or fails `Validate()`, or its `SchemaVersion` is newer than the app understands — **fail open**: return `null` to signal "no usable saved config," never throw. Callers (AppHost) combine this with `CreateDefault` per monitor. This matches the spec's "never require perfect previous state" rule.
- `Save()`: serializes to the target path, writing to a temp file then replacing atomically (`File.Replace`/move) so a crash mid-write can't corrupt the existing config. Creates the directory if missing. Current schema version constant = 1.
- No migration logic needed yet (only one schema version exists); the version field and the "unknown version → fail open" path is what future migrations hook into.

## 3. App project (WinUI3 conversion)

- Add Windows App SDK / WinUI3 references (`Microsoft.WindowsAppSDK` NuGet package, unpackaged deployment — no MSIX, keeping the existing plain-exe deployment model). `Program.Main` becomes a WinUI3 `Application`-derived entry point; the existing message-only-window/hotkey pump stays (WinUI3's dispatcher runs on the same thread, hotkey WM_HOTKEY handling is unaffected since it doesn't go through XAML).
- `AppHost` changes: on `Start()`, load config via `IConfigurationStore`, fall back to `CreateDefault` per monitor for any monitor missing from the saved config, apply workspace definitions into `WorkspaceManager`/`WindowTracker`'s initial state, and register hotkeys from `AppConfiguration.Hotkeys` instead of the hardcoded set. Expose `ApplyConfiguration(AppConfiguration)` for the Settings/Shortcuts windows to call after Save — this unregisters/re-registers hotkeys and renames in-memory `Workspace` objects live. **Changing workspace count for a monitor requires an app restart** (documented in the Settings UI with a notice) — live add/remove of workspaces while windows are assigned to them is out of scope for Phase 2; renaming and hotkey changes apply immediately.
- `TrayIcon`: currently only sets a tooltip and never handles its own callback message. Add: right-click/left-click opens a context menu (Settings, Shortcuts, Diagnostics, Show All Windows, Exit) via `TrackPopupMenu`. This requires wiring the `WM_APP` callback (`uCallbackMessage`) through `AppHost.HandleMessage`, which today only forwards `WM_HOTKEY`.
- `SettingsWindow` (XAML) + `SettingsViewModel` (plain C#, testable): loads current `AppConfiguration`, presents each monitor with an editable, reorderable list of workspace names, Add/Remove/Rename, Save/Cancel. Save calls `Validate()`; on failure shows inline error and does not close.
- `ShortcutSettingsWindow` + `ShortcutSettingsViewModel`: lists current `HotkeyBinding`s with a "press new combination" capture; validates no duplicates via the same `Validate()` path; Save persists and calls `ApplyConfiguration`.
- `DiagnosticsWindow` + `DiagnosticsViewModel`: read-only. `AppHost.GetDiagnosticsSnapshot()` returns tracked windows (hwnd, process, monitor, workspace, visibility/min/max state) and monitors with their active workspace. `DiagnosticsViewModel` polls this via a `DispatcherTimer` at 1s while the window is open (diagnostics-only cost, doesn't affect the idle-CPU target since it's inactive otherwise).

## 4. Testing

- **Core**: `AppConfigurationTests` (validation rules, `CreateDefault`), `HotkeyBindingTests` (duplicate detection).
- **Persistence**: `JsonConfigurationStoreTests` — round-trip save/load; missing file → `Load()` returns null; corrupt JSON → null; invalid-per-`Validate()` content → null; concurrent-safe write via temp-file swap (verify no partial file left after a simulated failure, if feasible to simulate).
- **App view-models**: `SettingsViewModelTests`, `ShortcutSettingsViewModelTests`, `DiagnosticsViewModelTests` — pure logic against a fake `IConfigurationStore`/fake `AppHost` snapshot, no WinUI types involved, placed in `WindowsSpaces.Tests` alongside existing Core tests.
- **App integration**: extend the existing `WindowsSpaces.Tests/Integration` pattern with a `TrayIconMenuTests` if the context-menu wiring can be tested without a live tray (may end up manual/hardware-only like the existing acceptance test — acceptable, follow the existing precedent of documenting it as a manual test).
- XAML/code-behind and the `Application`/window-lifecycle glue are not unit tested — consistent with how `Program.cs`'s Win32 message loop is untested today.

## Out of scope for Phase 2 (explicitly, per the roadmap)

Mission Control overview, thumbnails, drag/drop, per-app rules, profiles, visual transitions — all Phase 3. Live workspace-count changes without restart are also deferred.
