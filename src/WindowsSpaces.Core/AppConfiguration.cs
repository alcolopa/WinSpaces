namespace WindowsSpaces.Core;

public sealed record AppConfiguration(
    int SchemaVersion,
    IReadOnlyList<MonitorWorkspaceConfig> Monitors,
    IReadOnlyList<HotkeyBinding> Hotkeys,
    IReadOnlyList<ApplicationRule>? Rules = null,
    IReadOnlyList<WorkspaceProfile>? Profiles = null,
    string? ActiveProfileName = null,
    bool EnableTransitions = true)
{
    public const int CurrentSchemaVersion = 1;
    private const int MaxWorkspacesPerMonitor = 9;

    public IReadOnlyList<ApplicationRule> ActiveRules => Rules ?? Array.Empty<ApplicationRule>();
    public IReadOnlyList<WorkspaceProfile> ActiveProfiles => Profiles ?? Array.Empty<WorkspaceProfile>();

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
            new(HotkeyAction.ShowAllWindows, 0, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x1B),
            new(HotkeyAction.ShowOverview, 0, ModifierKeys.Control | ModifierKeys.Alt, 0x26)
        };

        return new AppConfiguration(
            CurrentSchemaVersion,
            monitorConfigs,
            hotkeys,
            Array.Empty<ApplicationRule>(),
            Array.Empty<WorkspaceProfile>(),
            null
        );
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

        if (Rules != null)
        {
            foreach (var rule in Rules)
            {
                if (string.IsNullOrWhiteSpace(rule.Id))
                {
                    error = "A rule has an empty ID.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(rule.RuleName))
                {
                    error = $"Rule '{rule.Id}' has an empty rule name.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(rule.ProcessPath) &&
                    string.IsNullOrWhiteSpace(rule.WindowClass) &&
                    string.IsNullOrWhiteSpace(rule.WindowTitle))
                {
                    error = $"Rule '{rule.RuleName}' has no matching criteria (ProcessPath, WindowClass, and WindowTitle are all empty).";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(rule.TargetMonitorId))
                {
                    error = $"Rule '{rule.RuleName}' has an empty target monitor ID.";
                    return false;
                }

                if (rule.TargetWorkspaceIndex is < 1 or > MaxWorkspacesPerMonitor)
                {
                    error = $"Rule '{rule.RuleName}' target workspace index must be between 1 and {MaxWorkspacesPerMonitor}.";
                    return false;
                }
            }
        }

        if (Profiles != null)
        {
            var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in Profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.Name))
                {
                    error = "A workspace profile has an empty name.";
                    return false;
                }

                if (!profileNames.Add(profile.Name))
                {
                    error = $"Duplicate workspace profile name '{profile.Name}'.";
                    return false;
                }

                foreach (var kv in profile.ActiveWorkspaceByMonitor)
                {
                    var monitorId = kv.Key;
                    var workspaceId = kv.Value;

                    var monitorConfig = Monitors.FirstOrDefault(m => m.MonitorId == monitorId);
                    if (monitorConfig == null)
                    {
                        error = $"Profile '{profile.Name}' references unknown monitor ID '{monitorId}'.";
                        return false;
                    }

                    if (!monitorConfig.Workspaces.Any(w => w.Id == workspaceId))
                    {
                        error = $"Profile '{profile.Name}' references unknown workspace ID '{workspaceId}' on monitor '{monitorId}'.";
                        return false;
                    }
                }
            }

            if (!string.IsNullOrEmpty(ActiveProfileName))
            {
                if (!profileNames.Contains(ActiveProfileName))
                {
                    error = $"Active profile name '{ActiveProfileName}' does not match any configured profile.";
                    return false;
                }
            }
        }

        error = null;
        return true;
    }
}
