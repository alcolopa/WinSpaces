using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using WindowsSpaces.Core;

namespace WindowsSpaces.Cli;

internal static class Program
{
    private const string PipeName = "WindowsSpaces_IPC_Pipe";

    private static void Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return;
        }

        bool jsonOutput = args.Contains("--json");
        var cleanArgs = args.Where(arg => arg != "--json").ToArray();

        if (cleanArgs.Length == 0)
        {
            PrintUsage();
            return;
        }

        string command = cleanArgs[0].ToLowerInvariant();
        var arguments = new Dictionary<string, string>();

        switch (command)
        {
            case "status":
                break;

            case "switch":
                if (cleanArgs.Length < 3)
                {
                    PrintError("Error: 'switch' command requires <monitorId> and <workspaceId>.", jsonOutput);
                    return;
                }
                arguments["MonitorId"] = cleanArgs[1];
                arguments["WorkspaceId"] = cleanArgs[2];
                break;

            case "profile":
                if (cleanArgs.Length < 2)
                {
                    PrintError("Error: 'profile' command requires <profileName>.", jsonOutput);
                    return;
                }
                arguments["ProfileName"] = cleanArgs[1];
                break;

            case "move-window":
                if (cleanArgs.Length < 3)
                {
                    PrintError("Error: 'move-window' command requires <hwnd> and <workspaceId>.", jsonOutput);
                    return;
                }
                arguments["Hwnd"] = cleanArgs[1];
                arguments["WorkspaceId"] = cleanArgs[2];
                break;

            case "rules":
                break;

            case "sync":
                break;

            case "restore":
                break;

            default:
                PrintError($"Error: Unknown command '{cleanArgs[0]}'.", jsonOutput);
                PrintUsage();
                return;
        }

        var request = new IpcRequest(command, arguments);
        var response = SendRequest(request);

        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            if (response.Success)
            {
                if (!string.IsNullOrEmpty(response.Data))
                {
                    Console.WriteLine(response.Data);
                }
                else
                {
                    Console.WriteLine("Command completed successfully.");
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Error: {response.Error}");
                Console.ResetColor();
                Environment.Exit(1);
            }
        }
    }

    private static IpcResponse SendRequest(IpcRequest request)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            client.Connect(2000); // 2 seconds timeout

            using var writer = new StreamWriter(client);
            using var reader = new StreamReader(client);

            string jsonRequest = JsonSerializer.Serialize(request);
            writer.WriteLine(jsonRequest);
            writer.Flush();

            string? jsonResponse = reader.ReadLine();
            if (jsonResponse is null)
            {
                return new IpcResponse(false, "Server disconnected without response.", null);
            }

            return JsonSerializer.Deserialize<IpcResponse>(jsonResponse) 
                ?? new IpcResponse(false, "Failed to deserialize server response.", null);
        }
        catch (TimeoutException)
        {
            return new IpcResponse(false, "Connection to Windows Spaces timed out. Is the app running?", null);
        }
        catch (Exception ex)
        {
            return new IpcResponse(false, $"IPC Error: {ex.Message}", null);
        }
    }

    private static void PrintError(string message, bool jsonOutput)
    {
        if (jsonOutput)
        {
            var response = new IpcResponse(false, message, null);
            Console.WriteLine(JsonSerializer.Serialize(response));
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(message);
            Console.ResetColor();
        }
        Environment.Exit(1);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Windows Spaces CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  ws.exe status                       Show active monitors, workspaces, and tracked windows");
        Console.WriteLine("  ws.exe switch <monitorId> <workspaceId>   Switch active workspace for a monitor");
        Console.WriteLine("  ws.exe profile <profileName>        Apply a workspace profile");
        Console.WriteLine("  ws.exe move-window <hwnd> <workspaceId>   Move a window to a different workspace");
        Console.WriteLine("  ws.exe rules                        List active rules");
        Console.WriteLine("  ws.exe sync                         Trigger configuration reload");
        Console.WriteLine("  ws.exe restore                      Emergency show all windows recovery");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json                              Output results in raw JSON format");
        Console.WriteLine("  -h, --help                          Show this help message");
    }
}
