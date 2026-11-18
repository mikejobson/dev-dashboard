using DevDash.Contracts;
using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>Local branches already merged into the default branch.</summary>
public sealed class MergedBranchRule : ICleanupRule
{
    public string Id => "merged-branch";
    public string Title => "Local branches merged into the default branch";
    public string Description => "Branches whose commits are all in the default branch. Safe to delete with `git branch -d`.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        foreach (var repo in context.Repositories())
        {
            var git = repo.Git;
            if (git.DefaultBranch is null)
            {
                continue;
            }

            var checkedOut = CheckedOutBranches(git);
            var merged = git.Branches
                .Where(b => b.MergedIntoDefault == true
                    && b.Name != git.DefaultBranch
                    && !b.UpstreamGone // reported by GoneUpstreamRule
                    && !checkedOut.Contains(b.Name))
                .Select(b => b.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            if (merged.Count == 0)
            {
                continue;
            }

            var path = MainPath(repo);
            var names = string.Join(' ', merged.Select(Quote));
            yield return this.ForClone(repo, FindingSeverity.Low,
                $"{repo.Entry.Name}: {Plural(merged.Count, "branch", "branches")} merged into {git.DefaultBranch}",
                $"Already merged into {git.DefaultBranch}: {string.Join(", ", merged)}.",
                [
                    Inspect(merged.Count == 1 ? "Last commit on the branch" : "Last commit on each branch", GitCmd(path, "log --no-walk --oneline --decorate " + names)),
                    Fix(merged.Count == 1 ? "Delete the merged branch" : "Delete the merged branches", GitCmd(path, "branch -d " + names),
                        merged.Count <= AgentActions.MaxBranches ? AgentAction.DeleteBranches(path, merged) : null),
                ],
                [.. merged]);
        }
    }
}
