using System.Text;
using DevDash.Agent.Service;
using DevDash.Contracts;

namespace DevDash.Agent.Updates;

/// <summary><c>devdash-agent update</c>: the same updater the dashboard and auto mode use, run by hand.</summary>
public static class UpdateCli
{
    public static async Task<int> RunAsync(AgentCli cli, AgentUpdater updater, UpdateContext context, TextWriter output, TextWriter error, CancellationToken ct)
    {
        // Someone at the keyboard asked for this, so it goes ahead whatever Agent:Updates:Mode says:
        // the mode governs what the dashboard may ask for and what the agent does unprompted.
        var result = await updater.RunAsync(new UpdateRequest(cli.UpdateVersion, cli.Check, ReplacePending: true), ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case UpdateStatus.UpToDate:
                output.WriteLine(result.Message);
                return 0;

            case UpdateStatus.Available:
                output.WriteLine(result.Message);
                output.WriteLine("Run `devdash-agent update` to install it.");
                return 0;

            case UpdateStatus.Installed:
                output.WriteLine(result.Message);
                if (cli.NoRestart)
                {
                    output.WriteLine("--no-restart: a running agent keeps the old version until it is restarted (`devdash-agent restart`).");
                }
                else if (context.Installation is { } installation)
                {
                    output.WriteLine(await RestartServiceAsync(installation, ServiceEnvironment.Current(), new ProcessCommandRunner(), ct).ConfigureAwait(false));
                }

                return 0;

            default:
                error.WriteLine(result.Status == UpdateStatus.Refused ? $"Not updating: {result.Message}" : $"Update failed: {result.Message}");
                return 1;
        }
    }

    /// <summary>
    /// Restarts the installed service, but only when it is the service for the program that was just
    /// updated: updating a second copy of the agent somewhere else must not bounce the real one.
    /// </summary>
    public static async Task<string> RestartServiceAsync(UpdateInstallation installation, ServiceEnvironment environment, ICommandRunner runner, CancellationToken ct)
    {
        var manager = ServiceManagers.For(environment);
        var definition = manager.Definition.Path;
        string text;
        try
        {
            if (!File.Exists(definition))
            {
                return "No background service is installed, so nothing was restarted.";
            }

            text = File.ReadAllText(definition); // the Windows task file is UTF-16 with a byte-order mark, which this detects
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not read {definition} ({ex.Message}); restart the service with `devdash-agent restart`.";
        }

        var comparison = installation.Platform == ServicePlatform.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string[] programs = [.. installation.FileNames.Select(installation.PathOf), environment.ExecutablePath, environment.ServiceExecutablePath];
        if (!programs.Any(p => text.Contains(p, comparison)))
        {
            return $"The installed {manager.Name} runs a different copy of the agent, so it was not restarted.";
        }

        foreach (var command in manager.RestartCommands)
        {
            var result = await runner.RunAsync(command, ct).ConfigureAwait(false);
            if (!result.Ok && !command.IgnoreFailure)
            {
                return $"Updated, but `{command}` failed ({result.Output}). Restart the service with `devdash-agent restart`.";
            }
        }

        return $"Restarted the {manager.Name}.";
    }

    public static string DescribeMode(UpdateOptions options, UpdateContext context)
    {
        var mode = options.ResolvedMode switch
        {
            AgentUpdateModes.Auto => $"auto (installs new releases itself, checking about every {options.CheckIntervalHours} h)",
            AgentUpdateModes.Notify => "notify (updates when asked from the dashboard or by `devdash-agent update`)",
            _ => AgentUpdateModes.Normalize(options.Mode) is null ? $"off ('{options.Mode}' is not off, notify or auto)" : "off",
        };
        return context.Installation is null ? $"{mode}; not available here: {context.Problem}" : mode;
    }

    /// <summary>One line on a pending or finished update, or null when this installation has never updated itself.</summary>
    public static string? DescribeState(UpdateContext context)
    {
        if (context.Installation is not { } installation)
        {
            return null;
        }

        var state = new UpdateStateStore(installation.StateFile).Read();
        if (state.Pending is { } pending)
        {
            return $"{pending.FromVersion} -> {pending.ToVersion}, installed {pending.InstalledAt.ToLocalTime():yyyy-MM-dd HH:mm}, waiting to be confirmed by the hub (started {pending.Starts} time(s))";
        }

        return state.Last is { } last
            ? $"{last.Outcome}: {last.FromVersion} -> {last.ToVersion} at {last.At.ToLocalTime():yyyy-MM-dd HH:mm}{(last.Detail is null ? "" : $" ({last.Detail})")}"
            : null;
    }
}

/// <summary>
/// <c>devdash-agent verify-release &lt;folder&gt;</c>: checks a folder of release assets exactly the way an
/// agent would before installing from it, using the key this binary was built with. The release
/// workflow runs it on what it is about to publish, with a binary from that same build: if the
/// signing secret and the public key in the source ever disagree, the release fails instead of
/// shipping agents that can never update.
/// </summary>
public static class ReleaseCheck
{
    public const string Command = "verify-release";

    public static int Run(string[] args, ReleaseTrust trust, TextWriter output, TextWriter error)
    {
        var expectTestKey = args.Contains("--allow-test-key");
        var folder = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (folder is null || !Directory.Exists(folder))
        {
            error.WriteLine("Usage: devdash-agent verify-release <folder with the release assets>");
            return 64;
        }

        if (trust.IsTestKey && !expectTestKey)
        {
            error.WriteLine("This binary was built with a TEST signing key (-p:DevDashUpdatePublicKeyFile). It must not be released.");
            return 1;
        }

        try
        {
            var checksumBytes = File.ReadAllBytes(Path.Combine(folder, AgentUpdater.ChecksumsFile));
            var signaturePath = Path.Combine(folder, AgentUpdater.SignatureFile);
            var signature = File.Exists(signaturePath) ? File.ReadAllBytes(signaturePath) : [];
            if (ReleaseVerifier.SignatureProblem(checksumBytes, signature, trust) is { } problem)
            {
                error.WriteLine($"Not a valid release: {problem}");
                error.WriteLine($"This binary's release key: {trust.Describe()}");
                return 1;
            }

            var checksums = ReleaseVerifier.ParseChecksums(Encoding.UTF8.GetString(checksumBytes));
            if (!checksums.ContainsKey(AgentUpdater.VersionFile))
            {
                error.WriteLine($"Not a valid release: checksums.txt does not cover {AgentUpdater.VersionFile}.");
                return 1;
            }

            foreach (var (name, _) in checksums)
            {
                var file = Path.Combine(folder, name);
                if (!File.Exists(file))
                {
                    error.WriteLine($"Not a valid release: {name} is listed in checksums.txt but is not in {folder}.");
                    return 1;
                }

                if (ReleaseVerifier.ChecksumProblem(checksums, name, ReleaseVerifier.Sha256Hex(File.ReadAllBytes(file))) is { } mismatch)
                {
                    error.WriteLine($"Not a valid release: {mismatch}");
                    return 1;
                }
            }

            var version = File.ReadAllText(Path.Combine(folder, AgentUpdater.VersionFile)).Trim();
            if (!SemVer.IsValid(version))
            {
                error.WriteLine($"Not a valid release: version.txt holds '{version}', which is not a version.");
                return 1;
            }

            output.WriteLine($"Release {version}: signature valid for key {trust.Fingerprint}, {checksums.Count} files match checksums.txt.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Not a valid release: {ex.Message}");
            return 1;
        }
    }
}
