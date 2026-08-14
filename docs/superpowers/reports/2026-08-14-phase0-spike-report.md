# Windows Spaces — Phase 0 Spike Report

**Date:** 2026-08-14

## Architecture summary

The layering held as specified: `WindowsSpaces.Core` (domain models, interfaces, `WindowTracker`, `WorkspaceManager`) has zero Win32 or Windows-specific references — confirmed by its `net8.0` (non-`-windows`) target framework, which would fail to build if any Win32 type leaked in. `WindowsSpaces.Platform` implements the Core interfaces via `user32.dll`/`kernel32.dll` P/Invoke. `WindowsSpaces.App` is the composition root: it never calls window-management Win32 functions directly, only `WorkspaceManager`/`HotkeyManager`; the one exception is `TrayIcon.cs`, which uses `Shell_NotifyIcon` directly since a tray icon is UI chrome, not window management, per the plan's own carve-out.

One deviation from the original design: `WindowsSpaces.App` does **not** use WinUI 3 / Windows App SDK. Building against those packages requires the Windows 10/11 SDK's Appx packaging MSBuild tasks (`Microsoft.Build.Packaging.Pri.Tasks.dll`), which ship with a multi-GB Visual Studio component not present on this machine. Since no code in this phase actually uses a WinUI XAML surface — `Program.cs` hosts a plain Win32 message-only window (`HWND_MESSAGE`) and message loop — the WindowsAppSDK/WinUI package references were dropped with no functional loss. This should be revisited before Phase 2, which does need real UI (settings, workspace list).

An independent code review of the branch caught three real functional gaps that the first implementation pass missed, all now fixed and re-verified:
1. `WindowTracker` originally never assigned a discovered window's `MonitorId`/`WorkspaceId` from the real monitor layout, and unconditionally overwrote its own tracked state on every WinEvent — meaning the *running App* would have tracked zero windows against any monitor and the `OperationGuard` was never actually consulted. Fixed: `WindowTracker` now takes `IMonitorManager` + a shared `OperationGuard`, assigns new windows to their current monitor's first workspace, preserves an existing assignment across refresh events (merging in only the volatile fields), reassigns only when a window genuinely moves to a different monitor, and skips reassignment entirely for events raised by this process's own suppressed operations.
2. The "latest-request-wins" transition queue described in the design was not actually a queue — it was a per-monitor lock that serialized and fully executed every request, including intermediate targets. Fixed: `WorkspaceManager.SwitchWorkspace` now uses a real single-worker-per-monitor drain loop where a request arriving while a transition is in flight only overwrites the pending target and returns, so a burst of calls collapses to one execution of the final target.
3. `SetWinEventHook` failures were silently swallowed (return value never checked). Fixed: a failed hook registration now throws with `Marshal.GetLastWin32Error()`.

See the Test results section for how each fix is now covered.

## Files created

18 source files across 5 projects (`WindowsSpaces.Core`, `.Platform`, `.App`, `.TestApp`, `.Tests`), plus `WindowsSpaces.sln` and `global.json`. Full list: `git diff --stat 2120500^..68fe2d0` (scaffold commit through the final acceptance-test commit) on branch `phase0-spike`.

## Win32 APIs used

`EnumDisplayMonitors`, `GetMonitorInfo`, `MonitorFromWindow`, `EnumWindows`, `IsWindowVisible`, `IsWindow`, `GetWindowTextLength`/`GetWindowText`, `GetWindowThreadProcessId`, `GetWindow`, `GetWindowLong`, `GetWindowRect`, `GetWindowPlacement`, `SetWindowPlacement`, `ShowWindow`, `SetWindowPos`, `SetForegroundWindow`, `GetForegroundWindow`, `SetWinEventHook`, `UnhookWinEvent`, `RegisterHotKey`, `UnregisterHotKey`, `Shell_NotifyIcon`, `CreateWindowEx`, `RegisterClassEx`, `GetMessage`/`TranslateMessage`/`DispatchMessage`, `GetModuleHandle`.

## Test results

