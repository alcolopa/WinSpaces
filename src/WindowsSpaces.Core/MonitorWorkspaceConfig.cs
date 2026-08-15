namespace WindowsSpaces.Core;

public sealed record MonitorWorkspaceConfig(string MonitorId, IReadOnlyList<WorkspaceDefinition> Workspaces);
