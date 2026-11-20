using System.Diagnostics;
using DevDash.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Scanning;

public enum SizeMode
{
    /// <summary>Use cached sizes only; never walk build folders.</summary>
    CachedOnly,

    /// <summary>Measure entries whose cached size is missing or older than the size-scan interval.</summary>
    WhenDue,

    /// <summary>Measure everything now.</summary>
    Force,
}

/// <param name="ForcedFetch">Set for a refresh asked for from the dashboard: fetch now instead of when due. Needs <paramref name="AllowFetch"/>.</param>
public sealed record ScanRequest(bool AllowFetch, SizeMode Sizes, Git.ForcedFetch? ForcedFetch = null)
{
    /// <summary>
    /// Set by an action that has just fetched the repository itself: the scan does not fetch again, and
    /// lists the remote's tags afresh whatever the auto-fetch setting, so the result is as current as it can be.
    /// </summary>
    public bool JustFetched { get; init; }
}

/// <summary>Scans the configured roots into a <see cref="MachineSnapshot"/>. Reads only, apart from optional fetches.</summary>
public sealed class MachineScanner
{
    private readonly RepoScanner _repos;
    private readonly SizeCache _sizes;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<MachineScanner> _logger;
    private readonly TimeProvider _time;
    private readonly Toolchains.ToolchainService? _toolchains;
    private readonly Dependencies.DependencyCache? _dependencies;
    private readonly Dependencies.DependencyTargets? _dependencyTargets;
    private readonly T3Code.T3Service? _t3;
    private readonly Disk.DiskService? _disk;
    private readonly DevServers.DevServerCollector? _devServers;

