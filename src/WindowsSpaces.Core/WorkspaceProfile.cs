using System.Collections.Generic;

namespace WindowsSpaces.Core;

/// <summary>
/// A profile representing a saved configuration of active workspaces across physical monitors.
/// </summary>
public sealed record WorkspaceProfile(
    string Name,
    IReadOnlyDictionary<string, string> ActiveWorkspaceByMonitor,
    IReadOnlyList<WindowProfileState>? Windows = null,
    string? EnterCommand = null
);
