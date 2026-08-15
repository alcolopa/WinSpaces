using System.Text.Json;
using WindowsSpaces.Core;

namespace WindowsSpaces.Persistence;

/// <summary>
/// JSON-file-backed IConfigurationStore. Load() fails open (returns null)
/// on any error — missing file, corrupt JSON, failed Validate(), or a
/// SchemaVersion this build doesn't understand — per the "never require
/// perfect previous state to start" recovery rule. Save() writes to a
/// temp file and swaps it in with File.Move(overwrite: true) so a crash
/// mid-write can't corrupt the existing config.
/// </summary>
public sealed class JsonConfigurationStore : IConfigurationStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _filePath;

    public JsonConfigurationStore(string filePath)
    {
        _filePath = filePath;
    }

    public AppConfiguration? Load()
    {
        if (!File.Exists(_filePath)) return null;

        AppConfiguration? config;
        try
        {
            var json = File.ReadAllText(_filePath);
            config = JsonSerializer.Deserialize<AppConfiguration>(json, Options);
        }
        // Deliberately broad: Load() is contractually fail-open, and the failure
        // modes are open-ended (JsonException, IOException, UnauthorizedAccessException,
        // NotSupportedException, ...). Any of them must yield defaults rather than
        // crash startup, so filtering by exception type here would be a bug.
        catch (Exception)
        {
            return null;
        }

        if (config is null) return null;
        if (config.SchemaVersion != AppConfiguration.CurrentSchemaVersion) return null;
        if (!config.Validate(out _)) return null;

        return config;
    }

    public void Save(AppConfiguration config)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(config, Options));
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
