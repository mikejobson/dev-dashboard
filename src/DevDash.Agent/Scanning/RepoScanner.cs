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

    public RepoScanner(GitRunner git, FetchService fetch, IOptionsMonitor<AgentOptions> options)
    {
        _git = git;
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
            IsLinkedWorktree = !PathsEqual(commonDir, gitDir),
            CommonDir = commonDir,
            LastFetchAt = FetchService.LastFetch(commonDir),
            Remotes = remotes,
            Branches = branches,
            Worktrees = worktrees,
            Stashes = stashes,
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

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}

public sealed class GitScanException(string message) : Exception(message);
