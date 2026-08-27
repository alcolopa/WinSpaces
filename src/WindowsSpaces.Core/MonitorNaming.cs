namespace WindowsSpaces.Core;

/// <summary>
/// Turns a monitor's stable id into something worth showing a human.
///
/// Ids come from <c>MONITORINFOEX.szDevice</c> and look like
/// <c>\\.\DISPLAY1</c>. That prefix is a device-namespace artefact, not part
/// of the display's name, so it is stripped for display only — the id itself
/// is never rewritten, because it is the key that config, rules and profiles
/// are stored against (monitor identity must survive reboots and redocking).
/// </summary>
public static class MonitorNaming
{
    private const string DeviceNamespacePrefix = @"\\.\";

    public static string ToDisplayName(string? monitorId)
    {
        if (string.IsNullOrWhiteSpace(monitorId)) return string.Empty;

        var name = monitorId.StartsWith(DeviceNamespacePrefix, StringComparison.Ordinal)
            ? monitorId[DeviceNamespacePrefix.Length..]
            : monitorId.TrimStart('\\');

        return name.Length == 0 ? monitorId : name;
    }
}