- **Unit tests (Core, fakes only, no real windows):** 17/17 passing — `DomainModelTests` (3), `WindowTrackerTests` (6, including the two added after review: assignment-preservation across a routine refresh event, reassignment when a window genuinely changes monitor, and the operation-guard suppression path), `WorkspaceManagerTests` (8, including the added concurrency test that proves the transition queue actually collapses rather than executing every intermediate target — verified by asserting exactly 1 `Hide` call occurred where an uncollapsed implementation would produce 3).
- **Manual integration tests (real Win32 adapter, this machine):** `MonitorApiTests` 2/2 passing (real monitor enumeration, distinct IDs across the 2 attached monitors), `WindowApiTests` 2/2 passing (real window enumeration, hide/show round-trip).
- **End-to-end acceptance test** (`WorkspaceManagerAcceptanceTests`, real `WorkspaceManager` + real Win32 windows from a running `WindowsSpaces.TestApp` + this machine's real 2-monitor setup): 1/1 passing, covering parent-spec AC-001, AC-002, AC-003, AC-007 in sequence. Verified independently by querying real window positions after the test ran: `SpacesTest-Normal-2` at (50,50) on Monitor A (origin 0,0) and `SpacesTest-Normal-1` at (1970,-797) on Monitor B (origin 1920,-847) — exactly the offsets the test applied — both visible after the final `ShowAllWindows` call. **Caveat:** this test constructs `WorkspaceManager`/`WindowTracker` directly and does not call `WinEventHook.Start()`, so it proves the switching algorithm and (post-review) the real assignment logic, but not the live WinEvent-driven pipeline end-to-end.
- **App smoke test:** launched the built `WindowsSpaces.App.exe` directly; it started cleanly, registered all 5 hotkeys and the tray icon with no exceptions, and stayed responsive for the observation window, both before and after the review-driven fixes.
- Total: 22/22 automated tests passing (17 unit + 5 manual/integration), plus the manual App launch smoke test.
- **Known automated-coverage gap:** no test drives the running `WindowsSpaces.App` end-to-end via its actual global hotkeys (this requires either physical key presses or simulated input, neither of which was exercised this session). The wiring is now correct by code review and by the unit/acceptance tests above, but a hotkey-triggered real-window switch through the live `WinEventHook` pipeline was not itself observed running.

## Known Windows limitations

- **WinUI 3 / Windows App SDK requires a large local SDK install.** The Appx packaging MSBuild tasks (`Microsoft.Build.Packaging.Pri.Tasks.dll`) aren't installable via NuGet alone — they need the Windows 10/11 SDK component normally bundled with Visual Studio. This blocked the originally-planned WinUI 3 App project; worked around by dropping those packages since this phase needs no XAML UI. **This is a real blocker for Phase 2**, which does need a settings/workspace UI, and should be resolved (install the Windows SDK / relevant VS workload) before that phase starts.
- **`P/Invoke SetLastError` must be explicit.** None of the original P/Invoke declarations set `SetLastError = true`, so `GetLastError()` calls returned stale/unrelated codes rather than the true failure reason — this actively caused a misdiagnosis during implementation (see Compatibility issues below). Fixed for every fallible declaration; this is now a pattern to follow for any new P/Invoke added in later phases.
- **`WNDCLASSEX` string marshaling defaults to ANSI**, not Unicode, unless `[StructLayout(..., CharSet = CharSet.Unicode)]` is set explicitly on the struct — independent of the `CharSet.Unicode` set on the `DllImport` attributes of the functions that consume it. This mismatch silently registered a window class under a different name than `CreateWindowEx` looked up, producing `ERROR_CANNOT_FIND_WND_CLASS` (1407). Worth flagging for anyone adding more native window classes in later phases.
- **Machine had only the .NET *runtime* installed, not the SDK**, and later turned out to have a user-local .NET SDK install (`%LOCALAPPDATA%\Microsoft\dotnet`) separate from the machine-wide runtime at `C:\Program Files\dotnet`, plus both .NET 8 and .NET 10 SDKs installed side-by-side. Without a `global.json` pinning to 8.0.424, `dotnet new sln` defaulted to the .NET 10 tooling's new `.slnx` format. Not a Windows API limitation, but a real environment-setup gotcha worth documenting for anyone else setting up this repo.
- **`WINDOWPLACEMENT.showCmd` uses `SW_SHOWMINIMIZED` (2) for a minimized window, not `SW_MINIMIZE` (6).** The first implementation checked the wrong constant, so `WindowState.IsMinimized` was always false even for genuinely minimized windows (this had no behavioral impact yet since `WorkspaceManager` doesn't currently branch on it, but would have silently broken any Phase 1 logic that does). Fixed.
- **`SetWinEventHook`'s failure return (`NULL`) is easy to miss.** It's a normal `nint`, not an `HRESULT`, so nothing about the call signature forces a check — the first implementation added every result straight to the hooks list. Fixed with an explicit check; worth being deliberate about this for any future `SetWinEventHook`-style API in later phases.

## Compatibility issues

Not exercised in this phase: no Electron/Chromium, UWP, elevated-app, or exclusive-fullscreen windows were tested — the acceptance pass used only `WindowsSpaces.TestApp`'s plain WinForms windows. This matches the phase's scope (compatibility matrix testing is explicitly deferred past the spike).

## Crash/recovery analysis

Bounded to emergency show-all only, as scoped (no persistence or crash markers in this phase). `WorkspaceManager.ShowAllWindows()` was verified end-to-end: after hiding a window via a workspace switch, `ShowAllWindows()` reliably made it visible again without altering its workspace assignment. No crash-during-transition scenario was exercised (would require killing the process mid-`SwitchWorkspace`, which needs the persistence/recovery-marker machinery explicitly deferred to Phase 1+).

## Performance observations

Not formally profiled (no CPU/memory sampling under load). Qualitatively: the acceptance test's real `SwitchWorkspace`/`ShowAllWindows` calls against real windows completed in under 1ms each — `SetWindowPos`/`ShowWindow` are direct, synchronous syscalls with no artificial latency, consistent with the <100ms perceived-switch target in the parent spec. No sustained idle/active CPU measurement was taken; the WinEvent dispatch loop's 10ms poll-when-empty (`WinEventHook.DispatchLoop`) is a plausible place to look if idle CPU turns out to matter in Phase 1.

## Recommendation

**Proceed to Phase 1.** The core mechanism — independent per-monitor workspace switching via application-level hide/show/move, backed by a latest-request-wins transition queue and an operation guard against feedback loops — works reliably against real Win32 windows on real multi-monitor hardware, matching the parent spec's definition of technical success (two monitors, two spaces each, independent switching, zero lost windows in every path tested).

Three items should be addressed early in Phase 1 rather than deferred further:
1. Resolve the WinUI 3/Windows App SDK build blocker (install the Windows SDK component) before Phase 2's UI work, since Phase 1 itself has no UI requirement but Phase 2 does.
2. `MonitorApi.MonitorsChanged` is currently a no-op event (never raised) — Phase 1's monitor disconnect/reconnect handling (FR-005, AC-004) will need `WM_DISPLAYCHANGE` wired into it.
3. Get real evidence for the running App's hotkey-triggered switching path (the one automated-coverage gap noted above) — either via simulated input (`SendInput`) in a manual/integration test, or a deliberate manual walkthrough with a human at the keyboard — before treating the App itself, not just its underlying `WorkspaceManager`, as proven.
