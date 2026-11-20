using DevDash.Contracts;

namespace DevDash.Hub.Services;

public enum SyncKind
{
    /// <summary>The branch is ahead of its remote branch and not behind: a plain push.</summary>
    Push,

    /// <summary>The branch is behind its remote branch and not ahead: a fast-forward.</summary>
    Pull,
}

/// <summary>One push or fast-forward the dashboard can offer for a branch on a machine.</summary>
/// <param name="Count">How many commits it moves.</param>
/// <param name="Blocked">Why it cannot be done as things stand (the branch's working tree has uncommitted changes). Null when it can.</param>
public sealed record SyncOffer(MachineView Machine, string RepoPath, string Branch, string Remote, SyncKind Kind, int Count, AgentAction Action, string? Blocked)
{
    /// <summary>"Push main from work-mac" / "Pull main on work-mac".</summary>
    public string Label => Kind == SyncKind.Push ? $"Push {Branch} from {Machine.Machine.Hostname}" : $"Pull {Branch} on {Machine.Machine.Hostname}";

    public string What => Kind == SyncKind.Push
        ? $"Push {Count} commit{(Count == 1 ? "" : "s")} on {Branch} to {Remote}, never forced"
        : $"Fast-forward {Branch} by {Count} commit{(Count == 1 ? "" : "s")} from {Remote}";
}

/// <summary>
/// Works out where a push or a fast-forward pull would bring a device level with the remote, from the
/// snapshots alone. Pure. Only the plain cases are offered: a branch that tracks the branch of the same
/// name, and is either ahead or behind, never both. Anything else needs a person.
/// </summary>
public static class SyncOffers
{
    public static IReadOnlyList<SyncOffer> For(IEnumerable<CloneView> clones)
    {
        var all = clones.Where(c => c.Git is not null).ToList();
        var offers = new List<SyncOffer>();

        // One working tree per repository per machine: branches are shared by its linked worktrees. The
        // main working tree when it is scanned, because that is the path the agent is sure to accept.
        foreach (var repo in all.GroupBy(c => (c.Machine.MachineId, c.Git!.CommonDir ?? c.Entry.Path)).Select(g => g.OrderBy(c => c.Git!.IsLinkedWorktree).First()))
        {
            var git = repo.Git!;
            foreach (var branch in git.Branches.OrderBy(b => b.Name, StringComparer.Ordinal))
            {
                if (branch.UpstreamGone || Split(git, branch.Upstream) is not { } upstream || upstream.Branch != branch.Name)
                {
                    continue;
                }

                if (branch is { Ahead: > 0, Behind: 0 })
                {
                    offers.Add(new SyncOffer(repo.Machine, repo.Entry.Path, branch.Name, upstream.Remote, SyncKind.Push, branch.Ahead,
                        AgentAction.Push(repo.Entry.Path, upstream.Remote, branch.Name), null));
                }
                else if (branch is { Behind: > 0, Ahead: 0 })
                {
                    offers.Add(new SyncOffer(repo.Machine, repo.Entry.Path, branch.Name, upstream.Remote, SyncKind.Pull, branch.Behind,
                        AgentAction.PullFastForward(repo.Entry.Path, upstream.Remote, branch.Name), DirtyWhere(all, repo, branch.Name)));
                }
            }
        }

        return offers;
    }

    /// <summary>Where the branch is checked out with uncommitted changes, as a sentence. Null when it is not, or that working tree is clean.</summary>
    private static string? DirtyWhere(List<CloneView> all, CloneView repo, string branch)
    {
        if (repo.Git!.Worktrees.FirstOrDefault(w => w.Branch == branch && !w.Prunable && !w.Bare) is not { } worktree)
        {
            return null;
        }

        var tree = all.FirstOrDefault(c => c.Machine.MachineId == repo.Machine.MachineId && DevDash.Core.RepoIdentity.SamePath(c.Entry.Path, worktree.Path));
        return tree?.Git is { IsDirty: true } dirty
            ? $"{branch} is checked out at {tree.Entry.Path} with {dirty.DirtyCount} uncommitted change{(dirty.DirtyCount == 1 ? "" : "s")}. Commit or stash them first."
            : null;
    }

    /// <summary>Splits <c>origin/feature/x</c> into remote and branch, using the repo's own remote names.</summary>
    private static (string Remote, string Branch)? Split(GitState git, string? upstream)
    {
        if (upstream is null)
        {
            return null;
        }

        foreach (var remote in git.Remotes.Select(r => r.Name).Distinct(StringComparer.Ordinal).OrderByDescending(n => n.Length))
        {
            if (upstream.Length > remote.Length + 1 && upstream.StartsWith(remote + "/", StringComparison.Ordinal))
            {
                return (remote, upstream[(remote.Length + 1)..]);
            }
        }

        return null;
    }
}
