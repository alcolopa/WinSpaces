using WindowsSpaces.Core;

namespace WindowsSpaces.App.ViewModels;

public sealed class SettingsViewModel
{
    private readonly AppConfiguration _original;
    private List<MonitorWorkspaceConfig> _monitors;

    public SettingsViewModel(AppConfiguration current)
    {
        _original = current;
        _monitors = current.Monitors.Select(m => m with { Workspaces = m.Workspaces.ToList() }).ToList();
    }

    public IReadOnlyList<MonitorWorkspaceConfig> Monitors => _monitors;

    public void AddWorkspace(string monitorId)
    {
        var index = IndexOf(monitorId);
        var monitor = _monitors[index];
        var nextIndex = monitor.Workspaces.Count == 0 ? 1 : monitor.Workspaces.Max(w => w.Index) + 1;
        var workspaces = monitor.Workspaces.ToList();
        workspaces.Add(new WorkspaceDefinition($"{monitorId}:{nextIndex}", $"Space {nextIndex}", nextIndex));
        _monitors[index] = monitor with { Workspaces = workspaces };
    }

    public void RemoveWorkspace(string monitorId, string workspaceId)
    {
        var index = IndexOf(monitorId);
        var monitor = _monitors[index];
        var workspaces = monitor.Workspaces.Where(w => w.Id != workspaceId).ToList();
        _monitors[index] = monitor with { Workspaces = workspaces };
    }

    public void RenameWorkspace(string monitorId, string workspaceId, string newName)
    {
        var index = IndexOf(monitorId);
        var monitor = _monitors[index];
        var workspaces = monitor.Workspaces
            .Select(w => w.Id == workspaceId ? w with { Name = newName } : w)
            .ToList();
        _monitors[index] = monitor with { Workspaces = workspaces };
    }

    public bool TrySave(out AppConfiguration updated, out string? error)
    {
        var candidate = _original with { Monitors = _monitors };
        if (!candidate.Validate(out error))
        {
            updated = _original;
            return false;
        }

        updated = candidate;
        return true;
    }

    private int IndexOf(string monitorId) =>
        _monitors.FindIndex(m => m.MonitorId == monitorId) is var i and >= 0
            ? i
            : throw new ArgumentException($"Unknown monitor '{monitorId}'", nameof(monitorId));
}
