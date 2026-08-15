namespace WindowsSpaces.Core;

public sealed record IpcRequest(string Command, IReadOnlyDictionary<string, string>? Arguments = null);
public sealed record IpcResponse(bool Success, string? Error, string? Data);
