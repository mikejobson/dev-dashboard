using System.Text;

namespace DevDash.Agent.Service;

/// <summary>A file the installer writes to register the service (plist, unit file or task XML).</summary>
public sealed record ServiceFile(string Path, string Content, Encoding Encoding);

public sealed record ServiceState(bool Installed, bool Running, string? Detail = null)
{
    public override string ToString() =>
        !Installed ? "not installed" : (Running ? "installed, running" : "installed, not running") + (Detail is null ? "" : $" ({Detail})");
}

/// <summary>
/// One per operating system. Each describes its service as a definition file plus the commands that
/// register, start, stop and remove it, so `install --dry-run` and the tests can show exactly what
/// would happen without touching the machine.
/// </summary>
public interface IServiceManager
{
    /// <summary>Human-readable name, e.g. "launchd agent com.github.mikejobson.devdash-agent".</summary>
    string Name { get; }

    ServiceFile Definition { get; }

    IReadOnlyList<ServiceCommand> InstallCommands { get; }
    IReadOnlyList<ServiceCommand> UninstallCommands { get; }
    IReadOnlyList<ServiceCommand> StartCommands { get; }
    IReadOnlyList<ServiceCommand> StopCommands { get; }

    Task<ServiceState> GetStateAsync(ICommandRunner runner, CancellationToken ct);

    /// <summary>Anything the user should know after installing, e.g. lingering on Linux.</summary>
    IReadOnlyList<string> Notes { get; }
}

public static class ServiceManagers
{
    public const string RunArguments = "run --service";

    public static IServiceManager For(ServiceEnvironment environment) => environment.Platform switch
    {
        ServicePlatform.MacOS => new LaunchdServiceManager(environment),
        ServicePlatform.Windows => new ScheduledTaskServiceManager(environment),
        _ => new SystemdUserServiceManager(environment),
    };
}
