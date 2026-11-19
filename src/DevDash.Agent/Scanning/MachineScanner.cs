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

public sealed record ScanRequest(bool AllowFetch, SizeMode Sizes);

/// <summary>Scans the configured roots into a <see cref="MachineSnapshot"/>. Reads only, apart from optional fetches.</summary>
public sealed class MachineScanner
{
    private readonly RepoScanner _repos;
    private readonly SizeCache _sizes;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<MachineScanner> _logger;
    private readonly TimeProvider _time;

    public MachineScanner(RepoScanner repos, SizeCache sizes, IOptionsMonitor<AgentOptions> options, ILogger<MachineScanner> logger, TimeProvider? time = null)
    {
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

        return new MachineSnapshot
        {
            Machine = machine,
            ScannedAt = _time.GetUtcNow(),
            ScanDurationMs = stopwatch.ElapsedMilliseconds,
            Roots = roots,
            Entries = WithOneGraphPerRepository(entries.OrderBy(e => e.Root, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList()),
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
        void Collect(IEnumerable<DevEntry> list)
        {
            foreach (var entry in list)
            {
                if (entry.Git is { IsLinkedWorktree: false, Graph: not null, CommonDir: { } dir })
                {
                    carried.Add(dir);
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
                git = git with { Graph = null };
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

        return await ScanEntryAsync(root, path, request, _options.CurrentValue.WorktreeContainerDepth, ct).ConfigureAwait(false);
    }

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
                var git = await _repos.ScanAsync(path, request.AllowFetch, ct).ConfigureAwait(false);
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
