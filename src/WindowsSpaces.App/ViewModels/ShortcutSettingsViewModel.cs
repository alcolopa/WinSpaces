using WindowsSpaces.Core;

namespace WindowsSpaces.App.ViewModels;

public sealed class ShortcutSettingsViewModel
{
    private readonly AppConfiguration _original;
    private List<HotkeyBinding> _bindings;

    public ShortcutSettingsViewModel(AppConfiguration current)
    {
        _original = current;
        _bindings = current.Hotkeys.ToList();
    }

    public IReadOnlyList<HotkeyBinding> Bindings => _bindings;

    public void Rebind(HotkeyAction action, int workspaceIndex, ModifierKeys modifiers, int virtualKey)
    {
        var index = _bindings.FindIndex(b => b.Action == action && b.WorkspaceIndex == workspaceIndex);
        if (index < 0)
        {
            throw new ArgumentException($"No existing binding for {action}/{workspaceIndex}");
        }

        _bindings[index] = _bindings[index] with { Modifiers = modifiers, VirtualKey = virtualKey };
    }

    public bool TrySave(out AppConfiguration updated, out string? error)
    {
        var candidate = _original with { Hotkeys = _bindings };
        if (!candidate.Validate(out error))
        {
            updated = _original;
            return false;
        }

        updated = candidate;
        return true;
    }
}
