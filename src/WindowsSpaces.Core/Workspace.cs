namespace WindowsSpaces.Core;

/// <summary>
/// A logical workspace scoped to a single monitor. Id is "{MonitorId}:{Index}".
/// </summary>
public sealed record Workspace(
    string Id,
    string MonitorId,
    string Name,
    int Index);
