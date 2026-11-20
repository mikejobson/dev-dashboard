using DevDash.Agent.Toolchains;
using DevDash.Contracts;
using DevDash.Core.Toolchains;

namespace DevDash.Agent.Disk;

public sealed class DiskOptions
{
    /// <summary>Measure the developer caches outside the repositories, and accept CleanCache when actions are on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a measurement is reused before the caches are measured again.</summary>
    public double IntervalHours { get; set; } = 6;

    /// <summary>How long after the agent starts the first measurement waits, so it never competes with the first scan.</summary>
    public int StartDelaySeconds { get; set; } = 90;

    /// <summary>Longest any one cache may take to find and measure; a cache that takes longer is reported as "at least" what was counted.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Longest a clean may run.</summary>
    public int CleanTimeoutSeconds { get; set; } = 600;

    /// <summary><see cref="DiskCacheId"/> names to leave out entirely, e.g. <c>DockerImages</c> on a machine where Docker is slow to answer.</summary>
    public List<string> Skip { get; set; } = [];
}

/// <summary>A program the agent found, and how to start it with no shell.</summary>
/// <param name="Prefix">Arguments that come before the tool's own (npm on Windows is <c>node npm-cli.js</c>).</param>
public sealed record ToolHandle(string File, IReadOnlyList<string> Prefix, IReadOnlyList<string> PathPrefix, IReadOnlyDictionary<string, string> Environment)
{
    public ToolCommand Command(params string[] args) =>
        new(File, [.. Prefix, .. args]) { PathPrefix = PathPrefix, Environment = Environment, StandardInput = null };
}

/// <summary>Where a cache is on this machine, and the tool its recipe needs (null when that tool is not here).</summary>
public sealed record CacheLocation(DiskCacheId Id, string? Path, ToolHandle? Tool);

