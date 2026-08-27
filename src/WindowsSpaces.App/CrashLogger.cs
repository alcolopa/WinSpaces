namespace WindowsSpaces.App;

/// <summary>
/// Best-effort append-only crash log, separate from Windows Error Reporting,
/// so a .NET-catchable unhandled exception (as opposed to a native/WinRT
/// fast-fail, which WER already records) leaves a trace of what the app
/// itself saw before <see cref="AppDomain.UnhandledException"/> tears the
/// process down.
/// </summary>
internal static class CrashLogger
{
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WindowsSpaces", "crash.log");

    public static void Log(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(LogFilePath,
                $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public static void Log(string context, Exception ex)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(LogFilePath,
                $"[{DateTimeOffset.Now:O}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best effort: the process is already going down over ex; a
            // failure to log must not throw a second exception on top of it.
        }
    }
}
