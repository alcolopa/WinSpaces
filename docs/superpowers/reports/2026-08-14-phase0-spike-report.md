# Windows Spaces — Phase 0 Spike Report

**Date:** 2026-08-14

## Architecture summary

The layering held as specified: `WindowsSpaces.Core` (domain models, interfaces, `WindowTracker`, `WorkspaceManager`) has zero Win32 or Windows-specific references — confirmed by its `net8.0` (non-`-windows`) target framework, which would fail to build if any Win32 type leaked in. `WindowsSpaces.Platform` implements the Core interfaces via `user32.dll`/`kernel32.dll` P/Invoke. `WindowsSpaces.App` is the composition root: it never calls window-management Win32 functions directly, only `WorkspaceManager`/`HotkeyManager`; the one exception is `TrayIcon.cs`, which uses `Shell_NotifyIcon` directly since a tray icon is UI chrome, not window management, per the plan's own carve-out.

One deviation from the original design: `WindowsSpaces.App` does **not** use WinUI 3 / Windows App SDK. Building against those packages requires the Windows 10/11 SDK's Appx packaging MSBuild tasks (`Microsoft.Build.Packaging.Pri.Tasks.dll`), which ship with a multi-GB Visual Studio component not present on this machine. Since no code in this phase actually uses a WinUI XAML surface — `Program.cs` hosts a plain Win32 message-only window (`HWND_MESSAGE`) and message loop — the WindowsAppSDK/WinUI package references were dropped with no functional loss. This should be revisited before Phase 2, which does need real UI (settings, workspace list).

## Files created

18 source files across 5 projects (`WindowsSpaces.Core`, `.Platform`, `.App`, `.TestApp`, `.Tests`), plus `WindowsSpaces.sln` and `global.json`. Full list: `git diff --stat 2120500^..68fe2d0` (scaffold commit through the final acceptance-test commit) on branch `phase0-spike`.

## Win32 APIs used

`EnumDisplayMonitors`, `GetMonitorInfo`, `MonitorFromWindow`, `EnumWindows`, `IsWindowVisible`, `IsWindow`, `GetWindowTextLength`/`GetWindowText`, `GetWindowThreadProcessId`, `GetWindow`, `GetWindowLong`, `GetWindowRect`, `GetWindowPlacement`, `SetWindowPlacement`, `ShowWindow`, `SetWindowPos`, `SetForegroundWindow`, `GetForegroundWindow`, `SetWinEventHook`, `UnhookWinEvent`, `RegisterHotKey`, `UnregisterHotKey`, `Shell_NotifyIcon`, `CreateWindowEx`, `RegisterClassEx`, `GetMessage`/`TranslateMessage`/`DispatchMessage`, `GetModuleHandle`.

## Test results

