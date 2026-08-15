using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using WindowsSpaces.Core;

namespace WindowsSpaces.App;

public sealed class IpcServer : IDisposable
{
    private const string PipeName = "WindowsSpaces_IPC_Pipe";
    private readonly AppHost _appHost;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public IpcServer(AppHost appHost, DispatcherQueue dispatcherQueue)
    {
        _appHost = appHost;
        _dispatcherQueue = dispatcherQueue;
    }

    public void Start()
    {
        _listenTask = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                using var pipeServer = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipeServer.WaitForConnectionAsync(_cts.Token);

                using var reader = new StreamReader(pipeServer);
                using var writer = new StreamWriter(pipeServer);

                string? line = await reader.ReadLineAsync();
                if (line is not null)
                {
                    var request = JsonSerializer.Deserialize<IpcRequest>(line);
                    if (request is not null)
                    {
                        var response = await ProcessRequestAsync(request);
                        string responseJson = JsonSerializer.Serialize(response);
                        await writer.WriteLineAsync(responseJson);
                        await writer.FlushAsync();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                await Task.Delay(100, _cts.Token);
            }
        }
    }

    private async Task<IpcResponse> ProcessRequestAsync(IpcRequest request)
    {
        var tcs = new TaskCompletionSource<IpcResponse>();

        bool enqueued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var response = ExecuteCommand(request);
                tcs.SetResult(response);
            }
            catch (Exception ex)
            {
                tcs.SetResult(new IpcResponse(false, $"Error executing command: {ex.Message}", null));
            }
        });

        if (!enqueued)
        {
            return new IpcResponse(false, "Failed to dispatch request to UI thread.", null);
        }

        return await tcs.Task;
    }

    private IpcResponse ExecuteCommand(IpcRequest request)
    {
        switch (request.Command.ToLowerInvariant())
        {
            case "status":
                var snapshot = _appHost.GetDiagnosticsSnapshot();
                var statusData = new
                {
                    ActiveProfile = _appHost.Config?.ActiveProfileName,
                    Monitors = snapshot.Monitors.Select(m => new
                    {
                        m.MonitorId,
                        m.ActiveWorkspaceId,
                        Workspaces = _appHost.WorkspaceManager.GetWorkspaceNames(m.MonitorId)
                    }).ToList(),
                    Windows = snapshot.Windows.Select(w => new
                    {
                        w.Hwnd,
                        w.ProcessId,
                        w.MonitorId,
                        w.WorkspaceId,
                        w.IsVisible,
                        w.IsMinimized,
                        w.IsMaximized
                    }).ToList()
                };
                string data = JsonSerializer.Serialize(statusData, new JsonSerializerOptions { WriteIndented = true });
                return new IpcResponse(true, null, data);

            case "switch":
                if (request.Arguments is null ||
                    !request.Arguments.TryGetValue("MonitorId", out var monitorId) ||
                    !request.Arguments.TryGetValue("WorkspaceId", out var workspaceId))
                {
                    return new IpcResponse(false, "Missing MonitorId or WorkspaceId.", null);
                }
                _appHost.WorkspaceManager.SwitchWorkspace(monitorId, workspaceId);
                return new IpcResponse(true, null, $"Switched monitor {monitorId} to workspace {workspaceId}.");

            case "profile":
                if (request.Arguments is null ||
                    !request.Arguments.TryGetValue("ProfileName", out var profileName))
                {
                    return new IpcResponse(false, "Missing ProfileName.", null);
                }
                var profile = _appHost.Config.ActiveProfiles.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
                if (profile is null)
                {
                    return new IpcResponse(false, $"Profile '{profileName}' not found.", null);
                }
                _appHost.WorkspaceManager.ApplyProfile(profile);

                var updatedConfig = _appHost.Config with { ActiveProfileName = profile.Name };
                _appHost.ApplyConfiguration(updatedConfig, out _);

                return new IpcResponse(true, null, $"Applied profile '{profile.Name}'.");

            case "move-window":
                if (request.Arguments is null ||
                    !request.Arguments.TryGetValue("Hwnd", out var hwndStr) ||
                    !request.Arguments.TryGetValue("WorkspaceId", out var targetWorkspaceId))
                {
                    return new IpcResponse(false, "Missing Hwnd or WorkspaceId.", null);
                }
                if (!nint.TryParse(hwndStr, out var hwnd))
                {
                    return new IpcResponse(false, $"Invalid HWND '{hwndStr}'.", null);
                }
                _appHost.WorkspaceManager.AssignWindow(hwnd, targetWorkspaceId);
                return new IpcResponse(true, null, $"Moved window {hwnd} to workspace {targetWorkspaceId}.");

            case "rules":
                var rulesData = _appHost.Config.ActiveRules.Select(r => new
                {
                    r.Id,
                    r.RuleName,
                    r.ProcessPath,
                    r.WindowClass,
                    r.WindowTitle,
                    r.TargetMonitorId,
                    r.TargetWorkspaceIndex
                }).ToList();
                return new IpcResponse(true, null, JsonSerializer.Serialize(rulesData, new JsonSerializerOptions { WriteIndented = true }));

            case "sync":
                if (_appHost.ReloadConfiguration(out var syncError))
                {
                    return new IpcResponse(true, null, "Configuration reloaded successfully.");
                }
                else
                {
                    return new IpcResponse(false, $"Failed to reload configuration: {syncError}", null);
                }

            case "restore":
                _appHost.ShowAllWindows();
                return new IpcResponse(true, null, "All windows recovered to visible states.");

            default:
                return new IpcResponse(false, $"Unknown command '{request.Command}'.", null);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listenTask?.Wait(1000);
        }
        catch
        {
            // Ignore wait/cancellation exceptions
        }
        _cts.Dispose();
    }
}
