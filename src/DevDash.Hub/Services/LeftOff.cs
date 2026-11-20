using DevDash.Contracts;
using DevDash.Core.GitHub;

namespace DevDash.Hub.Services;

/// <summary>What made a working tree's "last touched" time.</summary>
public enum TouchKind
{
    Commit,
    Edit,
    T3,
}

/// <summary>When a working tree was last touched, and by what.</summary>
public sealed record Touch(DateTimeOffset At, TouchKind Kind);

/// <summary>The day groups of "Where I left off", in the order shown.</summary>
public enum LeftOffGroup
{
    Pinned,
    Today,
    Yesterday,
    ThisWeek,
    Earlier,
}

/// <summary>One project on "Where I left off": the working tree touched last, with what it takes to pick it up again.</summary>
public sealed record LeftOffItem
{
    public required RepoRow Row { get; init; }

    /// <summary>The working tree touched most recently, on whichever machine.</summary>
    public required CloneView WorkingTree { get; init; }

    /// <summary>Null for a pinned repo with nothing touched in the window.</summary>
    public Touch? Touched { get; init; }
    public LeftOffGroup Group { get; init; }
    public RepoNote Note { get; init; } = RepoNote.None("");
    public IReadOnlyList<RecentChange> RecentFiles { get; init; } = [];
    public int Uncommitted { get; init; }
    public int Untracked { get; init; }
    public int Stashes { get; init; }

    /// <summary>Commits on the checked-out branch that are on no remote: ahead of its upstream, or all of them without one.</summary>
    public int Unpushed { get; init; }
    public PullRequestInfo? PullRequest { get; init; }

    /// <summary>T3 threads on this working tree or its branch, newest first.</summary>
    public IReadOnlyList<T3Thread> Threads { get; init; } = [];

    /// <summary>The repo's other working trees touched in the window, newest first.</summary>
    public IReadOnlyList<(CloneView Clone, Touch Touched)> AlsoTouched { get; init; } = [];

    public string? Branch => WorkingTree.Git is { } git ? (git.Detached ? null : git.Branch) : null;
}

/// <summary>The settings of one "Where I left off" list.</summary>
public sealed record LeftOffQuery
{
    public const int DefaultDays = 14;

    public int Days { get; init; } = DefaultDays;
    public string? Tag { get; init; }

    /// <summary>Also list repos carrying a quiet tag (<c>archived</c>).</summary>
    public bool IncludeQuiet { get; init; }

    /// <summary>Most items; 0 for all.</summary>
    public int Limit { get; init; }
}

/// <summary>
/// "Where I left off": the projects touched most recently on any machine. Pure; the page and the
/// Overview card only render it. "Touched" is the newest of the last commit, the newest uncommitted
/// edit (tracked or untracked) and the newest T3 thread activity on the working tree.
/// </summary>
public static class LeftOff
{
    /// <summary>When a working tree was last touched, or null when nothing about it has a time.</summary>
    /// <param name="threads">The T3 threads on it (see <see cref="ThreadsOn"/>).</param>
    public static Touch? LastTouched(DevEntry entry, IEnumerable<T3Thread> threads)
    {
        Touch? best = null;

        void Consider(DateTimeOffset? at, TouchKind kind)
        {
            if (at is { } value && (best is null || value > best.At))
            {
                best = new Touch(value, kind);
            }
        }

        if (entry.Git is { } git)
        {
            Consider(git.LastCommit?.Date, TouchKind.Commit);
            Consider(git.NewestTrackedChangeAt, TouchKind.Edit);
            Consider(git.NewestUntrackedAt, TouchKind.Edit);
            foreach (var change in git.RecentChanges ?? [])
            {
                Consider(change.ModifiedAt, TouchKind.Edit);
            }
        }

        foreach (var thread in threads)
        {
            Consider(thread.UpdatedAt ?? thread.CreatedAt, TouchKind.T3);
        }

        return best;
    }

    /// <summary>
    /// The threads on one working tree: the threads its repo reports whose worktree is this folder, or,
    /// when a thread names no worktree, whose branch is the one checked out here.
    /// </summary>
    public static IReadOnlyList<T3Thread> ThreadsOn(CloneView clone, IEnumerable<T3Thread> repoThreads)
    {
        var branch = clone.Git is { Detached: false } git ? git.Branch : null;
        return repoThreads
            .Where(t => t.WorktreePath is { Length: > 0 } path
                ? Core.RepoIdentity.SamePath(path, clone.Entry.Path)
                : branch is not null && string.Equals(t.Branch, branch, StringComparison.Ordinal))
            .OrderByDescending(t => t.UpdatedAt ?? t.CreatedAt)
            .ToList();
    }

    /// <summary>Today, yesterday, this week (the five days before), or earlier, by the calendar in <paramref name="zone"/>.</summary>
    public static LeftOffGroup GroupOf(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var days = today.DayNumber - day.DayNumber;
        return days switch
        {
            <= 0 => LeftOffGroup.Today,
            1 => LeftOffGroup.Yesterday,
            < 7 => LeftOffGroup.ThisWeek,
            _ => LeftOffGroup.Earlier,
        };
    }

