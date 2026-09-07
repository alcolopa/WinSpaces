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

    /// <summary>
    /// Upper bound on spaces per monitor. Spaces are created on demand at
    /// runtime, so this is a sanity ceiling rather than the old
    /// one-space-per-number-key limit — only the first
    /// <see cref="MaxDirectSwitchWorkspaces"/> get a "jump straight there"
    /// hotkey; the rest are reached with next/previous or the overview.
    /// </summary>
    public const int MaxWorkspacesPerMonitor = 20;

    /// <summary>How many spaces can be bound to a direct number-key hotkey (1-9).</summary>
    public const int MaxDirectSwitchWorkspaces = 9;

    public IReadOnlyList<ApplicationRule> ActiveRules => Rules ?? Array.Empty<ApplicationRule>();
    public IReadOnlyList<WorkspaceProfile> ActiveProfiles => Profiles ?? Array.Empty<WorkspaceProfile>();

    public static AppConfiguration CreateDefault(IEnumerable<Monitor> monitors)
    {
        // One space per monitor. Extra spaces are created on demand
        // (Ctrl+Alt+Plus, or the overview's add button) — starting with an
        // empty second space just gives every monitor a space nothing is in.
        var monitorConfigs = monitors
            .Select(m => new MonitorWorkspaceConfig(m.Id, new[]
            {
                new WorkspaceDefinition($"{m.Id}:1", "Space 1", 1)
            }))
            .ToList();

        var hotkeys = new List<HotkeyBinding>
        {
            // Ctrl+Alt+N per spec §"Default shortcuts". Plain Ctrl+N is not
            // usable as a global default: RegisterHotKey takes it system-wide
            // and every browser/editor loses its tab-switching shortcut.
            new(HotkeyAction.SwitchWorkspace, 1, ModifierKeys.Control | ModifierKeys.Alt, 0x31),
            new(HotkeyAction.SwitchWorkspace, 2, ModifierKeys.Control | ModifierKeys.Alt, 0x32),
            new(HotkeyAction.MoveToWorkspace, 1, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x31),
            new(HotkeyAction.MoveToWorkspace, 2, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x32),
            // VK_HOME (0x24), not VK_ESCAPE: Ctrl+Shift+Esc is Windows' own
            // reserved Task Manager shortcut, so RegisterHotKey for it always
            // fails with ERROR_HOTKEY_ALREADY_REGISTERED.
            new(HotkeyAction.ShowAllWindows, 0, ModifierKeys.Control | ModifierKeys.Shift, 0x24),
            new(HotkeyAction.ShowOverview, 0, ModifierKeys.Control, 0x26),

            // Relative navigation is the primary way to reach spaces beyond
            // the first few, and the only way to reach spaces created at
            // runtime (which have no number-key binding of their own).
            // VK_RIGHT (0x27) / VK_LEFT (0x25).
            new(HotkeyAction.NextWorkspace, 0, ModifierKeys.Control | ModifierKeys.Alt, 0x27),
            new(HotkeyAction.PreviousWorkspace, 0, ModifierKeys.Control | ModifierKeys.Alt, 0x25),
            new(HotkeyAction.MoveToNextWorkspace, 0, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x27),
            new(HotkeyAction.MoveToPreviousWorkspace, 0, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x25),

            // Create/delete a space on the monitor under the cursor.
            // VK_OEM_PLUS (0xBB) / VK_OEM_MINUS (0xBD) — the same pairing
            // Windows itself uses for adding and removing virtual desktops.
            new(HotkeyAction.CreateWorkspace, 0, ModifierKeys.Control | ModifierKeys.Alt, 0xBB),
            new(HotkeyAction.CloseWorkspace, 0, ModifierKeys.Control | ModifierKeys.Alt, 0xBD),

            // Send the focused window to the next/previous monitor and follow
            // it there. VK_DOWN (0x28) / VK_UP (0x26) — Up/Down free up the
            // Left/Right pair (already Ctrl+Alt+Shift for the within-monitor
            // move) for the monitor axis instead.
            new(HotkeyAction.MoveToNextMonitor, 0, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x28),
            new(HotkeyAction.MoveToPreviousMonitor, 0, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, 0x26)
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
