using DevDash.Core.Cleanup;
using DevDash.Core.Safety;
using DevDash.Hub.GitHub;

namespace DevDash.Hub.Services;

/// <summary>
/// The numbers shown next to the navigation links. This is the one place that defines them:
/// <list type="bullet">
/// <item>GitHub: review requests waiting on me, not counting the bot drafts the GitHub page hides by
/// default (<see cref="PrTriage.IsStaleBotDraft"/>).</item>
/// <item>Cleanup: active findings (not snoozed, not dismissed) of <see cref="FindingSeverity.High"/> severity.</item>
/// <item>Safety: working trees with commits that are on no remote (<see cref="RiskLevel.High"/>). Uncommitted
/// and untracked files are on the page but not in the count, or it would never be zero.</item>
/// <item>Alerts: alerts open now, whether or not they could be sent.</item>
/// </list>
/// A count of zero is not shown.
/// </summary>
public sealed class NavCounts(GitHubStore github, FindingsService findings, SafetyService safety, Alerts.AlertService alerts, TimeProvider time, IssueStore? issues = null)
{
    /// <summary>Issues: the open issues of the repos the hub tracks (the Issues page's list).</summary>
    public async Task<int> IssuesAsync(CancellationToken ct = default) => issues is null ? 0 : (await issues.GetAsync(ct)).All.Count();

    public async Task<int> AlertsAsync(CancellationToken ct = default) => (await alerts.OpenAsync(ct)).Count;

    public int Safety() => safety.Get().Count(r => r.Safety.Level == RiskLevel.High);

    public int GitHub() => PrTriage.Split(github.Current, time.GetUtcNow()).Review.Count;

    public async Task<int> CleanupAsync(CancellationToken ct = default) =>
        (await findings.GetAsync(ct)).Count(v => v.IsActive && v.Finding.Severity == FindingSeverity.High);
}
