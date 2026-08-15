namespace WindowsSpaces.Core;

/// <summary>
/// Load() must never throw: any missing/corrupt/invalid/future-schema config
/// returns null so callers fall back to AppConfiguration.CreateDefault
/// ("never require perfect previous state to start").
/// </summary>
public interface IConfigurationStore
{
    AppConfiguration? Load();
    void Save(AppConfiguration config);
}