    public static string Label(LeftOffGroup group) => group switch
    {
        LeftOffGroup.Pinned => "Pinned",
        LeftOffGroup.Today => "Today",
        LeftOffGroup.Yesterday => "Yesterday",
        LeftOffGroup.ThisWeek => "This week",
        _ => "Earlier",
    };

    /// <summary>
    /// The list: pinned repos first (whenever they were last touched), then every other git repo touched
    /// in the last <see cref="LeftOffQuery.Days"/> days, most recent first. Repos with a quiet tag are
    /// left out unless asked for.
    /// </summary>
    public static IReadOnlyList<LeftOffItem> Build(IReadOnlyList<RepoRow> rows, RepoNoteSet notes, LeftOffQuery query, DateTimeOffset now, TimeZoneInfo zone)
    {
        var since = now.AddDays(-Math.Max(1, query.Days));
        var items = new List<LeftOffItem>();
        foreach (var row in rows)
        {
            if (row.Kind != EntryKind.GitRepo || !notes.Matches(row.Key, query.Tag) || (!query.IncludeQuiet && notes.IsQuiet(row.Key)))
            {
                continue;
            }

            var repoThreads = row.Clones
                .SelectMany(c => c.Entry.T3?.Threads ?? [])
                .DistinctBy(t => t.Id, StringComparer.Ordinal)
                .ToList();
            var touched = row.Clones
                .Where(c => c.Git is not null)
                .Select(c => (Clone: c, Threads: ThreadsOn(c, repoThreads), Touched: (Touch?)null))
                .Select(x => x with { Touched = LastTouched(x.Clone.Entry, x.Threads) })
                .OrderByDescending(x => x.Touched?.At ?? DateTimeOffset.MinValue)
                .ThenBy(x => x.Clone.IsLinkedWorktree)
                .ToList();
            if (touched.Count == 0)
            {
                continue;
            }

            var note = notes.For(row.Key);
            var top = touched[0];
            var recent = top.Touched is { } t && t.At >= since;
            if (!recent && !note.Pinned)
            {
                continue;
            }

            items.Add(Item(row, top.Clone, top.Threads, top.Touched, note, now, zone) with
            {
                AlsoTouched = touched.Skip(1).Where(x => x.Touched is { } at && at.At >= since).Select(x => (x.Clone, x.Touched!)).ToList(),
            });
        }

        var ordered = items
            .OrderBy(i => i.Group == LeftOffGroup.Pinned ? 0 : 1)
            .ThenByDescending(i => i.Touched?.At ?? DateTimeOffset.MinValue)
            .ThenBy(i => i.Row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Row.Key, StringComparer.Ordinal);
        return (query.Limit > 0 ? ordered.Take(query.Limit) : ordered).ToList();
    }

    private static LeftOffItem Item(RepoRow row, CloneView clone, IReadOnlyList<T3Thread> threads, Touch? touched, RepoNote note, DateTimeOffset now, TimeZoneInfo zone)
    {
        var git = clone.Git!;
        var branch = git.Detached ? null : git.Branch;
        var info = branch is null ? null : git.Branches.FirstOrDefault(b => b.Name == branch);
        // The agent's own count of commits on no remote when it sent one; else ahead of the upstream. With
        // neither (no upstream, an agent before 0.6.0) it is not known, and nothing is claimed.
        var unpushed = info?.UnpushedCount ?? (git.Upstream is not null ? git.Ahead : 0);

        var remoteBranch = info is not null
            ? BranchPullRequests.RemoteName(info, git.Remotes.Select(r => r.Name))
            : branch;
        var pr = branch is null ? null : BranchPullRequests.For(row.PullRequests, remoteBranch, git.HeadSha).FirstOrDefault()?.PullRequest;

        return new LeftOffItem
        {
            Row = row,
            WorkingTree = clone,
            Touched = touched,
            Group = note.Pinned ? LeftOffGroup.Pinned : GroupOf(touched!.At, now, zone),
            Note = note,
            RecentFiles = git.RecentChanges ?? [],
            Uncommitted = git.DirtyCount,
            Untracked = git.Untracked,
            Stashes = git.Stashes.Count,
            Unpushed = unpushed,
            PullRequest = pr,
            Threads = threads,
        };
    }

    /// <summary>A link to the branch on GitHub when the repo is there and the branch has a name, else the repo.</summary>
    public static string? GitHubUrl(LeftOffItem item)
    {
        if (item.Row.GitHub is not { } gh)
        {
            return null;
        }

        if (item.PullRequest is { Url.Length: > 0 } pr)
        {
            return pr.Url;
        }

        var git = item.WorkingTree.Git;
        var info = item.Branch is { } b ? git?.Branches.FirstOrDefault(x => x.Name == b) : null;
        var onRemote = info?.Upstream is not null && !info.UpstreamGone;
        return onRemote ? $"{gh.Url}/tree/{Uri.EscapeDataString(BranchPullRequests.RemoteName(info!, git!.Remotes.Select(r => r.Name))).Replace("%2F", "/")}" : gh.Url;
    }
}
