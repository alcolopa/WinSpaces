namespace WindowsSpaces.Core;

/// <summary>
/// A rule that matches windows and assigns them to a specific monitor and workspace index.
/// </summary>
public sealed record ApplicationRule(
    string Id,
    string RuleName,
    string? ProcessPath,
    string? WindowClass,
    string? WindowTitle,
    string TargetMonitorId,
    int TargetWorkspaceIndex)
{
    public bool Matches(string? processPath, string? windowClass, string? windowTitle)
    {
        // Must match at least one criteria
        if (string.IsNullOrEmpty(ProcessPath) && string.IsNullOrEmpty(WindowClass) && string.IsNullOrEmpty(WindowTitle))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(ProcessPath))
        {
            if (processPath == null || !processPath.Contains(ProcessPath, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(WindowClass))
        {
            if (windowClass == null || !string.Equals(windowClass, WindowClass, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrEmpty(WindowTitle))
        {
            if (windowTitle == null || !windowTitle.Contains(WindowTitle, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
