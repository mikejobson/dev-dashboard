using System.Globalization;
using DevDash.Agent.Git;
using DevDash.Contracts;
using DevDash.Core.Git;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Scanning;

/// <summary>Collects the git state of one working tree using read-only git commands.</summary>
public sealed class RepoScanner
{
    private const char Sep = '\u001f';
    private const int MaxStatPaths = 2000;

    private readonly GitRunner _git;
    private readonly FetchService _fetch;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly CommitGraphCollector _graphs;

    public RepoScanner(GitRunner git, FetchService fetch, IOptionsMonitor<AgentOptions> options)
    {
        _git = git;
        _graphs = new CommitGraphCollector(git);
        _fetch = fetch;
        _options = options;
    }

    /// <summary>Returns the git state, or throws <see cref="GitScanException"/> when the folder cannot be read as a repo.</summary>
    public async Task<GitState> ScanAsync(string path, bool allowFetch, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(_options.CurrentValue.GitTimeoutSeconds);
        Task<GitResult> Run(params string[] args) => _git.RunAsync(path, args, timeout, ct);

        var dirs = await Run("rev-parse", "--path-format=absolute", "--git-common-dir", "--git-dir").ConfigureAwait(false);
        if (!dirs.Ok)
        {
            throw new GitScanException(dirs.StdErr.Trim());
        }

        var dirLines = dirs.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var commonDir = dirLines.Length > 0 ? dirLines[0] : Path.Combine(path, ".git");
        var gitDir = dirLines.Length > 1 ? dirLines[1] : commonDir;

        var remotes = RemoteListParser.Parse((await Run("remote", "-v").ConfigureAwait(false)).StdOut);

        if (allowFetch)
        {
            await _fetch.FetchIfDueAsync(path, commonDir, remotes.Count > 0, ct).ConfigureAwait(false);
        }

        var statusResult = await Run("status", "--porcelain=v2", "--branch").ConfigureAwait(false);
        if (!statusResult.Ok)
        {
            throw new GitScanException(statusResult.StdErr.Trim());
        }

        var status = StatusParser.Parse(statusResult.StdOut);

        CommitInfo? lastCommit = null;
        var log = await Run("log", "-1", "--format=%H%x1f%cI%x1f%an%x1f%s").ConfigureAwait(false);
        if (log.Ok)
        {
            var f = log.StdOut.Trim().Split(Sep, 4);
            if (f.Length == 4 && DateTimeOffset.TryParse(f[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                lastCommit = new CommitInfo { Sha = f[0], Date = date, Author = f[2], Subject = f[3] };
            }
        }

        var branches = ForEachRefParser.Parse(
            (await Run("for-each-ref", "--format=" + ForEachRefParser.Format, "refs/heads").ConfigureAwait(false)).StdOut);

        // Default branch: what origin/HEAD points at, else a conventional local name.
        string? defaultBranch = null;
        string? mergeTarget = null;
        var originHead = await Run("symbolic-ref", "--short", "refs/remotes/origin/HEAD").ConfigureAwait(false);
        if (originHead.Ok && originHead.StdOut.Trim() is { Length: > 0 } remoteHead)
        {
            mergeTarget = remoteHead;
            var slash = remoteHead.IndexOf('/');
            defaultBranch = slash >= 0 ? remoteHead[(slash + 1)..] : remoteHead;
        }
        else
        {
            defaultBranch = new[] { "main", "master", "develop", "trunk" }
                .FirstOrDefault(name => branches.Any(b => b.Name == name));
            mergeTarget = defaultBranch;
        }

        if (mergeTarget is not null)
        {
            var merged = await Run("for-each-ref", "--merged", mergeTarget, "--format=%(refname:short)", "refs/heads").ConfigureAwait(false);
            if (merged.Ok)
            {
                var names = ForEachRefParser.ParseNames(merged.StdOut);
                branches = branches.Select(b => b with { MergedIntoDefault = names.Contains(b.Name) }).ToList();
            }
        }

        var worktrees = WorktreeListParser.Parse((await Run("worktree", "list", "--porcelain").ConfigureAwait(false)).StdOut);
        var stashes = StashListParser.Parse((await Run("stash", "list", "--format=" + StashListParser.Format).ConfigureAwait(false)).StdOut);

        // The commit each stash was made on, so the branch map can pin it to a commit.
        if (stashes.Count > 0)
        {
            var bases = await Run("stash", "list", "--format=%H%x1f%P").ConfigureAwait(false);
            if (bases.Ok)
            {
                var baseOf = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in bases.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = line.TrimEnd('\r').Split(Sep);
                    if (f.Length == 2 && f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..])
                    {
                        baseOf[f[0]] = first;
                    }
                }

                stashes = stashes.Select(s => s.Sha is not null && baseOf.TryGetValue(s.Sha, out var b) ? s with { BaseSha = b } : s).ToList();
            }
        }

        var graphTrunk = await GraphTrunkAsync(args => Run(args), mergeTarget, defaultBranch).ConfigureAwait(false);

        var isLinked = !PathsEqual(commonDir, gitDir);
        // Linked worktrees get the repository's graph too (it is cached, so that costs one for-each-ref);
        // MachineScanner drops the copies when the main working tree is in the same snapshot.
        var graph = await _graphs.CollectAsync(
            path,
            commonDir,
            new GraphRequest
            {
                Branches = branches,
                TrunkRef = graphTrunk,
                ExtraTips =
                [
                    .. worktrees.Where(w => !w.Prunable && w.Head is not null).Select(w => w.Head!),
                    .. stashes.Where(s => s.BaseSha is not null).Select(s => s.BaseSha!),
                ],
                HasRemotes = remotes.Count > 0,
            },
            timeout,
            ct).ConfigureAwait(false);

        var (trackedAt, untrackedAt) = NewestChange(path, status.Paths);

        return new GitState
        {
            Branch = status.Branch,
            Detached = status.Detached,
            HeadSha = status.HeadSha,
            Upstream = status.Upstream,
            UpstreamGone = status.UpstreamGone,
            Ahead = status.Ahead,
            Behind = status.Behind,
            Staged = status.Staged,
            Unstaged = status.Unstaged,
            Untracked = status.Untracked,
            Conflicted = status.Conflicted,
            NewestTrackedChangeAt = trackedAt,
            NewestUntrackedAt = untrackedAt,
            LastCommit = lastCommit,
            DefaultBranch = defaultBranch,
            IsLinkedWorktree = isLinked,
            CommonDir = commonDir,
            LastFetchAt = FetchService.LastFetch(commonDir),
            Remotes = remotes,
            Branches = branches,
            Worktrees = worktrees,
            Stashes = stashes,
            Graph = graph,
        };
    }

