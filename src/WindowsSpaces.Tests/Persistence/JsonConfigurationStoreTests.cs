using System;
using System.IO;
using WindowsSpaces.Core;
using WindowsSpaces.Persistence;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.Persistence;

public class JsonConfigurationStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public JsonConfigurationStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WindowsSpacesTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static readonly Monitor MonA = new("MON-A", "\\\\.\\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), IsPrimary: true);

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var store = new JsonConfigurationStore(_path);

        Assert.Null(store.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new JsonConfigurationStore(_path);
        var original = AppConfiguration.CreateDefault(new[] { MonA });

        store.Save(original);
        var loaded = store.Load();

        // AppConfiguration's IReadOnlyList<T> properties deserialize into List<T>,
        // which compares by reference under record equality — so compare the
        // serialized JSON representations for structural equality instead.
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(original),
            System.Text.Json.JsonSerializer.Serialize(loaded));
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        var nestedPath = Path.Combine(_tempDir, "nested", "sub", "config.json");
        var store = new JsonConfigurationStore(nestedPath);

        store.Save(AppConfiguration.CreateDefault(new[] { MonA }));

        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNull()
    {
        File.WriteAllText(_path, "{ not valid json ");
        var store = new JsonConfigurationStore(_path);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_ValidJsonButFailsValidation_ReturnsNull()
    {
        File.WriteAllText(_path, """
        {
          "SchemaVersion": 1,
          "Monitors": [ { "MonitorId": "MON-A", "Workspaces": [] } ],
          "Hotkeys": []
        }
        """);
        var store = new JsonConfigurationStore(_path);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_UnknownFutureSchemaVersion_ReturnsNull()
    {
        var store = new JsonConfigurationStore(_path);
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with { SchemaVersion = AppConfiguration.CurrentSchemaVersion + 1 };
        store.Save(config);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_DoesNotLeaveTempFileBehind()
    {
        var store = new JsonConfigurationStore(_path);
        store.Save(AppConfiguration.CreateDefault(new[] { MonA }));

        var leftoverTempFiles = Directory.GetFiles(_tempDir, "*.tmp");
        Assert.Empty(leftoverTempFiles);
    }
}
