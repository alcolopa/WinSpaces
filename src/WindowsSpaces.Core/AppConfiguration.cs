namespace WindowsSpaces.Core;

public sealed record AppConfiguration(
    int SchemaVersion,
    IReadOnlyList<MonitorWorkspaceConfig> Monitors,
    IReadOnlyList<HotkeyBinding> Hotkeys)
{
    public const int CurrentSchemaVersion = 1;
    private const int MaxWorkspacesPerMonitor = 9;

    public static AppConfiguration CreateDefault(IEnumerable<Monitor> monitors)
    {
        var monitorConfigs = monitors
            .Select(m => new MonitorWorkspaceConfig(m.Id, new[]
            {
                new WorkspaceDefinition($"{m.Id}:1", "Space 1", 1),
                new WorkspaceDefinition($"{m.Id}:2", "Space 2", 2)
            }))
            .ToList();

        var hotkeys = new List<HotkeyBinding>
        {
            new(HotkeyAction.SwitchWorkspace, 1, ModifierKeys.Control | ModifierKeys.Alt, 0x31),
            new(HotkeyAction.SwitchWorkspace, 2, ModifierKeys.Control | ModifierKeys.Alt, 0x32),
            new(HotkeyAction.MoveToWorkspace, 1, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x31),
            new(HotkeyAction.MoveToWorkspace, 2, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x32),
            new(HotkeyAction.ShowAllWindows, 0, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x1B)
        };

        return new AppConfiguration(CurrentSchemaVersion, monitorConfigs, hotkeys);
    }

    public bool Validate(out string? error)
    {
        foreach (var monitor in Monitors)
        {
            if (monitor.Workspaces.Count is < 1 or > MaxWorkspacesPerMonitor)
            {
                error = $"Monitor {monitor.MonitorId} must have between 1 and {MaxWorkspacesPerMonitor} workspaces.";
                return false;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var workspace in monitor.Workspaces)
            {
                if (string.IsNullOrWhiteSpace(workspace.Name))
                {
                    error = $"Monitor {monitor.MonitorId} has a workspace with an empty name.";
                    return false;
                }

                if (!names.Add(workspace.Name))
                {
                    error = $"Monitor {monitor.MonitorId} has duplicate workspace name '{workspace.Name}'.";
                    return false;
                }
            }
        }

        for (var i = 0; i < Hotkeys.Count; i++)
        {
            for (var j = i + 1; j < Hotkeys.Count; j++)
            {
                if (Hotkeys[i].ConflictsWith(Hotkeys[j]))
                {
                    error = $"Hotkeys for {Hotkeys[i].Action} and {Hotkeys[j].Action} use the same key combination.";
                    return false;
                }
            }
        }

        error = null;
        return true;
    }
}
