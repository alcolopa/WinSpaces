# Changelog

## 0.0.1-beta
- Single-instance guard: a second launch exits instead of fighting the first for hotkeys and the IPC pipe.
- Crash recovery: windows hidden by a session that was killed (Task Manager, power loss) are shown again at next launch, via a journal at `%LOCALAPPDATA%\WindowsSpaces\hidden-windows.txt`.
- Settings: "Start with Windows" toggle (per-user Run key).
- README: overview is called "Spaces Overview".

- Fix: published/installed builds crashed opening any window (XamlParseException) because `dotnet publish` omitted `resources.pri` and the `.xbf` files.
- CLI: unknown monitor/space/window ids now fail with an error (they reported success); `add-space` no longer writes phantom monitors into config.json; `--json` embeds `Data` as real JSON and exits non-zero on failure.

## Earlier
Phases 0-4 of the spec: core engine, tray/settings/rules/profiles/diagnostics UI, Spaces Overview with thumbnails and drag/drop, slide transitions, CLI + IPC, config sync, installer.
