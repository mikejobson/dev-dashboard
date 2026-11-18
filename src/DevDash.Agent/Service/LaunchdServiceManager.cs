using System.Text;
using System.Xml.Linq;

namespace DevDash.Agent.Service;

/// <summary>
/// macOS: a per-user LaunchAgent in the GUI domain, so the agent runs as the logged-in user with their
/// home folder, git config, keychain and ssh-agent, and starts at login.
/// </summary>
public sealed class LaunchdServiceManager(ServiceEnvironment environment) : IServiceManager
{
    public const string Label = "com.github.mikejobson.devdash-agent";

    private string Domain => $"gui/{environment.UserId}";
    private string Target => $"{Domain}/{Label}";

    public string Name => $"launchd agent {Label}";

    public string PlistPath => environment.Join(environment.HomeDirectory, "Library", "LaunchAgents", Label + ".plist");

    public ServiceFile Definition
    {
        get
        {
            var dict = new XElement("dict",
                Key("Label"), Str(Label),
                Key("ProgramArguments"), new XElement("array", new[] { environment.ServiceExecutablePath }.Concat(ServiceManagers.RunArguments.Split(' ')).Select(Str)),
                Key("RunAtLoad"), new XElement("true"),
                Key("KeepAlive"), new XElement("true"),
                // Wait between restarts if the agent keeps exiting, rather than spinning.
                Key("ThrottleInterval"), new XElement("integer", 30),
                Key("ProcessType"), Str("Background"),
                Key("WorkingDirectory"), Str(environment.HomeDirectory),
                // The agent writes its own rolling log; these only catch what happens before logging starts.
                Key("StandardOutPath"), Str(environment.Join(environment.LogDirectory, "launchd.out.log")),
                Key("StandardErrorPath"), Str(environment.Join(environment.LogDirectory, "launchd.err.log")),
                Key("EnvironmentVariables"), new XElement("dict", Key("PATH"), Str(ServicePath.For(environment))));

            var xml = new StringBuilder()
                .AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""")
                .AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""")
                .AppendLine(new XElement("plist", new XAttribute("version", "1.0"), dict).ToString())
                .ToString();
            return new ServiceFile(PlistPath, xml.ReplaceLineEndings("\n"), new UTF8Encoding(false));

            static XElement Key(string name) => new("key", name);
            static XElement Str(string value) => new("string", value);
        }
    }

    public IReadOnlyList<ServiceCommand> InstallCommands =>
    [
        // Replace a previous install; bootout fails harmlessly when nothing is loaded.
        new("launchctl", ["bootout", Target], IgnoreFailure: true),
        // Clears a `launchctl disable` left over from an earlier manual stop.
        new("launchctl", ["enable", Target], IgnoreFailure: true),
        // bootout returns before launchd has finished unloading, so the first bootstrap can fail with EIO.
        new("launchctl", ["bootstrap", Domain, PlistPath], Attempts: 5),
    ];

    public IReadOnlyList<ServiceCommand> UninstallCommands => [new("launchctl", ["bootout", Target], IgnoreFailure: true)];

    public IReadOnlyList<ServiceCommand> StartCommands =>
    [
        new("launchctl", ["bootstrap", Domain, PlistPath], IgnoreFailure: true), // already loaded is fine
        new("launchctl", ["kickstart", Target]),
    ];

    // With KeepAlive, `launchctl kill` would just be restarted; unloading is the only real stop.
    public IReadOnlyList<ServiceCommand> StopCommands => [new("launchctl", ["bootout", Target])];

    public IReadOnlyList<string> Notes =>
    [
        "macOS asks for permission the first time the agent reads Documents, Desktop or Downloads; scan roots elsewhere (such as ~/Developer) need none.",
    ];

    public async Task<ServiceState> GetStateAsync(ICommandRunner runner, CancellationToken ct)
    {
        var installed = environment.FileExists(PlistPath);
        var print = await runner.RunAsync(new ServiceCommand("launchctl", ["print", Target]), ct).ConfigureAwait(false);
        if (!print.Ok)
        {
            return new ServiceState(installed, false, installed ? "not loaded" : null);
        }

        var (running, pid) = ParsePrint(print.Output);
        return new ServiceState(true, running, pid is null ? null : $"pid {pid}");
    }

    /// <summary>Reads <c>state = running</c> and <c>pid = 123</c> from <c>launchctl print</c>.</summary>
    public static (bool Running, string? Pid) ParsePrint(string output)
    {
        var running = false;
        string? pid = null;
        foreach (var raw in output.Split('\n'))
        {
            // Only top-level keys: nested blocks repeat "state" for sockets and endpoints.
            if (!raw.StartsWith('\t') || raw.StartsWith("\t\t", StringComparison.Ordinal))
            {
                continue;
            }

            var line = raw.Trim();
            if (line.StartsWith("state = ", StringComparison.Ordinal))
            {
                running = line["state = ".Length..] == "running";
            }
            else if (line.StartsWith("pid = ", StringComparison.Ordinal))
            {
                pid = line["pid = ".Length..];
            }
        }

        return (running, pid);
    }
}

internal static class ServicePath
{
    private static readonly string[] Standard = ["/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"];

    /// <summary>
    /// The PATH the service runs with: the installing shell's PATH, plus the usual places git, gh and
    /// tailscale live, so credential helpers resolve the same way they do in a terminal.
    /// </summary>
    public static string For(ServiceEnvironment environment)
    {
        var parts = (environment.PathVariable ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.StartsWith('/'));
        string[] fallback = environment.Platform == ServicePlatform.MacOS ? ["/opt/homebrew/bin", .. Standard] : Standard;
        return string.Join(':', parts.Concat(fallback).Distinct(StringComparer.Ordinal));
    }
}
