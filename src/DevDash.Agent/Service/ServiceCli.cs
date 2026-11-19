using System.Text.Json;
using DevDash.Agent.Logging;
using DevDash.Agent.Scanning;

namespace DevDash.Agent.Service;

/// <summary>
/// The commands that manage the agent as a background service: install, uninstall, start, stop,
/// restart and logs. They run before the host is built because they need none of it.
/// </summary>
public sealed class ServiceCli(ServiceEnvironment environment, IServiceManager manager, ICommandRunner runner, TextWriter output, TextWriter error)
{
    public const string Usage = """
        Service commands:
          devdash-agent install --hub-url <url> --api-key <key>   Save the config, then register and start the background service
          devdash-agent uninstall [--purge]                       Remove the service (--purge also deletes config, key and logs)
          devdash-agent start | stop | restart                    Control the installed service
          devdash-agent logs [-n <lines>]                         Show where the logs are and print the latest lines
          devdash-agent --version

        Install options:
          --hub-url <url>       Hub base URL (required the first time)
          --api-key <key>       Agent API key (required the first time; or --api-key-file <f>, or DEVDASH_API_KEY)
          --root <path>         Folder to scan; repeat for several (default ~/Developer)
          --machine-id <id>     Override the machine id (default: host name)
          --no-fetch            Do not run `git fetch --prune --quiet`
          --no-watch            Do not watch for file changes between scans
          --enable-actions      Allow actions requested from the hub (writes Agent:Actions:Enabled)
          --updates <mode>      off | notify | auto (writes Agent:Updates:Mode; default notify: update when
                                asked from the dashboard, never unprompted)
          --set <Key=Value>     Any other Agent setting, e.g. --set ScanIntervalMinutes=30; repeatable
          --dry-run             Print the files and commands install would use; change nothing
          --no-start            Write the files but do not register or start the service
        """;

    private static readonly HashSet<string> Commands = ["install", "uninstall", "start", "stop", "restart", "logs", "version"];

    /// <summary>Runs a service command if <paramref name="args"/> names one; null means "not mine".</summary>
    public static async Task<int?> TryRunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
        {
            return null;
        }

        var command = args[0] is "--version" or "-V" ? "version" : args[0].ToLowerInvariant();
        if (!Commands.Contains(command))
        {
            return null;
        }

        if (command == "version")
        {
            Console.Out.WriteLine($"devdash-agent {MachineInfoProvider.AgentVersion}");
            return 0;
        }

