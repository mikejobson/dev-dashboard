using DevDash.Contracts;
using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>Work that exists only on one machine: no remote, no upstream, or commits ahead of upstream.</summary>
public sealed class UnpushedCommitsRule : ICleanupRule
{
    public string Id => "unpushed";
    public string Title => "Unpushed commits / no upstream";
    public string Description => "Commits that exist only on this machine. A lost or wiped disk loses them.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        foreach (var repo in context.Repositories())
        {
            var git = repo.Git;
            var path = MainPath(repo);

            if (git.Remotes.Count == 0)
            {
                if (git.LastCommit is not null)
                {
                    yield return this.ForClone(repo, FindingSeverity.Medium,
                        $"{repo.Entry.Name}: no remote",
                        $"This repo has {Plural(git.Branches.Count, "local branch", "local branches")} and no remote, so its history exists only on {repo.Machine.Hostname}.",
                        [
                            Inspect("Recent commits", GitCmd(path, "log --oneline -10")),
                            Fix("Create a private GitHub repo and push", $"gh repo create --private --source {Quote(path)} --push"),
                        ],
                        "no-remote");
                }

                continue;
            }

            var remote = RepoIdentity.PrimaryRemote(git)?.Name ?? git.Remotes[0].Name;
            foreach (var branch in git.Branches)
            {
                if (branch.Upstream is not null && !branch.UpstreamGone && branch.Ahead > 0)
                {
                    yield return this.ForClone(repo, FindingSeverity.Medium,
                        $"{repo.Entry.Name}: {Plural(branch.Ahead, "unpushed commit")} on {branch.Name}",
                        $"{branch.Name} is {branch.Ahead} ahead of {branch.Upstream}"
                        + (branch.Behind > 0 ? $" and {branch.Behind} behind; it needs a pull or rebase before pushing." : "."),
                        AheadCommands(git, path, remote, branch),
                        "ahead", branch.Name);
                }
                else if (branch.Upstream is null && branch.MergedIntoDefault != true && branch.Name != git.DefaultBranch)
                {
                    yield return this.ForClone(repo, FindingSeverity.Low,
                        $"{repo.Entry.Name}: {branch.Name} has no upstream",
                        $"{branch.Name} has never been pushed and has commits that are not in {git.DefaultBranch ?? "the default branch"}.",
                        [
                            git.DefaultBranch is { } target
                                ? Inspect($"Commits not in {target}", GitCmd(path, "log --oneline " + Range(target, branch.Name)))
                                : Inspect("Recent commits", GitCmd(path, $"log --oneline -10 {Quote(branch.Name)}")),
                            Fix($"Push and track {remote}/{branch.Name}", GitCmd(path, $"push -u {Quote(remote)} {Quote(branch.Name)}"),
                                AgentAction.Push(path, remote, branch.Name, setUpstream: true)),
                        ],
                        "no-upstream", branch.Name);
                }
            }
        }
    }

    /// <summary>
    /// A branch that is only ahead can be pushed. One that is also behind would have its push rejected,
    /// so it has to be rebased onto its upstream first, which needs the branch checked out.
    /// </summary>
    internal static IReadOnlyList<FindingCommand> AheadCommands(GitState git, string path, string fallbackRemote, BranchInfo branch)
    {
        var upstream = branch.Upstream!;
        var target = SplitUpstream(git, upstream);
        var remote = target?.Remote ?? fallbackRemote;
        var sameName = target is null || target.Value.Branch == branch.Name;
        var refspec = sameName ? branch.Name : $"{branch.Name}:{target!.Value.Branch}";

        // The agent's push is same-name only, so a branch tracking a differently named one is copy-only.
        var canPushByAgent = target is not null && sameName;

        if (branch.Behind == 0)
        {
            return
            [
                Inspect($"Commits not on {upstream}", GitCmd(path, "log --oneline " + Range(upstream, branch.Name))),
                Fix($"Push to {remote}", GitCmd(path, $"push {Quote(remote)} {Quote(refspec)}"),
                    canPushByAgent ? AgentAction.Push(path, remote, branch.Name) : null),
            ];
        }

        // Rebase where the branch is checked out. If it is not checked out anywhere, the main working
        // tree has to switch to it first (git refuses the switch if local changes would be overwritten).
        var checkedOutAt = WorktreeOf(git, branch.Name);
        var at = checkedOutAt ?? path;
        return
        [
            Inspect("Refresh from the remote", GitCmd(path, "fetch --prune"), AgentAction.Fetch(path)),
            Inspect($"Compare with {upstream} (< theirs, > yours)", GitCmd(path, "log --oneline --left-right " + Range(upstream, branch.Name, bothSides: true))),
            checkedOutAt is not null
                ? Fix($"Rebase onto {upstream}", GitCmd(at, "pull --rebase"))
                : Fix($"Switch to {branch.Name} and rebase onto {upstream}", GitCmd(at, $"switch {Quote(branch.Name)}") + " && " + GitCmd(at, "pull --rebase")),
            Fix($"Then push to {remote}", GitCmd(at, $"push {Quote(remote)} {Quote(refspec)}")),
        ];
    }
}