- **Unit tests (Core, fakes only, no real windows):** 12/12 passing — `DomainModelTests` (3), `WindowTrackerTests` (3), `WorkspaceManagerTests` (6, including the latest-request-wins queue and the operation guard).
- **Manual integration tests (real Win32 adapter, this machine):** `MonitorApiTests` 2/2 passing (real monitor enumeration, distinct IDs across the 2 attached monitors), `WindowApiTests` 2/2 passing (real window enumeration, hide/show round-trip).
- **End-to-end acceptance test** (`WorkspaceManagerAcceptanceTests`, real `WorkspaceManager` + real Win32 windows from a running `WindowsSpaces.TestApp` + this machine's real 2-monitor setup): 1/1 passing, covering parent-spec AC-001, AC-002, AC-003, AC-007 in sequence. Verified independently by querying real window positions after the test ran: `SpacesTest-Normal-2` at (50,50) on Monitor A (origin 0,0) and `SpacesTest-Normal-1` at (1970,-797) on Monitor B (origin 1920,-847) — exactly the offsets the test applied — both visible after the final `ShowAllWindows` call.
- **App smoke test:** launched the built `WindowsSpaces.App.exe` directly; it started cleanly, registered all 5 hotkeys and the tray icon with no exceptions, and stayed responsive for the observation window.
- Total: 16/16 automated tests passing (12 unit + 4 manual/integration), plus the manual App launch smoke test.

## Known Windows limitations

- **WinUI 3 / Windows App SDK requires a large local SDK install.** The Appx packaging MSBuild tasks (`Microsoft.Build.Packaging.Pri.Tasks.dll`) aren't installable via NuGet alone — they need the Windows 10/11 SDK component normally bundled with Visual Studio. This blocked the originally-planned WinUI 3 App project; worked around by dropping those packages since this phase needs no XAML UI. **This is a real blocker for Phase 2**, which does need a settings/workspace UI, and should be resolved (install the Windows SDK / relevant VS workload) before that phase starts.
- **`P/Invoke SetLastError` must be explicit.** None of the original P/Invoke declarations set `SetLastError = true`, so `GetLastError()` calls returned stale/unrelated codes rather than the true failure reason — this actively caused a misdiagnosis during implementation (see Compatibility issues below). Fixed for every fallible declaration; this is now a pattern to follow for any new P/Invoke added in later phases.
- **`WNDCLASSEX` string marshaling defaults to ANSI**, not Unicode, unless `[StructLayout(..., CharSet = CharSet.Unicode)]` is set explicitly on the struct — independent of the `CharSet.Unicode` set on the `DllImport` attributes of the functions that consume it. This mismatch silently registered a window class under a different name than `CreateWindowEx` looked up, producing `ERROR_CANNOT_FIND_WND_CLASS` (1407). Worth flagging for anyone adding more native window classes in later phases.
- **Machine had only the .NET *runtime* installed, not the SDK**, and later turned out to have a user-local .NET SDK install (`%LOCALAPPDATA%\Microsoft\dotnet`) separate from the machine-wide runtime at `C:\Program Files\dotnet`, plus both .NET 8 and .NET 10 SDKs installed side-by-side. Without a `global.json` pinning to 8.0.424, `dotnet new sln` defaulted to the .NET 10 tooling's new `.slnx` format. Not a Windows API limitation, but a real environment-setup gotcha worth documenting for anyone else setting up this repo.

## Compatibility issues

Not exercised in this phase: no Electron/Chromium, UWP, elevated-app, or exclusive-fullscreen windows were tested — the acceptance pass used only `WindowsSpaces.TestApp`'s plain WinForms windows. This matches the phase's scope (compatibility matrix testing is explicitly deferred past the spike).

## Crash/recovery analysis

Bounded to emergency show-all only, as scoped (no persistence or crash markers in this phase). `WorkspaceManager.ShowAllWindows()` was verified end-to-end: after hiding a window via a workspace switch, `ShowAllWindows()` reliably made it visible again without altering its workspace assignment. No crash-during-transition scenario was exercised (would require killing the process mid-`SwitchWorkspace`, which needs the persistence/recovery-marker machinery explicitly deferred to Phase 1+).

## Performance observations

Not formally profiled (no CPU/memory sampling under load). Qualitatively: the acceptance test's real `SwitchWorkspace`/`ShowAllWindows` calls against real windows completed in under 1ms each — `SetWindowPos`/`ShowWindow` are direct, synchronous syscalls with no artificial latency, consistent with the <100ms perceived-switch target in the parent spec. No sustained idle/active CPU measurement was taken; the WinEvent dispatch loop's 10ms poll-when-empty (`WinEventHook.DispatchLoop`) is a plausible place to look if idle CPU turns out to matter in Phase 1.

## Recommendation

**Proceed to Phase 1.** The core mechanism — independent per-monitor workspace switching via application-level hide/show/move, backed by a latest-request-wins transition queue and an operation guard against feedback loops — works reliably against real Win32 windows on real multi-monitor hardware, matching the parent spec's definition of technical success (two monitors, two spaces each, independent switching, zero lost windows in every path tested).

Two items should be addressed early in Phase 1 rather than deferred further:
1. Resolve the WinUI 3/Windows App SDK build blocker (install the Windows SDK component) before Phase 2's UI work, since Phase 1 itself has no UI requirement but Phase 2 does.
2. `MonitorApi.MonitorsChanged` is currently a no-op event (never raised) — Phase 1's monitor disconnect/reconnect handling (FR-005, AC-004) will need `WM_DISPLAYCHANGE` wired into it.
