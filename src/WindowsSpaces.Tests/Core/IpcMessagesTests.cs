using System.Collections.Generic;
using System.Text.Json;
using WindowsSpaces.Core;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class IpcMessagesTests
{
    [Fact]
    public void IpcRequest_SerializesAndDeserializes()
    {
        var request = new IpcRequest("switch", new Dictionary<string, string>
        {
            { "MonitorId", "MON-1" },
            { "WorkspaceId", "MON-1:2" }
        });

        string json = JsonSerializer.Serialize(request);
        var deserialized = JsonSerializer.Deserialize<IpcRequest>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("switch", deserialized.Command);
        Assert.NotNull(deserialized.Arguments);
        Assert.Equal("MON-1", deserialized.Arguments["MonitorId"]);
        Assert.Equal("MON-1:2", deserialized.Arguments["WorkspaceId"]);
    }

    [Fact]
    public void IpcResponse_SerializesAndDeserializes()
    {
        var response = new IpcResponse(Success: true, Error: null, Data: "Some status data");

        string json = JsonSerializer.Serialize(response);
        var deserialized = JsonSerializer.Deserialize<IpcResponse>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Success);
        Assert.Null(deserialized.Error);
        Assert.Equal("Some status data", deserialized.Data);
    }
}