    private static (DateTimeOffset? Tracked, DateTimeOffset? Untracked) NewestChange(string repo, IReadOnlyList<ChangedPath> paths)
    {
        DateTimeOffset? tracked = null, untracked = null;
        foreach (var changed in paths.Take(MaxStatPaths))
        {
            DateTime time;
            try
            {
                var full = Path.Combine(repo, changed.Path.TrimEnd('/'));
                if (File.Exists(full))
                {
                    time = File.GetLastWriteTimeUtc(full);
                }
                else if (Directory.Exists(full))
                {
                    time = Directory.GetLastWriteTimeUtc(full);
                }
                else
                {
                    continue; // deleted file
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            var value = new DateTimeOffset(time, TimeSpan.Zero);
            if (changed.Tracked)
            {
                tracked = tracked is null || value > tracked ? value : tracked;
            }
            else
            {
                untracked = untracked is null || value > untracked ? value : untracked;
            }
        }

        return (tracked, untracked);
    }

    /// <summary>
    /// The line the branch map is drawn along. Usually the default branch, but in a git-flow repository
    /// (default <c>main</c>, day-to-day work merged into <c>develop</c>) that would put all of develop
    /// "off the main line". So when a develop branch exists and has newer commits than the default
    /// branch, it is the map's trunk instead. "Merged into default" for cleanup is not affected.
    /// </summary>
    internal static async Task<string?> GraphTrunkAsync(Func<string[], Task<GitResult>> run, string? mergeTarget, string? defaultBranch)
    {
        if (mergeTarget is null || defaultBranch is "develop" or "development")
        {
            return mergeTarget;
        }

        var slash = mergeTarget.IndexOf('/');
        var remote = mergeTarget == defaultBranch || slash < 0 ? null : mergeTarget[..slash];
        foreach (var name in (string[])["develop", "development"])
        {
            var candidate = remote is null ? name : $"{remote}/{name}";
            var refName = remote is null ? $"refs/heads/{name}" : $"refs/remotes/{candidate}";
            if (!(await run(["rev-parse", "--verify", "--quiet", refName]).ConfigureAwait(false)).Ok)
            {
                continue;
            }

            var mine = await TipTimeAsync(run, candidate).ConfigureAwait(false);
            var theirs = await TipTimeAsync(run, mergeTarget).ConfigureAwait(false);
            return mine is { } m && theirs is { } t && m > t ? candidate : mergeTarget;
        }

        return mergeTarget;
    }

    private static async Task<long?> TipTimeAsync(Func<string[], Task<GitResult>> run, string rev)
    {
        var result = await run(["log", "-1", "--format=%ct", rev]).ConfigureAwait(false);
        return result.Ok && long.TryParse(result.StdOut.Trim(), out var t) ? t : null;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}

public sealed class GitScanException(string message) : Exception(message);
