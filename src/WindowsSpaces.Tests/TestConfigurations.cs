using System.Linq;
using WindowsSpaces.Core;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests;

internal static class TestConfigurations
{
    /// <summary>
    /// A default configuration with a second space added to every monitor.
    ///
    /// <see cref="AppConfiguration.CreateDefault"/> gives one space per
    /// monitor — the state a fresh install starts in — so anything about
    /// switching between spaces, moving windows between them, or editing a
    /// monitor that has several has to set that up for itself.
    /// </summary>
    public static AppConfiguration WithTwoSpaces(params Monitor[] monitors)
    {
        var config = AppConfiguration.CreateDefault(monitors);

        return config with
        {
            Monitors = config.Monitors
                .Select(m => new MonitorWorkspaceConfig(
                    m.MonitorId,
                    m.Workspaces
                        .Append(new WorkspaceDefinition($"{m.MonitorId}:2", "Space 2", 2))
                        .ToList()))
                .ToList()
        };
    }
}