        var environment = ServiceEnvironment.Current();
        var cli = new ServiceCli(environment, ServiceManagers.For(environment), new ProcessCommandRunner(), Console.Out, Console.Error);
        var rest = args[1..];
        if (rest.Contains("--help") || rest.Contains("-h"))
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        try
        {
            return command switch
            {
                "install" => await cli.InstallAsync(InstallRequest.Parse(rest, environmentKey: Environment.GetEnvironmentVariable("DEVDASH_API_KEY")), ct).ConfigureAwait(false),
                "uninstall" => await cli.UninstallAsync(InstallRequest.Parse(rest), ct).ConfigureAwait(false),
                "start" => await cli.ControlAsync(manager => manager.StartCommands, "Started", ct).ConfigureAwait(false),
                "stop" => await cli.ControlAsync(manager => manager.StopCommands, "Stopped", ct).ConfigureAwait(false),
                "restart" => await cli.ControlAsync(manager => [.. manager.StopCommands.Select(c => c with { IgnoreFailure = true }), .. manager.StartCommands], "Restarted", ct).ConfigureAwait(false),
                _ => cli.Logs(rest),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>One line for `devdash-agent status`.</summary>
    public static async Task<string> DescribeStateAsync(CancellationToken ct)
    {
        var environment = ServiceEnvironment.Current();
        var manager = ServiceManagers.For(environment);
        var state = await manager.GetStateAsync(new ProcessCommandRunner(), ct).ConfigureAwait(false);
        return state.Installed ? $"{state}; logs in {environment.LogDirectory}" : $"{state} (run `devdash-agent install`)";
    }

    /// <summary>
    /// Removes <c>--service</c> and <c>--log-dir &lt;path&gt;</c> from the arguments and returns the folder to
    /// log to, or null to log to the console only. <c>--service</c> is what the installed service passes.
    /// </summary>
    public static string? TakeLogDirectory(ref string[] args)
    {
        string? directory = null;
        var rest = new List<string>(args.Length);
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--service")
            {
                directory ??= ServiceEnvironment.Current().LogDirectory;
            }
            else if (args[index] == "--log-dir" && index + 1 < args.Length)
            {
                directory = Path.GetFullPath(AgentOptions.ExpandHome(args[++index]));
            }
            else
            {
                rest.Add(args[index]);
            }
        }

        args = [.. rest];
        return directory;
    }

    public async Task<int> InstallAsync(InstallRequest request, CancellationToken ct)
    {
        if (request.Error is not null)
        {
            error.WriteLine(request.Error);
            error.WriteLine("See `devdash-agent install --help`.");
            return 64;
        }

        if (string.IsNullOrEmpty(environment.HomeDirectory))
        {
            error.WriteLine("Could not work out your home folder (is HOME set?).");
            return 1;
        }

        var launcher = Path.GetFileNameWithoutExtension(environment.ExecutablePath);
        if (launcher.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("install registers the running executable as the service, so it needs the published devdash-agent binary rather than `dotnet devdash-agent.dll`.");
            return 1;
        }

        // The service starts in the home folder, so a relative --root must be pinned down now.
        request = request with { Roots = [.. request.Roots.Select(r => r.StartsWith('~') ? r : Path.GetFullPath(r))] };

        var existing = File.Exists(environment.ConfigFile) ? File.ReadAllText(environment.ConfigFile) : null;
        var config = AgentConfigWriter.Merge(existing, request, environment.ApiKeyFile);
        var (hubUrl, hasKey) = AgentConfigWriter.Summarise(config);
        if (string.IsNullOrWhiteSpace(hubUrl))
        {
            error.WriteLine("No hub URL yet: pass --hub-url https://devdash.<your-tailnet>.ts.net");
            return 64;
        }

        if (!hasKey)
        {
            error.WriteLine("No API key yet: pass --api-key <key> (issue one on the hub's Machines page).");
            return 64;
        }

        var definition = manager.Definition;
        if (request.DryRun)
        {
            output.WriteLine("Dry run: nothing is written and no command is run.");
            output.WriteLine();
            output.WriteLine($"# {environment.ConfigFile}");
            output.WriteLine(config);
            if (request.ApiKey is not null)
            {
                output.WriteLine($"# {environment.ApiKeyFile}  (readable by {environment.UserName} only)");
                output.WriteLine(Redact(request.ApiKey));
                output.WriteLine();
            }

            output.WriteLine($"# {definition.Path}");
            output.WriteLine(definition.Content);
            output.WriteLine($"# commands ({manager.Name})");
            foreach (var command in request.NoStart ? [] : manager.InstallCommands)
            {
                output.WriteLine(command);
            }

            output.WriteLine();
            output.WriteLine($"# logs: {environment.LogDirectory}");
            return 0;
        }

        if (request.ApiKey is not null)
        {
            AgentConfigWriter.WriteApiKey(environment.ApiKeyFile, request.ApiKey);
            output.WriteLine($"Saved the API key to {environment.ApiKeyFile} (readable by {environment.UserName} only).");
        }

        AgentConfigWriter.WriteConfig(environment.ConfigFile, config);
        output.WriteLine($"Wrote {environment.ConfigFile} (hub {hubUrl}).");

        Directory.CreateDirectory(environment.LogDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(definition.Path)!);
        File.WriteAllText(definition.Path, definition.Content, definition.Encoding);
        output.WriteLine($"Wrote {definition.Path}.");

        if (request.NoStart)
        {
            output.WriteLine("--no-start: the service was not registered or started.");
            return 0;
        }

        if (!await RunAsync(manager.InstallCommands, ct).ConfigureAwait(false))
        {
            error.WriteLine($"Could not register the {manager.Name}. The config was saved; fix the problem above and run install again.");
            return 1;
        }

        // Give the service a moment to come up (or fall over) before reporting.
        var state = await manager.GetStateAsync(runner, ct).ConfigureAwait(false);
        for (var attempt = 0; attempt < 5 && !state.Running; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            state = await manager.GetStateAsync(runner, ct).ConfigureAwait(false);
        }

        output.WriteLine($"Installed the {manager.Name}: {state}.");
        output.WriteLine($"Logs: {environment.LogDirectory}  (devdash-agent logs)");
        foreach (var note in manager.Notes)
        {
            output.WriteLine($"Note: {note}");
        }

        return state.Running ? 0 : 1;
    }

    public async Task<int> UninstallAsync(InstallRequest request, CancellationToken ct)
    {
        if (request.Error is not null)
        {
            error.WriteLine(request.Error);
            return 64;
        }

        var definition = manager.Definition;
        string[] purged = [environment.ConfigFile, environment.ApiKeyFile];
        if (request.DryRun)
        {
            output.WriteLine("Dry run: nothing is removed and no command is run.");
            foreach (var command in manager.UninstallCommands)
            {
                output.WriteLine(command);
            }

            output.WriteLine($"delete {definition.Path}");
            if (request.Purge)
            {
                output.WriteLine($"delete {string.Join(", ", purged)}, {environment.LogDirectory}");
            }

            return 0;
        }

        await RunAsync(manager.UninstallCommands, ct).ConfigureAwait(false);
        File.Delete(definition.Path);
        output.WriteLine($"Removed the {manager.Name}.");

        if (request.Purge)
        {
            foreach (var file in purged)
            {
                File.Delete(file);
            }

            if (Directory.Exists(environment.ConfigDirectory) && !Directory.EnumerateFileSystemEntries(environment.ConfigDirectory).Any())
            {
                Directory.Delete(environment.ConfigDirectory);
            }

            if (Directory.Exists(environment.LogDirectory))
            {
                Directory.Delete(environment.LogDirectory, recursive: true);
            }

            output.WriteLine("Deleted the config, API key and logs.");
        }
        else
        {
            output.WriteLine($"Kept the config in {environment.ConfigDirectory} and logs in {environment.LogDirectory}; `uninstall --purge` deletes them.");
        }

        output.WriteLine($"The program itself is still at {environment.ExecutablePath}; delete it to finish removing DevDash.");
        return 0;
    }

    public async Task<int> ControlAsync(Func<IServiceManager, IReadOnlyList<ServiceCommand>> commands, string done, CancellationToken ct)
    {
        if (!File.Exists(manager.Definition.Path))
        {
            error.WriteLine("The service is not installed. Run `devdash-agent install` first.");
            return 1;
        }

        if (!await RunAsync(commands(manager), ct).ConfigureAwait(false))
        {
            return 1;
        }

        output.WriteLine($"{done} the {manager.Name}.");
        return 0;
    }

    public int Logs(IReadOnlyList<string> args)
    {
        var lines = 40;
        if (args.Count == 2 && args[0] == "-n" && int.TryParse(args[1], out var requested) && requested >= 0)
        {
            lines = requested;
        }
        else if (args.Count > 0)
        {
            error.WriteLine("Usage: devdash-agent logs [-n <lines>]");
            return 64;
        }

        var latest = FileLoggerProvider.LatestFile(environment.LogDirectory);
        if (latest is null)
        {
            error.WriteLine($"No logs yet in {environment.LogDirectory}. The agent writes them when it runs as a service.");
            return 1;
        }

        error.WriteLine($"{latest}:");
        using var stream = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var tail = new Queue<string>(lines + 1);
        while (reader.ReadLine() is { } line)
        {
            tail.Enqueue(line);
            if (tail.Count > lines)
            {
                tail.Dequeue();
            }
        }

        foreach (var line in tail)
        {
            output.WriteLine(line);
        }

        return 0;
    }

    private async Task<bool> RunAsync(IReadOnlyList<ServiceCommand> commands, CancellationToken ct)
    {
        foreach (var command in commands)
        {
            var result = await runner.RunAsync(command, ct).ConfigureAwait(false);
            for (var attempt = 1; attempt < command.Attempts && !result.Ok; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                result = await runner.RunAsync(command, ct).ConfigureAwait(false);
            }

            if (!result.Ok && !command.IgnoreFailure)
            {
                error.WriteLine($"`{command}` failed with exit code {result.ExitCode}: {result.Output}");
                return false;
            }
        }

        return true;
    }

    private static string Redact(string key) => key.Length <= 8 ? "********" : key[..4] + new string('*', 8) + $" ({key.Length} characters)";
}