/// <summary>
/// Finds and measures the developer caches outside the repositories. Every location comes from the tool that
/// owns the cache (<c>dotnet nuget locals</c>, <c>npm config get cache</c>, <c>pnpm store path</c>, <c>brew --cache</c>)
/// or from a fixed place under the home folder; nothing comes from the hub. Each tool call and each walk has a
/// time limit. Nothing here changes anything.
/// </summary>
public sealed class DiskCollector(IToolRunner runner, ToolLocator locator, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private ToolEnvironment Env => locator.Environment;

    private static readonly Dictionary<string, string> DotnetEnvironment = new()
    {
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
    };

    private static readonly Dictionary<string, string> NpmEnvironment = new()
    {
        ["npm_config_update_notifier"] = "false",
        ["npm_config_fund"] = "false",
        ["npm_config_loglevel"] = "error",
    };

    private static readonly Dictionary<string, string> BrewEnvironment = new()
    {
        ["HOMEBREW_NO_AUTO_UPDATE"] = "1",
        ["HOMEBREW_NO_ANALYTICS"] = "1",
        ["HOMEBREW_NO_ENV_HINTS"] = "1",
    };

    private static readonly DiskCacheId[] Docker = [DiskCacheId.DockerImages, DiskCacheId.DockerBuildCache, DiskCacheId.DockerVolumes];

    /// <summary>Every cache that applies to this operating system, in report order.</summary>
    public IEnumerable<DiskCacheId> Applicable() => Enum.GetValues<DiskCacheId>().Where(id => Env.IsMacOS || !IsMacOnly(id));

    public static bool IsMacOnly(DiskCacheId id) =>
        id is DiskCacheId.XcodeDerivedData or DiskCacheId.XcodeArchives or DiskCacheId.XcodeDeviceSupport or DiskCacheId.CoreSimulator;

    /// <summary>Measures every cache that is here, one after another, each within <paramref name="timeout"/>.</summary>
    public async Task<DiskReport> CollectAsync(IReadOnlyCollection<string> skip, TimeSpan timeout, CancellationToken ct)
    {
        var items = new List<DiskItem>();
        var ids = Applicable().Where(id => !skip.Contains(id.ToString())).ToList();
        foreach (var id in ids.Where(id => !Docker.Contains(id)))
        {
            if (await CollectItemAsync(id, timeout, ct).ConfigureAwait(false) is { } item)
            {
                items.Add(item);
            }
        }

        // One answer from Docker covers all three.
        if (ids.Any(Docker.Contains))
        {
            items.AddRange((await DockerAsync(timeout, ct).ConfigureAwait(false)).Where(i => ids.Contains(Enum.Parse<DiskCacheId>(i.Id))));
        }

        return new DiskReport { CollectedAt = _time.GetUtcNow(), Items = items.OrderBy(i => (int)Enum.Parse<DiskCacheId>(i.Id)).ToList() };
    }

    /// <summary>Finds and measures one cache. Null when it is not on this machine.</summary>
    public async Task<DiskItem?> CollectItemAsync(DiskCacheId id, TimeSpan timeout, CancellationToken ct)
    {
        if (Docker.Contains(id))
        {
            return (await DockerAsync(timeout, ct).ConfigureAwait(false)).FirstOrDefault(i => i.Id == id.ToString());
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        CacheLocation? location;
        try
        {
            location = await LocateAsync(id, timeout, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        if (location?.Path is not { } path || !Directory.Exists(path))
        {
            return null;
        }

        var cleanable = DiskCaches.HasRecipe(id) && (location.Tool is not null || DiskCaches.Get(id).Recipe!.TrashContents);
        if (id == DiskCacheId.DotnetSdks)
        {
            return await Task.Run(() => Sdks(limit.Token, timeout), ct).ConfigureAwait(false);
        }

        if (id == DiskCacheId.XcodeDeviceSupport)
        {
            return await Task.Run(() => WithParts(id, path, limit.Token, timeout), ct).ConfigureAwait(false);
        }

        // An empty cache is not worth a row.
        var measured = await Task.Run(() => Measured(id, path, cleanable, limit.Token, timeout), ct).ConfigureAwait(false);
        return measured is { Bytes: 0, Problem: null } ? null : measured;
    }

    /// <summary>
    /// Where a cache is, from the tool that owns it or a fixed place. Used both to measure and, by the cleaner,
    /// to check the cache is still there right before cleaning it. Null when neither the tool nor the folder is here.
    /// </summary>
    public async Task<CacheLocation?> LocateAsync(DiskCacheId id, TimeSpan timeout, CancellationToken ct)
    {
        switch (id)
        {
            case DiskCacheId.NuGetGlobalPackages:
            case DiskCacheId.NuGetHttpCache:
            {
                var dotnet = DotnetTool();
                string? path = null;
                if (dotnet is not null)
                {
                    var result = await runner.RunAsync(dotnet.Command("nuget", "locals", "all", "--list"), timeout, ct).ConfigureAwait(false);
                    if (result.Ok)
                    {
                        DiskParsers.NuGetLocals(result.StdOut).TryGetValue(id == DiskCacheId.NuGetGlobalPackages ? "global-packages" : "http-cache", out path);
                    }
                }

                // Without dotnet (or an answer), the default places, measured but not cleanable.
                path ??= id == DiskCacheId.NuGetGlobalPackages
                    ? Env.InHome(".nuget", "packages")
                    : Env.IsWindows
                        ? Path.Combine(Env.Var("LOCALAPPDATA") ?? Env.InHome("AppData", "Local"), "NuGet", "v3-cache")
                        : Env.InHome(".local", "share", "NuGet", "http-cache");
                return new CacheLocation(id, path, dotnet);
            }

            case DiskCacheId.NpmCache:
            {
                var npm = NpmTool();
                string? path = null;
                if (npm is not null)
                {
                    var result = await runner.RunAsync(npm.Command("config", "get", "cache"), timeout, ct).ConfigureAwait(false);
                    path = result.Ok ? DiskParsers.SinglePath(result.StdOut) : null;
                }

                path ??= Env.IsWindows ? Path.Combine(Env.Var("LOCALAPPDATA") ?? Env.InHome("AppData", "Local"), "npm-cache") : Env.InHome(".npm");
                return new CacheLocation(id, path, npm);
            }

            case DiskCacheId.PnpmStore:
            {
                var pnpm = Find("pnpm", [Env.InHome("Library", "pnpm"), Env.InHome(".local", "share", "pnpm"), Path.Combine(Env.Var("LOCALAPPDATA") ?? Env.InHome("AppData", "Local"), "pnpm")]);
                if (pnpm is null)
                {
                    return null;
                }

                var result = await runner.RunAsync(pnpm.Command("store", "path"), timeout, ct).ConfigureAwait(false);
                return result.Ok && DiskParsers.SinglePath(result.StdOut) is { } store ? new CacheLocation(id, store, pnpm) : null;
            }

            case DiskCacheId.YarnCache:
            {
                string[] places = Env.IsWindows
                    ? [Path.Combine(Env.Var("LOCALAPPDATA") ?? Env.InHome("AppData", "Local"), "Yarn", "Cache"), Env.InHome(".yarn", "berry", "cache")]
                    : [Env.IsMacOS ? Env.InHome("Library", "Caches", "Yarn") : Env.InHome(".cache", "yarn"), Env.InHome(".yarn", "berry", "cache")];
                return places.FirstOrDefault(Directory.Exists) is { } yarn ? new CacheLocation(id, yarn, null) : null;
            }

            case DiskCacheId.XcodeDerivedData:
                return new CacheLocation(id, Env.InHome("Library", "Developer", "Xcode", "DerivedData"), null);
            case DiskCacheId.XcodeArchives:
                return new CacheLocation(id, Env.InHome("Library", "Developer", "Xcode", "Archives"), null);
            case DiskCacheId.XcodeDeviceSupport:
                return new CacheLocation(id, Env.InHome("Library", "Developer", "Xcode", "iOS DeviceSupport"), null);
            case DiskCacheId.CoreSimulator:
            {
                // simctl comes with Xcode itself; /usr/bin/xcrun alone would offer to install the command line tools.
                var xcrun = Env.Rooted("/usr/bin/xcrun");
                var tool = File.Exists(xcrun) && Directory.Exists(Env.Rooted("/Applications/Xcode.app"))
                    ? new ToolHandle(xcrun, [], [], new Dictionary<string, string>())
                    : null;
                return new CacheLocation(id, Env.InHome("Library", "Developer", "CoreSimulator"), tool);
            }

            case DiskCacheId.DockerImages:
            case DiskCacheId.DockerBuildCache:
            case DiskCacheId.DockerVolumes:
                return DockerTool() is { } docker ? new CacheLocation(id, null, docker) : null;

            case DiskCacheId.DotnetSdks:
                return DotnetRoots().FirstOrDefault() is { } root ? new CacheLocation(id, Path.Combine(root, "sdk"), null) : null;

            case DiskCacheId.HomebrewCache:
            {
                if (Find("brew", []) is not { } brew)
                {
                    return null;
                }

                var brewTool = brew with { Environment = BrewEnvironment };
                var result = await runner.RunAsync(brewTool.Command("--cache"), timeout, ct).ConfigureAwait(false);
                return result.Ok && DiskParsers.SinglePath(result.StdOut) is { } cache ? new CacheLocation(id, cache, brewTool) : null;
            }

            case DiskCacheId.GradleCache:
                return new CacheLocation(id, Env.InHome(".gradle", "caches"), null);
            case DiskCacheId.MavenRepository:
                return new CacheLocation(id, Env.InHome(".m2", "repository"), null);
            default:
                return null;
        }
    }

    /// <summary>
    /// Docker's own accounting: images (with what pruning the dangling ones would free), build cache and volumes.
    /// Docker not installed: nothing. Installed but not answering (the daemon is not running): one row saying so.
    /// </summary>
    private async Task<IReadOnlyList<DiskItem>> DockerAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (DockerTool() is not { } docker)
        {
            return [];
        }

        var now = _time.GetUtcNow();
        var df = await runner.RunAsync(docker.Command("system", "df", "--format", "json"), timeout, ct).ConfigureAwait(false);
        var rows = df.Ok ? DiskParsers.DockerDf(df.StdOut) : [];
        if (rows.Count == 0)
        {
            var why = df.TimedOut ? "Docker did not answer in time." : "Docker is installed but did not answer; is it running?";
            return [new DiskItem { Id = nameof(DiskCacheId.DockerImages), MeasuredAt = now, Problem = why }];
        }

        var dangling = await runner.RunAsync(docker.Command("image", "ls", "--filter", "dangling=true", "--format", "json"), timeout, ct).ConfigureAwait(false);
        (int Count, long Bytes)? danglingSize = dangling.Ok ? DiskParsers.DanglingImages(dangling.StdOut) : null;

        DockerDfRow? Row(string type) => rows.FirstOrDefault(r => r.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        var items = new List<DiskItem>();
        if (Row("Images") is { } images)
        {
            items.Add(new DiskItem
            {
                Id = nameof(DiskCacheId.DockerImages),
                Bytes = images.Size,
                ReclaimableBytes = danglingSize?.Bytes,
                Count = images.TotalCount,
                MeasuredAt = now,
                Cleanable = true,
                Problem = danglingSize is null ? "Could not list dangling images; what pruning frees is unknown." : null,
            });
        }

        if (Row("Build Cache") is { } build)
        {
            items.Add(new DiskItem { Id = nameof(DiskCacheId.DockerBuildCache), Bytes = build.Size, ReclaimableBytes = build.Reclaimable, Count = build.TotalCount, MeasuredAt = now, Cleanable = true });
        }

        if (Row("Local Volumes") is { } volumes)
        {
            items.Add(new DiskItem { Id = nameof(DiskCacheId.DockerVolumes), Bytes = volumes.Size, Count = volumes.TotalCount, MeasuredAt = now });
        }

        return items;
    }

    private DiskItem Measured(DiskCacheId id, string path, bool cleanable, CancellationToken token, TimeSpan timeout)
    {
        var size = FolderMeasure.Measure(path, token);
        return new DiskItem
        {
            Id = id.ToString(),
            Path = path,
            Bytes = size.Bytes,
            LastModified = size.NewestWrite,
            MeasuredAt = _time.GetUtcNow(),
            Cleanable = cleanable,
            Problem = size.Complete ? null : Stopped(timeout),
        };
    }

    /// <summary>A folder measured one sub-folder at a time, each a part: iOS DeviceSupport, one per iOS version.</summary>
    private DiskItem WithParts(DiskCacheId id, string path, CancellationToken token, TimeSpan timeout)
    {
        var parts = new List<DiskPart>();
        var complete = true;
        foreach (var child in ToolLocator.Children(path).Order(StringComparer.Ordinal))
        {
            var size = FolderMeasure.Measure(child, token);
            complete &= size.Complete;
            parts.Add(new DiskPart { Name = Path.GetFileName(child), Bytes = size.Bytes, LastModified = size.NewestWrite });
        }

        return new DiskItem
        {
            Id = id.ToString(),
            Path = path,
            Bytes = parts.Sum(p => p.Bytes),
            LastModified = parts.Max(p => p.LastModified),
            MeasuredAt = _time.GetUtcNow(),
            Problem = complete ? null : Stopped(timeout),
            Parts = parts.OrderByDescending(p => p.Bytes).Take(50).ToList(),
        };
    }

    /// <summary>Every <c>sdk/&lt;version&gt;</c> folder of every .NET installation found, one part each.</summary>
    private DiskItem? Sdks(CancellationToken token, TimeSpan timeout)
    {
        var parts = new List<DiskPart>();
        var complete = true;
        string? first = null;
        foreach (var root in DotnetRoots())
        {
            var sdk = Path.Combine(root, "sdk");
            foreach (var dir in ToolLocator.Children(sdk))
            {
                var name = Path.GetFileName(dir);
                if (ToolVersion.Parse(name) is not { IsFull: true } || parts.Any(p => p.Name == name))
                {
                    continue;
                }

                first ??= sdk;
                var size = FolderMeasure.Measure(dir, token);
                complete &= size.Complete;
                parts.Add(new DiskPart { Name = name, Bytes = size.Bytes, LastModified = size.NewestWrite });
            }
        }

        if (first is null)
        {
            return null;
        }

        return new DiskItem
        {
            Id = nameof(DiskCacheId.DotnetSdks),
            Path = first,
            Bytes = parts.Sum(p => p.Bytes),
            LastModified = parts.Max(p => p.LastModified),
            MeasuredAt = _time.GetUtcNow(),
            Count = parts.Count,
            Problem = complete ? null : Stopped(timeout),
            Parts = parts.OrderByDescending(p => ToolVersion.Parse(p.Name)).Take(50).ToList(),
        };
    }

    private static string Stopped(TimeSpan timeout) => $"Stopped measuring after {timeout.TotalSeconds:0} s: it is at least this big.";

    /// <summary>The folder of each .NET installation, links followed (<c>/usr/local/bin/dotnet</c> is a link into <c>/usr/local/share/dotnet</c>).</summary>
    private IEnumerable<string> DotnetRoots()
    {
        var seen = new HashSet<string>(Env.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in locator.FindAll("dotnet"))
        {
            string real;
            try
            {
                real = new FileInfo(file).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                real = file;
            }

            if (Path.GetDirectoryName(real) is { } dir && Directory.Exists(Path.Combine(dir, "sdk")) && seen.Add(dir))
            {
                yield return dir;
            }
        }
    }

    private ToolHandle? DotnetTool() =>
        locator.Find("dotnet") is { } dotnet ? new ToolHandle(dotnet, [], [], DotnetEnvironment) : null;

    /// <summary>As the dependency checks start npm: directly on macOS and Linux (with node's folder on the PATH), through <c>node npm-cli.js</c> on Windows.</summary>
    private ToolHandle? NpmTool()
    {
        if (Env.IsWindows)
        {
            foreach (var node in locator.FindAll("node"))
            {
                var cli = Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(cli))
                {
                    return new ToolHandle(node, [cli], [Path.GetDirectoryName(node)!], NpmEnvironment);
                }
            }

            return null;
        }

        if (locator.Find("npm") is not { } npm)
        {
            return null;
        }

        string[] path = locator.Find("node") is { } found ? [Path.GetDirectoryName(npm)!, Path.GetDirectoryName(found)!] : [Path.GetDirectoryName(npm)!];
        return new ToolHandle(npm, [], path.Distinct(StringComparer.Ordinal).ToArray(), NpmEnvironment);
    }

    private ToolHandle? DockerTool()
    {
        var programFiles = Env.Var("ProgramFiles") ?? @"C:\Program Files";
        return Find("docker", Env.IsWindows
            ? [Path.Combine(programFiles, "Docker", "Docker", "resources", "bin")]
            : [Env.InHome(".docker", "bin"), Env.Rooted("/Applications/Docker.app/Contents/Resources/bin"), Env.Rooted("/opt/homebrew/bin"), Env.Rooted("/usr/local/bin"), Env.Rooted("/usr/bin")]);
    }

    /// <summary>A tool on the PATH or the usual places, else in <paramref name="extra"/>. Its own folder goes on the child's PATH (pnpm and brew run scripts).</summary>
    private ToolHandle? Find(string tool, IEnumerable<string> extra)
    {
        var file = locator.Find(tool) ?? extra
            .Select(dir => Path.Combine(dir, Env.IsWindows ? tool + ".exe" : tool))
            .FirstOrDefault(File.Exists);
        return file is null ? null : new ToolHandle(file, [], [Path.GetDirectoryName(file)!], new Dictionary<string, string>());
    }
}
