using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>Working trees with modifications nobody has touched for a while.</summary>
public sealed class OldUncommittedChangesRule : ICleanupRule
{
    public string Id => "old-uncommitted";
    public string Title => "Uncommitted changes older than N days";
    public string Description => "Modified or untracked files that have been sitting in a working tree. Commit, stash or discard them.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        var limit = context.Options.UncommittedDays;
        foreach (var clone in context.Clones())
        {
            var git = clone.Git;
            if (git.IsDirty && git.NewestTrackedChangeAt is { } tracked)
            {
                var days = DaysSince(context.Now, tracked);
                if (days >= limit)
                {
                    yield return this.ForClone(clone, days >= limit * 4 ? FindingSeverity.High : FindingSeverity.Medium,
                        $"{clone.Entry.Name}: uncommitted changes for {days} days",
                        $"{Plural(git.DirtyCount, "changed file")} on {git.Branch ?? "a detached HEAD"}, last edited {days} days ago"
                        + (git.Untracked > 0 ? $", plus {Plural(git.Untracked, "untracked path")}." : "."),
                        [
                            Inspect("What changed", GitCmd(clone.Entry.Path, "status --short")),
                            Inspect("Size of the changes", GitCmd(clone.Entry.Path, "diff HEAD --stat")),
                        ],
                        "tracked");
                }
            }
            else if (!git.IsDirty && git.Untracked > 0 && git.NewestUntrackedAt is { } untracked)
            {
                var days = DaysSince(context.Now, untracked);
                if (days >= limit)
                {
                    yield return this.ForClone(clone, FindingSeverity.Info,
                        $"{clone.Entry.Name}: untracked files for {days} days",
                        $"{Plural(git.Untracked, "untracked path")} that git is neither tracking nor ignoring. Add them, ignore them or delete them.",
                        [Inspect("Untracked paths", GitCmd(clone.Entry.Path, "status --short"))],
                        "untracked");
                }
            }
        }
    }
}
