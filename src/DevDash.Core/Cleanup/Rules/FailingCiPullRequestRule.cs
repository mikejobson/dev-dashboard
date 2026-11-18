using DevDash.Core.GitHub;
using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>My open PRs whose checks are failing.</summary>
public sealed class FailingCiPullRequestRule : ICleanupRule
{
    public string Id => "failing-ci-pr";
    public string Title => "PRs with failing CI";
    public string Description => "Open PRs of mine where the latest commit's checks failed.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        foreach (var pr in context.PullRequests.Where(p => p.Authored && p.Ci == CiState.Failure))
        {
            yield return new Finding
            {
                RuleId = Id,
                Severity = pr.IsDraft ? FindingSeverity.Low : FindingSeverity.High,
                Title = $"{pr.Repo}#{pr.Number}: CI failing",
                Explanation = $"Checks are failing on \"{pr.Title}\"" + (pr.IsDraft ? " (draft)." : "."),
                Commands =
                [
                    Inspect("Which checks failed", $"gh pr checks {pr.Number} --repo {Quote(pr.Repo)}"),
                    Inspect("Open in the browser", $"gh pr view {pr.Number} --repo {Quote(pr.Repo)} --web"),
                ],
                RepoKey = pr.RepoKey,
                Url = pr.Url,
                Fingerprint = Finding.MakeFingerprint(Id, pr.RepoKey, pr.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
        }
    }
}
