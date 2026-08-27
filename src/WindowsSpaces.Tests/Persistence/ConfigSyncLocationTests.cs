using System;
using System.IO;
using WindowsSpaces.Persistence;
using Xunit;

namespace WindowsSpaces.Tests.Persistence;

public class ConfigSyncLocationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _pointerPath;
    private readonly string _defaultConfigPath;

    public ConfigSyncLocationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WindowsSpacesTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _pointerPath = Path.Combine(_tempDir, "sync-location.txt");
        _defaultConfigPath = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void GetSyncFolder_NoPointerFile_ReturnsNull()
    {
        var location = new ConfigSyncLocation(_pointerPath, _defaultConfigPath);

        Assert.Null(location.GetSyncFolder());
    }

    [Fact]
    public void ResolveConfigFilePath_NoPointer_ReturnsDefault()
    {
        var location = new ConfigSyncLocation(_pointerPath, _defaultConfigPath);

        Assert.Equal(_defaultConfigPath, location.ResolveConfigFilePath());
    }

    [Fact]
    public void SetSyncFolder_ThenResolve_PointsAtChosenFolder()
    {
        var location = new ConfigSyncLocation(_pointerPath, _defaultConfigPath);
        var syncFolder = Path.Combine(_tempDir, "OneDrive", "WindowsSpaces");

        location.SetSyncFolder(syncFolder);

        Assert.Equal(syncFolder, location.GetSyncFolder());
        Assert.Equal(Path.Combine(syncFolder, "config.json"), location.ResolveConfigFilePath());
    }

    [Fact]
    public void SetSyncFolder_Null_RevertsToDefaultLocation()
    {
        var location = new ConfigSyncLocation(_pointerPath, _defaultConfigPath);
        location.SetSyncFolder(Path.Combine(_tempDir, "OneDrive", "WindowsSpaces"));

        location.SetSyncFolder(null);

        Assert.Null(location.GetSyncFolder());
        Assert.Equal(_defaultConfigPath, location.ResolveConfigFilePath());
    }

    [Fact]
    public void GetSyncFolder_EmptyPointerFile_ReturnsNull()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_pointerPath)!);
        File.WriteAllText(_pointerPath, "   ");
        var location = new ConfigSyncLocation(_pointerPath, _defaultConfigPath);

        Assert.Null(location.GetSyncFolder());
    }

    [Fact]
    public void SetSyncFolder_CreatesMissingPointerDirectory()
    {
        var nestedPointerPath = Path.Combine(_tempDir, "nested", "sub", "sync-location.txt");
        var location = new ConfigSyncLocation(nestedPointerPath, _defaultConfigPath);

        location.SetSyncFolder(Path.Combine(_tempDir, "OneDrive"));

        Assert.True(File.Exists(nestedPointerPath));
    }
}
