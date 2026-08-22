namespace WindowsSpaces.Persistence;

/// <summary>
/// Resolves where the persisted AppConfiguration file actually lives.
/// The pointer file itself always stays at a fixed, local-only path (never
/// synced) so a machine can find its config before it has loaded it. When
/// no pointer exists, config lives at <see cref="DefaultConfigFilePath"/>;
/// otherwise it lives at "config.json" inside the folder the pointer names —
/// typically a folder synced by OneDrive/Dropbox/Syncthing/etc., making
/// cross-machine config sync a side effect of pointing two machines at the
/// same synced folder rather than a bespoke sync protocol.
/// </summary>
public sealed class ConfigSyncLocation
{
    private readonly string _pointerFilePath;

    public string DefaultConfigFilePath { get; }

    public ConfigSyncLocation(string pointerFilePath, string defaultConfigFilePath)
    {
        _pointerFilePath = pointerFilePath;
        DefaultConfigFilePath = defaultConfigFilePath;
    }

    /// <summary>
    /// The folder the user has chosen to sync config through, or null when
    /// using the default per-machine location. Fails open (returns null) on
    /// any read error, same as JsonConfigurationStore.Load().
    /// </summary>
    public string? GetSyncFolder()
    {
        if (!File.Exists(_pointerFilePath)) return null;

        string folder;
        try
        {
            folder = File.ReadAllText(_pointerFilePath).Trim();
        }
        catch (Exception)
        {
            return null;
        }

        return folder.Length == 0 ? null : folder;
    }

    public string ResolveConfigFilePath()
    {
        var folder = GetSyncFolder();
        return folder is null ? DefaultConfigFilePath : Path.Combine(folder, "config.json");
    }

    /// <summary>
    /// Points config at <paramref name="folder"/>, or back to
    /// <see cref="DefaultConfigFilePath"/> when null.
    /// </summary>
    public void SetSyncFolder(string? folder)
    {
        if (folder is null)
        {
            File.Delete(_pointerFilePath);
            return;
        }

        var directory = Path.GetDirectoryName(_pointerFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_pointerFilePath, folder);
    }
}
