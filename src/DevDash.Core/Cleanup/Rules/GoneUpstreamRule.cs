using DevDash.Contracts;
using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>Branches tracking a remote branch that has been deleted (usually after a PR merge).</summary>
public sealed class GoneUpstreamRule : ICleanupRule
{
    public string Id => "gone-upstream";
    public string Title => "Branches whose upstream is gone";
    public string Description => "The remote branch was deleted, typically after its PR merged. The local branch is left behind.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        foreach (var repo in context.Repositories())
        {
            var git = repo.Git;
            var checkedOut = CheckedOutBranches(git);
            var path = MainPath(repo);

            foreach (var branch in git.Branches.Where(b => b.UpstreamGone))
            {
                var isCheckedOut = checkedOut.Contains(branch.Name);
                var merged = branch.MergedIntoDefault == true;
                var explanation = $"{branch.Upstream} no longer exists on the remote. "
                    + (merged
                        ? $"The branch is merged into {git.DefaultBranch}, so deleting it loses nothing."
                        : "It is not merged into the default branch locally (a squash merge looks like this too), so check before forcing the delete.")
                    + (isCheckedOut ? " It is currently checked out; switch away first." : "");

                yield return this.ForClone(repo,
                    merged ? FindingSeverity.Low : FindingSeverity.Medium,
                    $"{repo.Entry.Name}: upstream gone for {branch.Name}",
                    explanation,
                    [
                        Inspect("Recent commits on the branch", GitCmd(path, $"log --oneline -10 {Quote(branch.Name)}")),
                        .. !merged && git.DefaultBranch is { } target && target != branch.Name
                            ? [Inspect($"Commits not in {target}", GitCmd(path, "log --oneline " + Range(target, branch.Name)))]
                            : Array.Empty<FindingCommand>(),
                        merged
                            // Only the safe delete is ever offered to an agent, and not for a branch that is checked out.
                            ? Fix("Delete the merged branch", GitCmd(path, $"branch -d {Quote(branch.Name)}"),
                                isCheckedOut ? null : AgentAction.DeleteBranches(path, [branch.Name]))
                            : Fix("Force-delete (loses any commits not merged elsewhere)", GitCmd(path, $"branch -D {Quote(branch.Name)}")),
                    ],
                    branch.Name);
            }
        }
    }
}