    /// <summary>
    /// The machine facts read afresh with every push (§21): the last disk report (measured in the background, only read
    /// here) and the listening dev servers, matched to the working trees in <paramref name="entries"/>. Never fails a scan.
    /// </summary>
    public async Task<MachineInfo> WithMachineFactsAsync(MachineInfo machine, IReadOnlyList<DevEntry> entries, CancellationToken ct)
    {
        DevServerReport? servers = null;
        if (_devServers is not null)
        {
            try
            {
                servers = await _devServers.CollectAsync(entries, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Could not list dev servers: {Error}", ex.Message);
            }
        }

        return machine with { Disk = _disk?.Current, DevServers = servers };
    }

    public MachineScanner(
        RepoScanner repos, SizeCache sizes, IOptionsMonitor<AgentOptions> options, ILogger<MachineScanner> logger, TimeProvider? time = null,
        Toolchains.ToolchainService? toolchains = null, Dependencies.DependencyCache? dependencies = null, Dependencies.DependencyTargets? dependencyTargets = null,
        T3Code.T3Service? t3 = null, Disk.DiskService? disk = null, DevServers.DevServerCollector? devServers = null)
    {
        _disk = disk;
        _devServers = devServers;
        _t3 = t3;
        _toolchains = toolchains;
        _dependencies = dependencies;
        _dependencyTargets = dependencyTargets;
        _repos = repos;
        _sizes = sizes;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<MachineSnapshot> ScanAsync(ScanRequest request, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var stopwatch = Stopwatch.StartNew();
        var roots = options.ResolveRoots();
        var machine = await MachineInfoProvider.GetAsync(options, ct).ConfigureAwait(false);
        if (_toolchains is not null)
        {
            // Collected for the first scan after the agent starts, then reused for hours: not a cost of every scan.
            machine = machine with { Toolchains = await _toolchains.GetAsync(ct).ConfigureAwait(false) };
        }

        var targets = new List<(string Root, string Path)>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                _logger.LogWarning("Scan root {Root} does not exist", root);
                continue;
            }

            targets.AddRange(TopLevel(root).Select(path => (root, path)));
        }

        var entries = new DevEntry[targets.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, targets.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism), CancellationToken = ct },
            async (i, token) => entries[i] = await ScanEntryAsync(targets[i].Root, targets[i].Path, request, options.WorktreeContainerDepth, token).ConfigureAwait(false))
            .ConfigureAwait(false);

        if (request.Sizes != SizeMode.CachedOnly)
        {
            _sizes.Save();
        }

        // The background dependency checks work through whatever the latest full scan found.
        _dependencyTargets?.Offer(DevDash.Core.RepoIdentity.Flatten(entries).Where(e => e.Kind == EntryKind.GitRepo).Select(e => e.Path));

        // T3 Code: each repository's own project and threads. Read-only calls; a T3 that is missing or not running only leaves them out.
        IReadOnlyList<DevEntry> scanned = entries;
        if (_t3 is not null && await _t3.InfoAsync(ct).ConfigureAwait(false) is { } t3Info)
        {
            scanned = await _t3.EnrichAsync(scanned, fullScan: true, ct).ConfigureAwait(false);
            machine = machine with { T3 = _t3.Current ?? t3Info, AcceptsT3 = options.Actions.Enabled && t3Info.Available };
        }

        machine = await WithMachineFactsAsync(machine, scanned, ct).ConfigureAwait(false);

        return new MachineSnapshot
        {
            Machine = machine,
            ScannedAt = _time.GetUtcNow(),
            ScanDurationMs = stopwatch.ElapsedMilliseconds,
            Roots = roots,
            Entries = WithOneGraphPerRepository(scanned.OrderBy(e => e.Root, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()),
        };
    }

    /// <summary>
    /// A repository and its linked worktrees share one commit graph. When the main working tree is in the
    /// snapshot it carries the graph and the linked worktrees' copies are dropped; a linked worktree whose
    /// main working tree is outside the scan roots keeps its own.
    /// </summary>
    public static IReadOnlyList<DevEntry> WithOneGraphPerRepository(IReadOnlyList<DevEntry> entries)
    {
        var carried = new HashSet<string>(StringComparer.Ordinal);
        var carriedActivity = new HashSet<string>(StringComparer.Ordinal);
        void Collect(IEnumerable<DevEntry> list)
        {
            foreach (var entry in list)
            {
                if (entry.Git is { IsLinkedWorktree: false, Graph: not null, CommonDir: { } dir })
                {
                    carried.Add(dir);
                }

                if (entry.Git is { IsLinkedWorktree: false, Activity: not null, CommonDir: { } activityDir })
                {
                    carriedActivity.Add(activityDir);
                }

                Collect(entry.Children);
            }
        }

        DevEntry Strip(DevEntry entry)
        {
            var children = entry.Children.Count == 0 ? entry.Children : entry.Children.Select(Strip).ToList();
            var git = entry.Git;
            if (git is { IsLinkedWorktree: true, Graph: not null, CommonDir: { } dir } && !carried.Add(dir))
            {
                // The remote branches are the repository's too, and go with the graph.
                git = git with { Graph = null, RemoteBranches = null };
            }

            // The same goes for the commits-per-day counts: one history, sent once.
            if (git is { IsLinkedWorktree: true, Activity: not null, CommonDir: { } shared } && !carriedActivity.Add(shared))
            {
                git = git with { Activity = null };
            }

            return ReferenceEquals(git, entry.Git) && ReferenceEquals(children, entry.Children) ? entry : entry with { Git = git, Children = children };
        }

        Collect(entries);
        return entries.Select(Strip).ToList();
    }

    /// <summary>Scans a single top-level entry (used for debounced rescans). Returns null when it no longer exists.</summary>
    public async Task<DevEntry?> ScanEntryAsync(string root, string name, ScanRequest request, CancellationToken ct)
    {
        var path = Path.Combine(root, name);
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return null;
        }

        var entry = await ScanEntryAsync(root, path, request, _options.CurrentValue.WorktreeContainerDepth, ct).ConfigureAwait(false);
        if (request.Sizes != SizeMode.CachedOnly)
        {
            _sizes.Save();
        }

        if (_t3 is not null)
        {
            entry = (await _t3.EnrichAsync([entry], fullScan: false, ct).ConfigureAwait(false))[0];
        }

        return entry;
    }

    /// <summary>
    /// Scans one folder as an entry of its own, wherever it sits under <paramref name="root"/>: a top-level
    /// repository, or a working tree inside a worktree folder. Used to look at a project once more
    /// before it is removed. It is not put into any snapshot.
    /// </summary>
    public Task<DevEntry> ScanRepoAsync(string root, string path, ScanRequest request, CancellationToken ct) =>
        ScanEntryAsync(root, path, request, containerDepth: 0, ct);

    private static IEnumerable<string> TopLevel(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir)
                .Where(p => !Path.GetFileName(p).StartsWith('.'))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task<DevEntry> ScanEntryAsync(string root, string path, ScanRequest request, int containerDepth, CancellationToken ct)
    {
        var name = Path.GetFileName(path);
        if (File.Exists(path))
        {
            return new DevEntry
            {
                Root = root,
                Name = name,
                Path = path,
                Kind = EntryKind.LooseFile,
                LastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                Size = new SizeInfo { TotalBytes = new FileInfo(path).Length, MeasuredAt = _time.GetUtcNow() },
            };
        }

        var hasGit = Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));
        if (hasGit)
        {
            try
            {
                var git = await _repos.ScanAsync(path, request.AllowFetch, ct, request.ForcedFetch, request.JustFetched).ConfigureAwait(false);
                var stats = Walk(path, request);
                return new DevEntry
                {
                    Root = root,
                    Name = name,
                    Path = path,
                    Kind = EntryKind.GitRepo,
                    Git = git,
                    Description = DescriptionReader.Read(path),
                    LastModified = stats.LastModified,
                    Size = stats.Size,
                    Pins = _options.CurrentValue.Toolchains.Enabled ? Toolchains.PinReader.Read(path) : null,
                    Dependencies = _options.CurrentValue.Dependencies.Enabled ? _dependencies?.HealthOf(path) : null,
                };
            }
            catch (GitScanException ex)
            {
                _logger.LogWarning("Could not read {Path} as a git repo: {Error}", path, ex.Message);
                return Folder(root, path, request) with { Error = ex.Message };
            }
        }

        if (containerDepth > 0 && LooksLikeContainer(path))
        {
            var children = new List<DevEntry>();
            foreach (var child in TopLevel(path).Where(Directory.Exists))
            {
                children.Add(await ScanEntryAsync(root, child, request, containerDepth - 1, ct).ConfigureAwait(false));
            }

            DateTimeOffset?[] times = [.. children.Select(c => c.LastModified)];
            return new DevEntry
            {
                Root = root,
                Name = name,
                Path = path,
                Kind = EntryKind.WorktreeContainer,
                Children = children,
                Folder = Summarise(path),
                LastModified = times.Max() ?? new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero),
            };
        }

        return Folder(root, path, request);
    }

    private DevEntry Folder(string root, string path, ScanRequest request)
    {
        var stats = Walk(path, request);
        return new DevEntry
        {
            Root = root,
            Name = Path.GetFileName(path),
            Path = path,
            Kind = EntryKind.NonGitFolder,
            Description = DescriptionReader.Read(path),
            Folder = Summarise(path),
            LastModified = stats.LastModified ?? new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero),
            Size = stats.Size,
        };
    }

    private TreeStats Walk(string path, ScanRequest request)
    {
        var now = _time.GetUtcNow();
        var cached = _sizes.Get(path);
        var measure = request.Sizes switch
        {
            SizeMode.Force => true,
            SizeMode.WhenDue => cached is null || now - cached.MeasuredAt >= TimeSpan.FromHours(_options.CurrentValue.SizeScanIntervalHours),
            _ => false,
        };

        var stats = TreeWalker.Walk(path, measure, now);
        if (stats.Size is not null)
        {
            _sizes.Set(path, stats.Size);
            return stats;
        }

        return stats with { Size = WithoutDeletedDirs(path, cached) };
    }

    /// <summary>
    /// A cached size, less any build folder that has since been deleted (by hand, or by the finding's
    /// own <c>rm -rf</c>). Checking that a folder exists is cheap, unlike measuring it again, so a
    /// deletion shows on the next scan instead of when the size is next due.
    /// </summary>
    private SizeInfo? WithoutDeletedDirs(string path, SizeInfo? cached)
    {
        if (cached is null || cached.ReclaimableDirs.All(d => Directory.Exists(d.Path)))
        {
            return cached;
        }

        var kept = cached.ReclaimableDirs.Where(d => Directory.Exists(d.Path)).ToList();
        var removed = cached.ReclaimableBytes - kept.Sum(d => d.Bytes);
        var updated = cached with
        {
            ReclaimableDirs = kept,
            ReclaimableBytes = kept.Sum(d => d.Bytes),
            TotalBytes = Math.Max(0, cached.TotalBytes - removed),
        };
        _sizes.Set(path, updated);
        return updated;
    }

    /// <summary>
    /// A folder that is not a repo itself but holds working trees: named like <c>repo.worktrees</c>,
    /// or with at least one child that has a <c>.git</c> entry.
    /// </summary>
    private static bool LooksLikeContainer(string path)
    {
        if (DevDash.Core.RepoIdentity.IsWorktreeFolderName(Path.GetFileName(path)))
        {
            return true;
        }

        try
        {
            return Directory.EnumerateDirectories(path).Any(child =>
                Directory.Exists(Path.Combine(child, ".git")) || File.Exists(Path.Combine(child, ".git")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static FolderSummary? Summarise(string path)
    {
        try
        {
            var files = Directory.EnumerateFiles(path).Select(Path.GetFileName).Where(n => n is not null && !n.StartsWith('.')).ToList();
            var dirs = Directory.EnumerateDirectories(path).Select(Path.GetFileName).Where(n => n is not null && !n.StartsWith('.')).ToList();
            return new FolderSummary
            {
                FileCount = files.Count,
                DirectoryCount = dirs.Count,
                SampleNames = files.Concat(dirs).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(8).Select(n => n!).ToList(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
