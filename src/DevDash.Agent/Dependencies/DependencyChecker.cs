using DevDash.Agent.Toolchains;
using DevDash.Contracts;

namespace DevDash.Agent.Dependencies;

public sealed class DependencyOptions
{
    /// <summary>Run the package tools' own vulnerability and outdated checks in each repository. On by default; this is the off switch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often each repository is checked.</summary>
    public double IntervalHours { get; set; } = 24;

    /// <summary>How soon a repository is tried again after a check that failed for a passing reason (offline, timed out).</summary>
    public double RetryHours { get; set; } = 2;

    /// <summary>Longest any one tool run may take before it is stopped.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>How long after the agent starts before the first check, so the first scan is not competing with it.</summary>
    public int StartDelaySeconds { get; set; } = 120;

    /// <summary>The pause between one repository and the next.</summary>
    public int PauseSeconds { get; set; } = 5;
}

/// <summary>
/// Asks the package tools about one repository and keeps only counts and a few names:
/// <list type="bullet">
/// <item><c>dotnet list &lt;sln or project&gt; package --vulnerable --include-transitive --format json</c> and
/// <c>--outdated --format json</c>, with <c>--no-restore</c> on SDKs that would otherwise restore first. A
/// project that has not been restored is reported as that and left alone.</item>
/// <item><c>npm audit --json</c> and <c>npm outdated --json</c>, only where a lockfile and
/// <c>node_modules</c> are both present.</item>
/// </list>
/// It never restores, installs or fixes anything, starts each tool directly with fixed arguments, and
/// treats "offline" as a reason to try again later, not as a result.
/// </summary>
public sealed class DependencyChecker(IToolRunner runner, ToolLocator locator, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private static readonly Dictionary<string, string> DotnetEnvironment = new()
    {
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["MSBUILDDISABLENODEREUSE"] = "1", // no build server left running behind the check
        ["MSBUILDTERMINALLOGGER"] = "off",
    };

    private static readonly Dictionary<string, string> NpmEnvironment = new()
    {
        ["npm_config_update_notifier"] = "false",
        ["npm_config_fund"] = "false",
        ["npm_config_loglevel"] = "error",
        ["NO_COLOR"] = "1",
        ["CI"] = "1",
    };

    /// <summary>Checks one repository. The entry's reports are empty when there is nothing there to check.</summary>
    public async Task<DependencyCacheEntry> CheckAsync(string repo, TimeSpan timeout, CancellationToken ct)
    {
        var reports = new List<DependencyReport>();
        var transient = false;

        if (FindDotnetTarget(repo) is { } target)
        {
            var (report, again) = await CheckDotnetAsync(repo, target, timeout, ct).ConfigureAwait(false);
            reports.Add(report);
            transient |= again;
        }

        if (FindNpmLockfile(repo) is { } lockfile)
        {
            var (report, again) = await CheckNpmAsync(repo, lockfile, timeout, ct).ConfigureAwait(false);
            reports.Add(report);
            transient |= again;
        }

        var now = _time.GetUtcNow();
        return new DependencyCacheEntry(new DependencyHealth { CheckedAt = now, Reports = reports }, now, transient);
    }

    /// <summary>
    /// The solution or project to ask about, relative to the repository: one at its top, else one in
    /// <c>src/</c>. A solution is preferred, since it covers every project. Null when there is none.
    /// </summary>
    public static string? FindDotnetTarget(string repo)
    {
        foreach (var dir in (string[])[repo, Path.Combine(repo, "src")])
        {
            foreach (var pattern in (string[])["*.sln", "*.slnx", "*.csproj", "*.fsproj", "*.vbproj"])
            {
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        break;
                    }

                    var first = Directory.EnumerateFiles(dir, pattern).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault(f => Path.GetExtension(f).Equals(pattern[1..], StringComparison.OrdinalIgnoreCase));
                    if (first is not null)
                    {
                        return Path.GetRelativePath(repo, first);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }
            }
        }

        return null;
    }

    /// <summary>The lockfile's name, when the repository has one and <c>node_modules</c> beside it. Otherwise npm would have to install first, which this never does.</summary>
    public static string? FindNpmLockfile(string repo)
    {
        try
        {
            if (!Directory.Exists(Path.Combine(repo, "node_modules")))
            {
                return null;
            }

            return ((string[])["package-lock.json", "npm-shrinkwrap.json"]).FirstOrDefault(name => File.Exists(Path.Combine(repo, name)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<(DependencyReport Report, bool Transient)> CheckDotnetAsync(string repo, string target, TimeSpan timeout, CancellationToken ct)
    {
        DependencyReport Skip(string why) => new() { Ecosystem = DependencyReport.NuGet, Target = target, Skipped = why };

        if (locator.Find("dotnet") is not { } dotnet)
        {
            return (Skip("dotnet was not found on this machine"), false);
        }

        ToolCommand Command(params string[] args) => new(dotnet, args) { WorkingDirectory = repo, Environment = DotnetEnvironment, LowPriority = true };

        // Asked in the repository, so its global.json decides which SDK answers. That SDK's own help says
        // what it understands: JSON output (7.0.200 and later), and whether it would restore unless told not to.
        var help = await runner.RunAsync(Command("list", "package", "--help"), timeout, ct).ConfigureAwait(false);
        if (!help.Ok)
        {
            return help.TimedOut
                ? (Skip("timed out"), true)
                : (Skip(help.Started ? "the .NET SDK this repo asks for is not installed" : "dotnet could not be started"), false);
        }

        if (!help.StdOut.Contains("--format", StringComparison.Ordinal))
        {
            return (Skip("this .NET SDK is too old to report as JSON"), false);
        }

        string[] noRestore = help.StdOut.Contains("--no-restore", StringComparison.Ordinal) ? ["--no-restore"] : [];

        var vulnerableRun = await runner.RunAsync(Command(["list", target, "package", "--vulnerable", "--include-transitive", "--format", "json", .. noRestore]), timeout, ct).ConfigureAwait(false);
        var vulnerable = vulnerableRun.TimedOut ? new VulnerableResult(null, [], "timed out", true)
            : !vulnerableRun.Started ? new VulnerableResult(null, [], "dotnet could not be started")
            : DependencyParsers.DotnetVulnerable(vulnerableRun.StdOut);

        // Not restored, or offline: the second question would get the same answer.
        var outdated = new OutdatedResult(null, [], vulnerable.Skipped, vulnerable.Transient);
        if (vulnerable.Counts is not null)
        {
            var outdatedRun = await runner.RunAsync(Command(["list", target, "package", "--outdated", "--format", "json", .. noRestore]), timeout, ct).ConfigureAwait(false);
            outdated = outdatedRun.TimedOut ? new OutdatedResult(null, [], "timed out", true)
                : !outdatedRun.Started ? new OutdatedResult(null, [], "dotnet could not be started")
                : DependencyParsers.DotnetOutdated(outdatedRun.StdOut);
        }

        return (Report(DependencyReport.NuGet, target, vulnerable, outdated), vulnerable.Transient || outdated.Transient);
    }

    private async Task<(DependencyReport Report, bool Transient)> CheckNpmAsync(string repo, string lockfile, TimeSpan timeout, CancellationToken ct)
    {
        if (NpmCommand() is not { } npm)
        {
            return (new DependencyReport { Ecosystem = DependencyReport.Npm, Target = lockfile, Skipped = "npm was not found on this machine" }, false);
        }

        ToolCommand Command(params string[] args) => new(npm.File, [.. npm.Prefix, .. args])
        {
            WorkingDirectory = repo,
            Environment = NpmEnvironment,
            PathPrefix = npm.Path,
            LowPriority = true,
        };

        // Both exit with 1 when they find something, so the exit code says nothing: the JSON does.
        var auditRun = await runner.RunAsync(Command("audit", "--json"), timeout, ct).ConfigureAwait(false);
        var audit = auditRun.TimedOut ? new VulnerableResult(null, [], "timed out", true)
            : !auditRun.Started ? new VulnerableResult(null, [], "npm could not be started")
            : DependencyParsers.NpmAudit(auditRun.StdOut);

        var outdated = new OutdatedResult(null, [], audit.Skipped, audit.Transient);
        if (!audit.Transient && auditRun.Started)
        {
            var outdatedRun = await runner.RunAsync(Command("outdated", "--json"), timeout, ct).ConfigureAwait(false);
            outdated = outdatedRun.TimedOut ? new OutdatedResult(null, [], "timed out", true) : DependencyParsers.NpmOutdated(outdatedRun.StdOut);
        }

        return (Report(DependencyReport.Npm, lockfile, audit, outdated), audit.Transient || outdated.Transient);
    }

    /// <summary>
    /// How to start npm with no shell. On macOS and Linux <c>npm</c> is a script that asks <c>env</c> for
    /// <c>node</c>, so node's folder goes on the child's PATH. On Windows <c>npm</c> is a <c>.cmd</c> file,
    /// which only a shell can run: there, <c>node.exe</c> is started with npm's own <c>npm-cli.js</c>.
    /// </summary>
    private (string File, string[] Prefix, string[] Path)? NpmCommand()
    {
        if (locator.Environment.IsWindows)
        {
            foreach (var node in locator.FindAll("node"))
            {
                var cli = Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(cli))
                {
                    return (node, [cli], [Path.GetDirectoryName(node)!]);
                }
            }

            return null;
        }

        if (locator.Find("npm") is not { } npm)
        {
            return null;
        }

        string[] path = locator.Find("node") is { } found
            ? [Path.GetDirectoryName(npm)!, Path.GetDirectoryName(found)!]
            : [Path.GetDirectoryName(npm)!];
        return (npm, [], path.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static DependencyReport Report(string ecosystem, string target, VulnerableResult vulnerable, OutdatedResult outdated) => new()
    {
        Ecosystem = ecosystem,
        Target = target,
        Vulnerable = vulnerable.Counts,
        TopVulnerable = vulnerable.Top,
        Outdated = outdated.Count,
        TopOutdated = outdated.Top,
        Skipped = vulnerable.Skipped ?? outdated.Skipped,
    };
}
