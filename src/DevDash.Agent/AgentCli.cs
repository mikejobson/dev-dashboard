namespace DevDash.Agent;

/// <summary>Minimal command-line parsing: a command plus a few flags that override configuration.</summary>
public sealed record AgentCli(string Command, bool DryRun, bool Sizes, Dictionary<string, string?> Overrides, string? Error)
{
    public const string Usage = """
        devdash-agent - reports the state of your code folders to the DevDash hub

        Usage:
          devdash-agent run                 Scan on a schedule and push to the hub (default)
          devdash-agent scan                Scan once and push to the hub
          devdash-agent scan --dry-run      Scan once and print the snapshot JSON; no fetch, no push
          devdash-agent status              Show configuration, hub reachability and the service state
          devdash-agent install|uninstall   Run as a background service; see `devdash-agent install --help`

        Options:
          --root <path>       Folder to scan; repeat for several (default ~/Developer)
          --hub-url <url>     Hub base URL (default http://localhost:5210)
          --api-key <key>     Agent API key
          --api-key-file <f>  File containing the API key
          --machine-id <id>   Override the machine id (default: host name)
          --no-fetch          Do not run `git fetch --prune --quiet`
          --sizes             Measure folder sizes now instead of when they are due
          --no-watch          Do not watch for file changes between scans
          --enable-actions    Let the dashboard queue allowlisted git fixes for this machine (off by default)

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

        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            command = args[0].ToLowerInvariant();
            index = 1;
            if (command is not ("run" or "scan" or "status" or "install" or "uninstall" or "help"))
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
                case "--enable-actions":
                    overrides["Agent:Actions:Enabled"] = "true";
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

        return new AgentCli(command, dryRun, sizes, overrides, null);

        static AgentCli Fail(string message) => new("help", false, false, [], message);
    }
}
