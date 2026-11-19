using DevDash.Core.Cleanup;
using DevDash.Hub.GitHub;

namespace DevDash.Hub.Services;

/// <summary>
/// The numbers shown next to the navigation links. This is the one place that defines them:
/// <list type="bullet">
/// <item>GitHub: review requests waiting on me, not counting the bot drafts the GitHub page hides by
/// default (<see cref="PrTriage.IsStaleBotDraft"/>).</item>
/// <item>Cleanup: active findings (not snoozed, not dismissed) of <see cref="FindingSeverity.High"/> severity.</item>
/// </list>
/// A count of zero is not shown.
/// </summary>
public sealed class NavCounts(GitHubStore github, FindingsService findings, TimeProvider time)
{
    public int GitHub() => PrTriage.Split(github.Current, time.GetUtcNow()).Review.Count;

    public async Task<int> CleanupAsync(CancellationToken ct = default) =>
        (await findings.GetAsync(ct)).Count(v => v.IsActive && v.Finding.Severity == FindingSeverity.High);
}
