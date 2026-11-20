using DevDash.Contracts;

namespace DevDash.Core;

public enum WorktreeStaleKind
{
    /// <summary>In use, locked, or simply not old enough to say.</summary>
    None,

    /// <summary>Git still lists it but its folder is gone.</summary>
    Prunable,

    /// <summary>Its branch has nothing that is not already in the default branch.</summary>
    Merged,

    /// <summary>Its branch's upstream was deleted on the remote.</summary>
    UpstreamGone,

    /// <summary>Nothing has happened in it for the configured number of days.</summary>
    Idle,
}

/// <summary>Whether a linked worktree looks finished with, and what that was read from.</summary>
/// <param name="LastActivity">The newest of the branch tip and the newest file, when either is known.</param>
/// <param name="Dirty">The scan saw uncommitted or untracked files in it.</param>
public sealed record WorktreeStaleness(WorktreeStaleKind Kind, BranchInfo? Branch, DateTimeOffset? LastActivity, bool Dirty)
{
    public bool IsStale => Kind != WorktreeStaleKind.None;

    /// <summary>A word or two for a chip: "merged", "idle 45 days".</summary>
    public string Label(DateTimeOffset now) => Kind switch
    {
        WorktreeStaleKind.Prunable => "folder missing",
        WorktreeStaleKind.Merged => "merged",
        WorktreeStaleKind.UpstreamGone => "branch deleted upstream",
        WorktreeStaleKind.Idle => LastActivity is { } last ? $"idle {(int)Math.Floor((now - last).TotalDays)} days" : "idle",
        _ => "",
    };
}

/// <summary>
/// The one definition of a stale linked worktree, shared by the cleanup rule that suggests removing it
/// and the repo lists that flag it.
/// </summary>
public static class WorktreeState
{
    /// <param name="repo">Any working tree of the repository: they all list the same branches and worktrees.</param>
    /// <param name="worktree">The linked worktree in question, from <paramref name="repo"/>'s list.</param>
    /// <param name="scanned">The worktree's own entry, when its folder is inside the scan roots.</param>
    public static WorktreeStaleness Classify(GitState repo, WorktreeInfo worktree, DevEntry? scanned, DateTimeOffset now, int staleDays)
    {
        var branch = worktree.Branch is null ? null : repo.Branches.FirstOrDefault(b => b.Name == worktree.Branch);
        var dirty = scanned?.Git is { } git && (git.IsDirty || git.Untracked > 0);

        // Idle: prefer the newest file mtime when the agent scanned the folder, else the branch tip date.
        var lastActivity = branch?.LastCommitDate;
        if (scanned?.LastModified is { } modified && (lastActivity is null || modified > lastActivity))
        {
            lastActivity = modified;
        }

        WorktreeStaleness Is(WorktreeStaleKind kind) => new(kind, branch, lastActivity, dirty);

        if (worktree.Prunable)
        {
            return Is(WorktreeStaleKind.Prunable);
        }

        if (worktree.Locked)
        {
            return Is(WorktreeStaleKind.None);
        }

        if (branch is { MergedIntoDefault: true } && branch.Name != repo.DefaultBranch)
        {
            return Is(WorktreeStaleKind.Merged);
        }

        if (branch is { UpstreamGone: true })
        {
            return Is(WorktreeStaleKind.UpstreamGone);
        }

        return Is(lastActivity is { } last && (now - last).TotalDays >= staleDays ? WorktreeStaleKind.Idle : WorktreeStaleKind.None);
    }

    /// <summary>
    /// The same, for a scanned linked worktree on its own: it finds itself in its repository's worktree
    /// list. Null for a main working tree, or when git's list does not have it.
    /// </summary>
    public static WorktreeStaleness? Of(DevEntry entry, DateTimeOffset now, int staleDays)
    {
        if (entry.Git is not { IsLinkedWorktree: true } git)
        {
            return null;
        }

        var linked = git.Worktrees.Skip(1).ToList();
        var self = linked.FirstOrDefault(w => RepoIdentity.SamePath(w.Path, entry.Path))
            ?? linked.FirstOrDefault(w => !w.Prunable && w.Head == git.HeadSha && w.Branch == git.Branch && linked.Count(o => o.Head == w.Head && o.Branch == w.Branch) == 1);
        return self is null ? null : Classify(git, self, entry, now, staleDays);
    }
}
