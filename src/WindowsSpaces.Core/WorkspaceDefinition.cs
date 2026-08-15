namespace WindowsSpaces.Core;

/// <summary>Configured (not yet live) workspace: Id is "{MonitorId}:{Index}", matching Workspace.Id.</summary>
public sealed record WorkspaceDefinition(string Id, string Name, int Index);
