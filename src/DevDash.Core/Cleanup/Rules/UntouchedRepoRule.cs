using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>Repos with no commits and no file changes for a long time.</summary>
public sealed class UntouchedRepoRule : ICleanupRule
{
    public string Id => "untouched-repo";
    public string Title => TitleFor(new CleanupOptions());

    public string TitleFor(CleanupOptions options) => $"Repos untouched for more than {Span(options.UntouchedDays)}";
    public string Description => "No commits and no file changes for a long time. A candidate to archive, or to delete locally once everything is pushed.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        foreach (var repo in context.Repositories())
        {
            var git = repo.Git;
            DateTimeOffset?[] dates = [repo.Entry.LastModified, git.LastCommit?.Date, .. git.Branches.Select(b => b.LastCommitDate)];
            var newest = dates.Where(d => d is not null).Max();
            if (newest is not { } last)
            {
                continue;
            }

            var days = DaysSince(context.Now, last);
            if (days < context.Options.UntouchedDays)
            {
                continue;
            }

            var safe = !git.IsDirty && git.Untracked == 0 && git.Stashes.Count == 0 && git.Remotes.Count > 0
                && git.Branches.All(b => b.Upstream is not null && !b.UpstreamGone && b.Ahead == 0);
            yield return this.ForClone(repo, FindingSeverity.Low,
                $"{repo.Entry.Name}: untouched for {days / 30} months",
                $"Nothing has changed here since {last:yyyy-MM-dd}. "
                + (safe
                    ? "Everything is pushed and the working tree is clean, so the local copy can go."
                    : "It still has local-only work (uncommitted files, stashes or unpushed branches), so check that first."),
                [
                    Inspect("Local state", GitCmd(repo.Entry.Path, "status --short --branch")),
                    Inspect("Commits that were never pushed", GitCmd(repo.Entry.Path, "log --branches --not --remotes --oneline")),
                    // Deleting the clone is only suggested when the scan found nothing that exists only here.
                    .. safe ? [Fix("Delete the local clone", $"rm -rf {Quote(repo.Entry.Path)}")] : Array.Empty<FindingCommand>(),
                ]).ActiveAt(last);
        }
    }
}
