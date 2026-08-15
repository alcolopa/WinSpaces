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
