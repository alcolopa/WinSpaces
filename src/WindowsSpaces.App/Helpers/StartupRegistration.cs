using Microsoft.Win32;

namespace WindowsSpaces.App.Helpers;

/// <summary>
/// "Start with Windows" via the per-user Run key. Takes effect immediately
/// (it is OS state, not part of the saved configuration) and needs no admin
/// rights. The single-instance guard makes it harmless if the installer's
/// startup shortcut is also present.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowsSpaces";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception) { return false; }
    }

    /// <returns>True if the registry now matches <paramref name="enabled"/>.</returns>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("Updating start-with-Windows failed", ex);
            return false;
        }
    }
}
