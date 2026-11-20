using System.Text.RegularExpressions;
using DevDash.Contracts;
using DevDash.Core.Toolchains;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Toolchains;

public sealed class ToolchainOptions
{
    /// <summary>Report which SDKs and runtimes this machine has.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long an inventory is reused before the tools are asked again.</summary>
    public double IntervalHours { get; set; } = 6;

    /// <summary>Longest any one <c>--version</c> call may take.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Finds out which toolchains a machine has: <c>dotnet --list-sdks</c>, <c>node --version</c>,
/// <c>python3 --version</c>, <c>go version</c>, <c>rustc --version</c>, <c>java -version</c>, each started
/// directly with a short timeout, plus the versions the version managers hold (nvm, fnm, volta, pyenv,
/// asdf, mise), which are read off their folder names without starting anything.
/// </summary>
public sealed partial class ToolchainCollector(IToolRunner runner, ToolLocator locator, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    [GeneratedRegex(@"\d+(?:\.\d+){1,3}(?:-[0-9A-Za-z.-]+)?")]
    private static partial Regex VersionPattern();

    public async Task<ToolchainInventory> CollectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var tools = new List<InstalledToolchain>();

        void Add(string tool, string? active, IEnumerable<string> versions, IEnumerable<string> managers)
        {
            var all = ToolVersion.ParseAll(versions.Append(active).Where(v => v is not null)!).Select(v => v.ToString()).ToList();
            if (all.Count > 0)
            {
                tools.Add(new InstalledToolchain
                {
                    Tool = tool,
                    Active = ToolVersion.Parse(active)?.ToString(),
                    Versions = all,
                    Managers = managers.Distinct(StringComparer.Ordinal).ToList(),
                });
            }
        }

        // .NET: every installation found lists its own SDKs.
        var sdks = new List<string>();
        foreach (var dotnet in locator.FindAll("dotnet"))
        {
            var result = await runner.RunAsync(new ToolCommand(dotnet, ["--list-sdks"]) { Environment = DotnetEnvironment }, timeout, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                sdks.AddRange(ParseSdkList(result.StdOut));
            }
        }

        var (dotnetManaged, dotnetManagers) = Managed(ToolNames.Dotnet);
        Add(ToolNames.Dotnet, null, sdks.Concat(dotnetManaged), dotnetManagers);

        // Node: what `node` is, and what the version managers could switch to.
        var nodeVersions = new List<string>();
        var nodeManagers = new List<string>();
        foreach (var (manager, root, _) in locator.NodeManagerRoots())
        {
            var found = ToolLocator.Children(root).Select(Path.GetFileName).Where(n => ToolVersion.Parse(n) is not null).Select(n => n!).ToList();
            if (found.Count > 0)
            {
                nodeVersions.AddRange(found);
                nodeManagers.Add(manager);
            }
        }

        var (nodeManaged, moreNodeManagers) = Managed(ToolNames.Node);
        Add(ToolNames.Node, await VersionOfAsync(["node"], ["--version"], timeout, ct).ConfigureAwait(false), nodeVersions.Concat(nodeManaged), nodeManagers.Concat(moreNodeManagers));

        var (pythons, pythonManagers) = Managed(ToolNames.Python);
        Add(ToolNames.Python, await VersionOfAsync(locator.Environment.IsWindows ? ["python", "python3"] : ["python3", "python"], ["--version"], timeout, ct).ConfigureAwait(false), pythons, pythonManagers);

        var (gos, goManagers) = Managed(ToolNames.Go);
        Add(ToolNames.Go, await VersionOfAsync(["go"], ["version"], timeout, ct).ConfigureAwait(false), gos, goManagers);

        var (rusts, rustManagers) = Managed(ToolNames.Rust);
        Add(ToolNames.Rust, await VersionOfAsync(["rustc"], ["--version"], timeout, ct).ConfigureAwait(false), rusts, rustManagers);

        // `java -version` prints to stderr. asdf and mise name Java installs by vendor, so only what runs is listed.
        Add(ToolNames.Java, await VersionOfAsync(["java"], ["-version"], timeout, ct).ConfigureAwait(false), [], []);

        return new ToolchainInventory { CollectedAt = _time.GetUtcNow(), Tools = tools };
    }

    private static readonly Dictionary<string, string> DotnetEnvironment = new()
    {
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
    };

    /// <summary>Lines of <c>dotnet --list-sdks</c>: <c>10.0.100 [/usr/local/share/dotnet/sdk]</c>.</summary>
    public static IEnumerable<string> ParseSdkList(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2)[0])
            .Where(v => ToolVersion.Parse(v) is { IsFull: true });

    /// <summary>The first version number in a tool's answer: <c>v20.11.0</c>, <c>Python 3.12.4</c>, <c>go version go1.22.3 darwin/arm64</c>, <c>openjdk version "21.0.3" 2024-04-16</c>.</summary>
    public static string? ParseVersion(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(5))
        {
            if (VersionPattern().Match(line) is { Success: true } match && ToolVersion.Parse(match.Value) is not null)
            {
                return match.Value;
            }
        }

        return null;
    }

    private async Task<string?> VersionOfAsync(string[] names, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        foreach (var name in names)
        {
            foreach (var file in locator.FindAll(name).Take(2))
            {
                var result = await runner.RunAsync(new ToolCommand(file, args) { PathPrefix = [Path.GetDirectoryName(file)!] }, timeout, ct).ConfigureAwait(false);
                if (result.Ok && ParseVersion(result.StdOut.Length > 0 ? result.StdOut : result.StdErr) is { } version)
                {
                    return version;
                }
            }
        }

        return null;
    }

    private (List<string> Versions, List<string> Managers) Managed(string tool)
    {
        var versions = new List<string>();
        var managers = new List<string>();
        foreach (var (manager, root) in locator.ManagedVersionRoots(tool))
        {
            var found = ToolLocator.Children(root).Select(Path.GetFileName).Where(n => ToolVersion.Parse(n) is { IsPrerelease: false }).Select(n => n!).ToList();
            if (found.Count > 0)
            {
                versions.AddRange(found);
                managers.Add(manager);
            }
        }

        return (versions, managers);
    }
}

/// <summary>
/// Keeps the machine's inventory for <see cref="ToolchainOptions.IntervalHours"/>: collected for the first
/// scan after the agent starts, then reused by every scan until it is due again.
/// </summary>
public sealed class ToolchainService(ToolchainCollector collector, IOptionsMonitor<AgentOptions> options, ILogger<ToolchainService> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ToolchainInventory? _cached;

    /// <summary>The inventory, collected now when there is none or it is due. Null when the feature is off.</summary>
    public async Task<ToolchainInventory?> GetAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue.Toolchains;
        if (!settings.Enabled)
        {
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_cached is not null && now - _cached.CollectedAt < TimeSpan.FromHours(Math.Max(0.05, settings.IntervalHours)))
            {
                return _cached;
            }

            try
            {
                _cached = await collector.CollectAsync(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 120)), ct).ConfigureAwait(false);
                logger.LogInformation("Toolchains: {Tools}", _cached.Tools.Count == 0 ? "none found" : string.Join("; ", _cached.Tools.Select(t => $"{t.Tool} {string.Join(", ", t.Versions.Take(4))}")));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never let this stop a scan: the last inventory, or none, is sent instead.
                logger.LogWarning("Could not collect toolchains: {Error}", ex.Message);
            }

            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
