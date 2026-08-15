using System.Drawing;

namespace WindowsSpaces.Core;

/// <summary>
/// Mutable runtime state for one tracked top-level window.
/// </summary>
public sealed class WindowState
{
    public required nint Hwnd { get; init; }
    public required int ProcessId { get; init; }
    public string? ProcessPath { get; set; }
    public string? WindowClass { get; set; }
    public string? Title { get; set; }
    public string? MonitorId { get; set; }
    public string? WorkspaceId { get; set; }
    public bool IsVisible { get; set; }
    public bool IsMinimized { get; set; }
    public bool IsMaximized { get; set; }
    public Rectangle NormalBounds { get; set; }
    public required DateTimeOffset LastUpdated { get; set; }
}
