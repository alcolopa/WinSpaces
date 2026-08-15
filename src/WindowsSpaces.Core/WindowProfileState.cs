using System.Drawing;

namespace WindowsSpaces.Core;

public sealed record WindowProfileState(
    string ProcessPath,
    string WindowClass,
    string Title,
    string MonitorId,
    string WorkspaceId,
    bool IsMinimized,
    bool IsMaximized,
    Rectangle NormalBounds,
    string? CommandLineArguments = null
);
