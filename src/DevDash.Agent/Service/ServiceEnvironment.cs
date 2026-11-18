using System.Runtime.InteropServices;

namespace DevDash.Agent.Service;

public enum ServicePlatform
{
    MacOS,
    Windows,
    Linux,
}

/// <summary>
/// Everything the installer needs to know about the machine and user it is installing for. Tests build
/// one by hand with a temporary home; <see cref="Current"/> reads the real environment.
/// </summary>
public sealed record ServiceEnvironment
{
    public required ServicePlatform Platform { get; init; }
    public required string HomeDirectory { get; init; }

    /// <summary>The binary the service runs (the console build; see <see cref="ServiceExecutablePath"/>).</summary>
    public required string ExecutablePath { get; init; }

    public string UserName { get; init; } = Environment.UserName;

    /// <summary>Numeric uid on macOS and Linux; the user's SID on Windows.</summary>
    public string UserId { get; init; } = "";

    /// <summary><c>DOMAIN\user</c> on Windows, used for the logon trigger.</summary>
    public string? QualifiedUserName { get; init; }

    /// <summary>%LOCALAPPDATA% on Windows.</summary>
    public string? LocalAppData { get; init; }

    public string? XdgConfigHome { get; init; }
    public string? XdgStateHome { get; init; }

    /// <summary>PATH at install time. launchd and systemd start services with a minimal PATH, so it is written into the unit.</summary>
    public string? PathVariable { get; init; }

    private char Sep => Platform == ServicePlatform.Windows ? '\\' : '/';

    /// <summary>Joins with the target platform's separator, so tests can build Windows paths on any OS.</summary>
    public string Join(params string[] parts) => string.Join(Sep, parts.Select((p, i) => i == 0 ? p.TrimEnd('/', '\\') : p.Trim('/', '\\')));

    /// <summary>Same on every OS, and where the agent has always looked: <c>~/.config/devdash</c>.</summary>
    public string ConfigDirectory => Join(HomeDirectory, ".config", "devdash");
    public string ConfigFile => Join(ConfigDirectory, "agent.json");
    public string ApiKeyFile => Join(ConfigDirectory, "agent.key");

    public string LogDirectory => Platform switch
    {
        ServicePlatform.MacOS => Join(HomeDirectory, "Library", "Logs", "DevDash"),
        ServicePlatform.Windows => Join(WindowsDataDirectory, "logs"),
        _ => Join(string.IsNullOrEmpty(XdgStateHome) ? Join(HomeDirectory, ".local", "state") : XdgStateHome, "devdash", "logs"),
    };

    public string WindowsDataDirectory => Join(string.IsNullOrEmpty(LocalAppData) ? Join(HomeDirectory, "AppData", "Local") : LocalAppData, "DevDash");

    /// <summary>
    /// What the service actually starts. On Windows that is <c>devdash-agentw.exe</c> when it sits beside
    /// the console binary: the same program built for the Windows subsystem, so no console window opens.
    /// </summary>
    public string ServiceExecutablePath
    {
        get
        {
            if (Platform != ServicePlatform.Windows)
            {
                return ExecutablePath;
            }

            var windowless = WindowlessExecutablePath;
            return windowless is not null && FileExists(windowless) ? windowless : ExecutablePath;
        }
    }

    public string? WindowlessExecutablePath
    {
        get
        {
            var cut = ExecutablePath.LastIndexOfAny(['\\', '/']);
            return cut < 0 ? null : ExecutablePath[..(cut + 1)] + WindowlessExecutableName;
        }
    }

    public const string WindowlessExecutableName = "devdash-agentw.exe";

    /// <summary>Replaced in tests.</summary>
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    public static ServiceEnvironment Current()
    {
        var platform = OperatingSystem.IsWindows() ? ServicePlatform.Windows
            : OperatingSystem.IsMacOS() ? ServicePlatform.MacOS
            : ServicePlatform.Linux;
        var windows = OperatingSystem.IsWindows();

        return new ServiceEnvironment
        {
            Platform = platform,
            // DoNotVerify: the default returns "" for a home folder that does not exist yet.
            HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
            ExecutablePath = Environment.ProcessPath ?? "devdash-agent",
            UserName = Environment.UserName,
            UserId = windows ? WindowsSid() : Posix.geteuid().ToString(System.Globalization.CultureInfo.InvariantCulture),
            QualifiedUserName = windows ? $"{Environment.UserDomainName}\\{Environment.UserName}" : null,
            LocalAppData = windows ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : null,
            XdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            XdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME"),
            PathVariable = Environment.GetEnvironmentVariable("PATH"),
        };
    }

    private static string WindowsSid() =>
        OperatingSystem.IsWindows() ? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "" : "";

    private static class Posix
    {
        [DllImport("libc")]
        internal static extern uint geteuid();
    }
}
