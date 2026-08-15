using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using WindowsSpaces.Core;

namespace WindowsSpaces.Platform;

public sealed class ProcessManager : IProcessManager
{
    public void Launch(string processPath, string? arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true
            };
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to launch process '{processPath}': {ex.Message}");
        }
    }

    public string? GetCommandLine(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            using var collection = searcher.Get();
            var obj = collection.Cast<ManagementBaseObject>().FirstOrDefault();
            return obj?["CommandLine"]?.ToString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to get command line for PID {processId}: {ex.Message}");
            return null;
        }
    }
}
