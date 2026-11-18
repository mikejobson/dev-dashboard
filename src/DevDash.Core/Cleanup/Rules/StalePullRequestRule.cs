using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>My open PRs that have stopped moving.</summary>
public sealed class StalePullRequestRule : ICleanupRule
{
    public string Id => "stale-pr";
    public string Title => "Stale PRs";
    public string Description => "Open PRs of mine with no recent activity, and drafts that have sat untouched.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        foreach (var pr in context.PullRequests.Where(p => p.Authored))
        {
            var days = DaysSince(context.Now, pr.UpdatedAt);
            var limit = pr.IsDraft ? context.Options.StaleDraftDays : context.Options.StalePullRequestDays;
            if (days < limit)
            {
                continue;
            }

            yield return new Finding
            {
                RuleId = Id,
                Severity = FindingSeverity.Low,
                Title = $"{pr.Repo}#{pr.Number}: {(pr.IsDraft ? "draft " : "")}idle for {days} days",
                Explanation = $"\"{pr.Title}\" has had no activity since {pr.UpdatedAt:yyyy-MM-dd}. Finish it, hand it over, or close it.",
                Commands =
                [
                    Inspect("Summary and checks", $"gh pr view {pr.Number} --repo {Quote(pr.Repo)}"),
                    Inspect("Open in the browser", $"gh pr view {pr.Number} --repo {Quote(pr.Repo)} --web"),
                ],
                RepoKey = pr.RepoKey,
                Url = pr.Url,
                Fingerprint = Finding.MakeFingerprint(Id, pr.RepoKey, pr.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
        }
    }
}
