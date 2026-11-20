using DevDash.Contracts;
using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>Worktrees that git can prune, whose branch is finished, or that nobody has touched for a while.</summary>
public sealed class StaleWorktreeRule : ICleanupRule
{
    public string Id => "stale-worktree";
    public string Title => "Stale or prunable worktrees";
    public string Description => "Worktrees whose folder is gone, whose branch is merged or deleted upstream, or that have sat idle; plus leftovers in worktree folders.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        var now = context.Now;
        var staleDays = context.Options.WorktreeStaleDays;

        foreach (var repo in context.Repositories())
        {
            var git = repo.Git;
            if (git.Worktrees.Count < 2)
            {
                continue;
            }

            var mainPath = git.Worktrees[0].Path;
            var scanned = context.Clones()
                .Where(c => c.Machine.MachineId == repo.Machine.MachineId)
                .GroupBy(c => c.Entry.Path, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Entry, StringComparer.Ordinal);

            foreach (var wt in git.Worktrees.Skip(1))
            {
                scanned.TryGetValue(wt.Path, out var entry);

                // What makes a worktree stale is defined once, in WorktreeState; this rule adds the commands.
                var state = WorktreeState.Classify(git, wt, entry, now, staleDays);
                if (state.Kind == WorktreeStaleKind.Prunable)
                {
                    yield return this.ForClone(repo, FindingSeverity.Medium,
                        $"Prunable worktree: {wt.Path}",
                        $"Git still lists this worktree of {repo.Entry.Name} but its folder is missing"
                        + (wt.PrunableReason is null ? "." : $" ({wt.PrunableReason}).")
                        + " Pruning only removes the stale bookkeeping.",
                        [
                            Inspect("What would be pruned", GitCmd(mainPath, "worktree prune --dry-run --verbose")),
                            Fix("Prune stale worktree records", GitCmd(mainPath, "worktree prune"), AgentAction.PruneWorktrees(mainPath)),
                        ],
                        "prunable", wt.Path);
                    continue;
                }

                if (!state.IsStale)
                {
                    continue;
                }

                var branch = state.Branch;
                var dirty = state.Dirty;

                // Look inside the worktree first. Removal is offered to the agent only for a worktree on a
                // branch (the commits stay on the branch) that the scan did not see as dirty; git checks again.
                FindingCommand[] remove =
                [
                    Inspect("Uncommitted work in the worktree", GitCmd(wt.Path, "status --short --branch")),
                    Inspect("Its recent commits", GitCmd(wt.Path, "log --oneline -5")),
                    Fix("Remove the worktree", GitCmd(mainPath, $"worktree remove {Quote(wt.Path)}"),
                        wt.Branch is not null && !dirty ? AgentAction.RemoveWorktree(mainPath, wt.Path) : null),
                ];

                if (state.Kind == WorktreeStaleKind.Merged)
                {
                    // A branch with no commits of its own also counts as merged, so uncommitted work
                    // in the worktree means it may simply be work that has not been committed yet.
                    yield return this.ForClone(repo, dirty ? FindingSeverity.Low : FindingSeverity.Medium,
                        $"Worktree for merged branch {branch!.Name}",
                        $"{wt.Path} has {branch.Name} checked out, which has nothing that is not already in {git.DefaultBranch}."
                        + (dirty ? " It has uncommitted changes, so look before removing it." : ""),
                        remove, "merged", wt.Path).ActiveAt(Latest(branch.LastCommitDate, entry?.LastModified));
                }
                else if (state.Kind == WorktreeStaleKind.UpstreamGone)
                {
                    yield return this.ForClone(repo, FindingSeverity.Medium,
                        $"Worktree for deleted branch {branch!.Name}",
                        $"{wt.Path} has {branch.Name} checked out, and its upstream branch has been deleted on the remote.",
                        remove, "gone", wt.Path).ActiveAt(Latest(branch.LastCommitDate, entry?.LastModified));
                }
                else if (state.LastActivity is { } last)
                {
                    yield return this.ForClone(repo, FindingSeverity.Low,
                        $"Idle worktree: {wt.Path}",
                        $"No activity for {DaysSince(now, last)} days"
                        + (wt.Branch is null ? " (detached HEAD)." : $" on {wt.Branch}.")
                        + " Remove it if the work is finished; git refuses if it has uncommitted changes.",
                        remove, "idle", wt.Path).ActiveAt(last);
                }
            }
        }

        // Worktree folders: empty ones, and children that are no longer worktrees.
        foreach (var snapshot in context.Machines)
        {
            foreach (var container in snapshot.Entries.Where(e => e.Kind == EntryKind.WorktreeContainer && RepoIdentity.IsWorktreeFolderName(e.Name)))
            {
                if (container.Children.Count == 0)
                {
                    yield return this.ForEntry(snapshot, container, FindingSeverity.Low,
                        $"Empty worktree folder: {container.Name}",
                        "This worktree folder has nothing left in it.",
                        [
                            Inspect("Check it is empty", $"ls -la {Quote(container.Path)}"),
                            Fix("Remove the empty folder", $"rmdir {Quote(container.Path)}"),
                        ],
                        "empty");
                    continue;
                }

                foreach (var child in container.Children.Where(c => c.Kind == EntryKind.NonGitFolder))
                {
                    yield return this.ForEntry(snapshot, child, FindingSeverity.Medium,
                        $"Orphaned worktree folder: {container.Name}/{child.Name}",
                        "This folder sits in a worktree folder but has no .git link, so git no longer knows about it. "
                        + "Check it for anything you still need before deleting it.",
                        [
                            Inspect("List the folder", $"ls -la {Quote(child.Path)}"),
                            Fix("Delete the folder", $"rm -rf {Quote(child.Path)}"),
                        ],
                        "orphan");
                }
            }
        }
    }
}
