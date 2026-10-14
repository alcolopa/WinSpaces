namespace WindowsSpaces.Core;

/// <param name="DisplayName">
/// User-chosen label for the physical display, or null to use the default
/// derived from the id. Label only — <paramref name="MonitorId"/> remains the
/// identity everything is keyed on.
/// </param>
public sealed record MonitorWorkspaceConfig(
    string MonitorId,
    IReadOnlyList<WorkspaceDefinition> Workspaces,
    string? DisplayName = null);
