namespace WindowsSpaces.Core;

public interface IProcessManager
{
    void Launch(string processPath, string? arguments);
    string? GetCommandLine(int processId);
}
