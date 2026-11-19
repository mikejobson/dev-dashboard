using DevDash.Contracts;
using DevDash.Core.GitHub;

namespace DevDash.Core.Cleanup;

public sealed record CleanupOptions
{
    public int UncommittedDays { get; init; } = 14;
    public int StashDays { get; init; } = 30;
    public int WorktreeStaleDays { get; init; } = 30;
    public long BuildDirBytes { get; init; } = 500L * 1024 * 1024;
    public int UntouchedDays { get; init; } = 180;
    public int StalePullRequestDays { get; init; } = 30;
    public int StaleDraftDays { get; init; } = 14;
}

/// <summary>A git working tree on one machine.</summary>
public sealed record LocalClone(MachineSnapshot Snapshot, DevEntry Entry)
{
    public MachineInfo Machine => Snapshot.Machine;
    public GitState Git => Entry.Git!;
    public string RepoKey => RepoIdentity.KeyFor(Machine.MachineId, Entry);
}

/// <summary>Input to the rules: every machine's latest snapshot plus what GitHub says.</summary>
public sealed record CleanupContext
{
    public IReadOnlyList<MachineSnapshot> Machines { get; init; } = [];
    public IReadOnlyDictionary<string, RemoteRepoLookup> RemoteRepos { get; init; } = new Dictionary<string, RemoteRepoLookup>();
    public IReadOnlyList<PullRequestInfo> PullRequests { get; init; } = [];
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
    public CleanupOptions Options { get; init; } = new();

    /// <summary>Every git working tree, including linked worktrees and worktree-container children.</summary>
    public IEnumerable<LocalClone> Clones() =>
        from snapshot in Machines
        from entry in RepoIdentity.Flatten(snapshot.Entries)
        where entry.Kind == EntryKind.GitRepo && entry.Git is not null
        select new LocalClone(snapshot, entry);

    /// <summary>
    /// One clone per repository (shared .git directory) per machine, preferring the main working tree.
    /// Use for anything stored per repository rather than per working tree: branches, stashes, worktrees.
    /// </summary>
    public IEnumerable<LocalClone> Repositories() =>
        Clones()
            .GroupBy(c => (c.Machine.MachineId, Dir: c.Git.CommonDir ?? c.Entry.Path))
            .Select(g => g.OrderBy(c => c.Git.IsLinkedWorktree).First());
}

public interface ICleanupRule
{
    string Id { get; }
    /// <summary>The group heading with the default thresholds. Prefer <see cref="TitleFor"/> when the options are at hand.</summary>
    string Title { get; }

    /// <summary>The group heading, naming the thresholds that are actually configured.</summary>
    string TitleFor(CleanupOptions options) => Title;
    string Description { get; }
    IEnumerable<Finding> Evaluate(CleanupContext context);
}
