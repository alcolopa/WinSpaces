# Changelog

## Unreleased
- Single-instance guard: a second launch exits instead of fighting the first for hotkeys and the IPC pipe.
- Crash recovery: windows hidden by a session that was killed (Task Manager, power loss) are shown again at next launch, via a journal at `%LOCALAPPDATA%\WindowsSpaces\hidden-windows.txt`.
- Settings: "Start with Windows" toggle (per-user Run key).
- README: overview is called "Spaces Overview".

## Earlier
Phases 0-4 of the spec: core engine, tray/settings/rules/profiles/diagnostics UI, Spaces Overview with thumbnails and drag/drop, slide transitions, CLI + IPC, config sync, installer.
