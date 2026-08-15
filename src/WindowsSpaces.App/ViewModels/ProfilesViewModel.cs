using WindowsSpaces.Core;

namespace WindowsSpaces.App.ViewModels;

public sealed class ProfilesViewModel
{
    private readonly AppConfiguration _original;
    private readonly IReadOnlyDictionary<string, string> _currentActiveWorkspaces;
    private readonly List<WorkspaceProfile> _profiles;
    private string? _activeProfileName;

    public ProfilesViewModel(AppConfiguration current, IReadOnlyDictionary<string, string> currentActiveWorkspaces)
    {
        _original = current;
        _currentActiveWorkspaces = currentActiveWorkspaces;
        _profiles = current.ActiveProfiles.ToList();
        _activeProfileName = current.ActiveProfileName;
    }

    public IReadOnlyList<WorkspaceProfile> Profiles => _profiles;
    public string? ActiveProfileName => _activeProfileName;

    public void SaveCurrentAsProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;

        var cleanName = profileName.Trim();
        var index = _profiles.FindIndex(p => string.Equals(p.Name, cleanName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            var originalName = _profiles[index].Name;
            _profiles[index] = new WorkspaceProfile(originalName, _currentActiveWorkspaces.ToDictionary(kv => kv.Key, kv => kv.Value));
        }
        else
        {
            var profile = new WorkspaceProfile(cleanName, _currentActiveWorkspaces.ToDictionary(kv => kv.Key, kv => kv.Value));
            _profiles.Add(profile);
        }
    }

    public void DeleteProfile(string profileName)
    {
        var index = _profiles.FindIndex(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _profiles.RemoveAt(index);
            if (string.Equals(_activeProfileName, profileName, StringComparison.OrdinalIgnoreCase))
            {
                _activeProfileName = null;
            }
        }
    }

    public void SelectProfile(string profileName)
    {
        if (_profiles.Any(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase)))
        {
            _activeProfileName = profileName;
        }
    }

    public bool TrySave(out AppConfiguration updated, out string? error)
    {
        var candidate = _original with { Profiles = _profiles, ActiveProfileName = _activeProfileName };
        if (!candidate.Validate(out error))
        {
            updated = _original;
            return false;
        }

        updated = candidate;
        return true;
    }
}
