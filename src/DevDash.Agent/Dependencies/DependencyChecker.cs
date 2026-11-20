using DevDash.Agent.Toolchains;
using DevDash.Contracts;

namespace DevDash.Agent.Dependencies;

public sealed class DependencyOptions
{
    /// <summary>Check each repository's packages for known vulnerabilities and newer versions. On by default; this is the off switch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often each repository is checked.</summary>
    public double IntervalHours { get; set; } = 24;

    /// <summary>How soon a repository is tried again after a check that failed for a passing reason (offline, timed out).</summary>
    public double RetryHours { get; set; } = 2;

    /// <summary>Longest any one tool run (npm) or nuget.org request may take before it is stopped.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>How long after the agent starts before the first check, so the first scan is not competing with it.</summary>
    public int StartDelaySeconds { get; set; } = 120;

    /// <summary>The pause between one repository and the next.</summary>
    public int PauseSeconds { get; set; } = 5;
}

/// <summary>
/// Checks one repository's dependencies and keeps only counts and a few names:
/// <list type="bullet">
/// <item>NuGet: reads what restore already wrote (<c>obj/project.assets.json</c>) and compares it with
/// nuget.org's published vulnerability data and version lists (<see cref="NuGetOrg"/>). It never runs
/// <c>dotnet</c> in the repository: that would let the repository's <c>global.json</c> pick the SDK (its
/// <c>sdk.paths</c> can point at one inside the repo, which is code execution just by being cloned),
/// have MSBuild evaluate its <c>Directory.Build.props</c>/<c>.targets</c>, and send requests (and the
/// user's feed credentials) to whatever feeds its <c>nuget.config</c> names. A project that has not
/// been restored is reported as that and left alone.</item>
/// <item><c>npm audit --json</c> and <c>npm outdated --json</c>, only where a lockfile and
/// <c>node_modules</c> are both present, with npm and node found outside the repository.</item>
/// </list>
/// It never restores, installs or fixes anything, starts each tool directly with fixed arguments, and
/// treats "offline" as a reason to try again later, not as a result.
/// </summary>
public sealed class DependencyChecker(IToolRunner runner, ToolLocator locator, TimeProvider? time = null, NuGetOrg? nuget = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly NuGetOrg _nuget = nuget ?? new NuGetOrg(time: time);

    /// <summary>Most top-level packages whose versions are looked up on nuget.org in one check.</summary>
    private const int MaxOutdatedLookups = 200;

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
            var (report, again) = await CheckNuGetAsync(repo, target, timeout, ct).ConfigureAwait(false);
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

    private async Task<(DependencyReport Report, bool Transient)> CheckNuGetAsync(string repo, string target, TimeSpan timeout, CancellationToken ct)
    {
        DependencyReport Skip(string why) => new() { Ecosystem = DependencyReport.NuGet, Target = target, Skipped = why };

        var found = NuGetAssets.Find(repo);
        var resolved = new HashSet<PackageVersion>();
        var topLevel = new HashSet<PackageVersion>();
        var read = 0;
        foreach (var file in found.AssetsFiles)
        {
            if (Scanning.RepoFile.ReadAll(repo, file, NuGetAssets.MaxBytes) is { } bytes && NuGetAssets.Parse(bytes) is { } packages)
            {
                read++;
                resolved.UnionWith(packages.Resolved);
                topLevel.UnionWith(packages.TopLevel);
            }
        }

        if (read == 0)
        {
            return (Skip(DependencyParsers.NotRestored), false);
        }

        // Some projects answered: their numbers stand, with a note about the rest.
        var missing = Math.Max(0, found.Projects.Count - read);
        var note = missing > 0 ? $"{missing} of {Math.Max(found.Projects.Count, read)} projects not restored" : null;

        VulnerableResult vulnerable;
        try
        {
            var advisories = await _nuget.AdvisoriesAsync(timeout, ct).ConfigureAwait(false);
            vulnerable = DependencyParsers.NuGetVulnerable(resolved, advisories, note);
        }
        catch (NuGetUnavailableException ex)
        {
            // Offline: the second question would get the same answer.
            return (Report(DependencyReport.NuGet, target, new VulnerableResult(null, [], ex.Message, true), new OutdatedResult(null, [], ex.Message, true)), true);
        }

        // Only nuget.org is asked, so a package from a private feed has no version list there and is not
        // counted as outdated (nor, having no advisories there, as vulnerable).
        OutdatedResult outdated;
        try
        {
            var available = new List<(PackageVersion, IReadOnlyList<string>)>();
            foreach (var package in topLevel.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Take(MaxOutdatedLookups))
            {
                if (await _nuget.VersionsAsync(package.Id, timeout, ct).ConfigureAwait(false) is { } versions)
                {
                    available.Add((package, versions));
                }
            }

            outdated = DependencyParsers.NuGetOutdated(available, note);
        }
        catch (NuGetUnavailableException ex)
        {
            outdated = new OutdatedResult(null, [], ex.Message, true);
        }

        return (Report(DependencyReport.NuGet, target, vulnerable, outdated), vulnerable.Transient || outdated.Transient);
    }

    private async Task<(DependencyReport Report, bool Transient)> CheckNpmAsync(string repo, string lockfile, TimeSpan timeout, CancellationToken ct)
    {
        if (NpmCommand(repo) is not { } npm)
        {
            return (new DependencyReport { Ecosystem = DependencyReport.Npm, Target = lockfile, Skipped = "npm was not found on this machine" }, false);
        }

        ToolCommand Command(params string[] args) => new(npm.File, [.. npm.Prefix, .. args])
        {
            WorkingDirectory = repo,
            Environment = new Dictionary<string, string>(NpmEnvironment) { ["PATH"] = SafePath(repo) },
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
    /// Neither is ever taken from inside the repository (its <c>node_modules/.bin</c>, say): that would be
    /// running the repository's own code.
    /// </summary>
    private (string File, string[] Prefix, string[] Path)? NpmCommand(string repo)
    {
        if (locator.Environment.IsWindows)
        {
            foreach (var node in locator.FindAll("node").Where(n => Outside(repo, n)))
            {
                var cli = Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(cli))
                {
                    return (node, [cli], [Path.GetDirectoryName(node)!]);
                }
            }

            return null;
        }

        if (locator.FindAll("npm").FirstOrDefault(n => Outside(repo, n)) is not { } npm)
        {
            return null;
        }

        string[] path = locator.FindAll("node").FirstOrDefault(n => Outside(repo, n)) is { } found
            ? [Path.GetDirectoryName(npm)!, Path.GetDirectoryName(found)!]
            : [Path.GetDirectoryName(npm)!];
        return (npm, [], path.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>The agent's PATH without relative entries or folders inside the repository, so nothing npm looks up comes from the repo.</summary>
    private string SafePath(string repo) =>
        string.Join(Path.PathSeparator, locator.Environment.PathEntries.Where(p => Path.IsPathRooted(p) && Outside(repo, p)));

    /// <summary>True when <paramref name="path"/>, and what it links to, are outside <paramref name="repo"/>.</summary>
    internal static bool Outside(string repo, string path)
    {
        try
        {
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo)) + Path.DirectorySeparatorChar;
            bool Inside(string p) => (Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar).StartsWith(top, comparison);
            var target = File.Exists(path) ? File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName : null;
            return !Inside(path) && (target is null || !Inside(target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
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
