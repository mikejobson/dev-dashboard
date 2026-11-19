using System.Text;

namespace DevDash.Agent.Service;

/// <summary>Linux: a <c>systemd --user</c> unit, so the agent runs as the user without root.</summary>
public sealed class SystemdUserServiceManager(ServiceEnvironment environment) : IServiceManager
{
    public const string Unit = "devdash-agent.service";

    public string Name => $"systemd user unit {Unit}";

    public string UnitPath => environment.Join(
        string.IsNullOrEmpty(environment.XdgConfigHome) ? environment.Join(environment.HomeDirectory, ".config") : environment.XdgConfigHome,
        "systemd", "user", Unit);

    public ServiceFile Definition => new(UnitPath, $"""
        [Unit]
        Description=DevDash agent: reports the state of your code folders to the DevDash hub

        [Service]
        Type=simple
        ExecStart={Quote(environment.ServiceExecutablePath)} {ServiceManagers.RunArguments}
        WorkingDirectory=%h
        Environment={Quote("PATH=" + ServicePath.For(environment))}
        Restart=always
        RestartSec=30

        [Install]
        WantedBy=default.target

        """.ReplaceLineEndings("\n"), new UTF8Encoding(false));

    public IReadOnlyList<ServiceCommand> InstallCommands =>
    [
        Systemctl("daemon-reload"),
        Systemctl("enable", Unit),
        Systemctl("restart", Unit),
    ];

    public IReadOnlyList<ServiceCommand> UninstallCommands =>
    [
        Systemctl("disable", "--now", Unit) with { IgnoreFailure = true },
    ];

    public IReadOnlyList<ServiceCommand> StartCommands => [Systemctl("start", Unit)];
    public IReadOnlyList<ServiceCommand> StopCommands => [Systemctl("stop", Unit)];
    public IReadOnlyList<ServiceCommand> RestartCommands => [Systemctl("restart", Unit)];

    public IReadOnlyList<string> Notes =>
    [
        $"The agent runs while you are logged in. To keep it running after logout: sudo loginctl enable-linger {environment.UserName}",
        "systemd user services do not inherit SSH_AUTH_SOCK; for SSH remotes, use a key without a passphrase or set IdentityAgent in ~/.ssh/config.",
    ];

    public async Task<ServiceState> GetStateAsync(ICommandRunner runner, CancellationToken ct)
    {
        var installed = environment.FileExists(UnitPath);
        var active = await runner.RunAsync(Systemctl("is-active", Unit), ct).ConfigureAwait(false);
        var state = active.Output.Trim();
        return new ServiceState(installed, installed && state == "active", installed && state.Length > 0 && state != "active" ? state : null);
    }

    private static ServiceCommand Systemctl(params string[] args) => new("systemctl", ["--user", .. args]);

    /// <summary>systemd splits on whitespace; quote and escape so paths with spaces survive.</summary>
    private static string Quote(string value) => QuoteRaw(value.Replace("%", "%%", StringComparison.Ordinal)); // % starts a specifier

    private static string QuoteRaw(string value) =>
        value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\' or '\'')
            ? "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
}
