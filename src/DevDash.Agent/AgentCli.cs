namespace DevDash.Agent;

/// <summary>Minimal command-line parsing: a command plus a few flags that override configuration.</summary>
public sealed record AgentCli(string Command, bool DryRun, bool Sizes, Dictionary<string, string?> Overrides, string? Error)
{
    /// <summary><c>update --version</c>: the release to install. Null means the latest.</summary>
    public string? UpdateVersion { get; init; }

    /// <summary><c>update --check</c>: say whether there is a newer release; change nothing.</summary>
    public bool Check { get; init; }

    /// <summary><c>update --no-restart</c>: replace the program but leave a running service alone.</summary>
    public bool NoRestart { get; init; }

    public const string Usage = """
        devdash-agent - reports the state of your code folders to the DevDash hub

        Usage:
          devdash-agent run                 Scan on a schedule and push to the hub (default)
          devdash-agent scan                Scan once and push to the hub
          devdash-agent scan --dry-run      Scan once and print the snapshot JSON; no fetch, no push
          devdash-agent status              Show configuration, hub reachability and the service state
          devdash-agent update              Install the latest release (signature and checksum verified), then restart the service
          devdash-agent update --check      Only say whether a newer release exists
          devdash-agent update --version X  Install release X (never an older one); --no-restart leaves the service running
          devdash-agent install|uninstall   Run as a background service; see `devdash-agent install --help`

        Options:
          --root <path>       Folder to scan; repeat for several (default ~/Developer, and ~/.t3/projects when it exists)
          --hub-url <url>     Hub base URL (default http://localhost:5210)
          --api-key <key>     Agent API key
          --api-key-file <f>  File containing the API key
          --machine-id <id>   Override the machine id (default: host name)
          --no-fetch          Do not run `git fetch --prune --quiet`
          --sizes             Measure folder sizes now instead of when they are due
          --no-watch          Do not watch for file changes between scans
          --no-deps           Do not run `dotnet list package` / `npm audit` in the background (on by default, once a day per repo)
          --no-t3             Do not look for T3 Code: no threads in the snapshot, no T3 actions, ~/.t3/projects not scanned
          --no-disk           Do not measure developer caches (NuGet, npm, Docker, Xcode…) in the background (on by default, every 6 h)
          --no-dev-servers    Do not list listening dev servers with each scan
          --enable-actions    Let the dashboard queue allowlisted git fixes for this machine (off by default)
          --updates <mode>    Self-update: off, notify (default: when asked from the dashboard) or auto

        Configuration can also come from ~/.config/devdash/agent.json or DEVDASH_Agent__* environment variables.
        """;

    public static AgentCli Parse(string[] args)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var command = "run";
        var dryRun = false;
        var sizes = false;
        var roots = 0;
        var index = 0;
        string? updateVersion = null;
        bool check = false, noRestart = false;

        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            command = args[0].ToLowerInvariant();
            index = 1;
            if (command is not ("run" or "scan" or "status" or "update" or "install" or "uninstall" or "help"))
            {
                return Fail($"Unknown command '{args[0]}'.");
            }
        }

        for (; index < args.Length; index++)
        {
            var arg = args[index];
            string? Value() => index + 1 < args.Length ? args[++index] : null;

            switch (arg)
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--sizes":
                    sizes = true;
                    break;
                case "--no-fetch":
                    overrides["Agent:AutoFetch:Enabled"] = "false";
                    break;
                case "--no-watch":
                    overrides["Agent:Watch"] = "false";
                    break;
                case "--no-deps":
                    overrides["Agent:Dependencies:Enabled"] = "false";
                    break;
                case "--no-t3":
                    overrides["Agent:T3:Enabled"] = "false";
                    break;
                case "--no-disk":
                    overrides["Agent:Disk:Enabled"] = "false";
                    break;
                case "--no-dev-servers":
                    overrides["Agent:DevServers:Enabled"] = "false";
                    break;
                case "--enable-actions":
                    overrides["Agent:Actions:Enabled"] = "true";
                    break;
                case "--check" when command == "update":
                    check = true;
                    break;
                case "--no-restart" when command == "update":
                    noRestart = true;
                    break;
                case "--version" when command == "update":
                    if (Value() is not { } wanted)
                    {
                        return Fail("--version needs a value.");
                    }

                    // People write v0.2.0 as often as 0.2.0; everything past this point is a strict version.
                    updateVersion = wanted.StartsWith('v') ? wanted[1..] : wanted;
                    if (!DevDash.Contracts.SemVer.IsValid(updateVersion))
                    {
                        return Fail($"--version must be MAJOR.MINOR.PATCH[-prerelease], got '{wanted}'.");
                    }

                    break;
                case "--updates":
                    if (DevDash.Contracts.AgentUpdateModes.Normalize(Value()) is not { } mode)
                    {
                        return Fail("--updates must be off, notify or auto.");
                    }

                    overrides["Agent:Updates:Mode"] = mode;
                    break;
                case "-h" or "--help":
                    command = "help";
                    break;
                case "--root" or "--hub-url" or "--api-key" or "--api-key-file" or "--machine-id":
                    if (Value() is not { } value)
                    {
                        return Fail($"{arg} needs a value.");
                    }

                    var key = arg switch
                    {
                        "--root" => $"Agent:Roots:{roots++}",
                        "--hub-url" => "Agent:HubUrl",
                        "--api-key" => "Agent:ApiKey",
                        "--api-key-file" => "Agent:ApiKeyFile",
                        _ => "Agent:MachineId",
                    };
                    overrides[key] = value;
                    break;
                default:
                    return Fail($"Unknown option '{arg}'.");
            }
        }

        return new AgentCli(command, dryRun, sizes, overrides, null) { UpdateVersion = updateVersion, Check = check, NoRestart = noRestart };

        static AgentCli Fail(string message) => new("help", false, false, [], message);
    }
}
