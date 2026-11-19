using DevDash.Core.GitHub;

namespace DevDash.Hub.Services;

/// <summary>Tells automated pull request authors (bots and coding agents) from people.</summary>
public static class BotAuthors
{
    // GitHub's GraphQL API reports app authors without the "[bot]" suffix the REST API and the web UI
    // use, so the usual agents are listed by name as well.
    private static readonly HashSet<string> KnownAgents = new(StringComparer.OrdinalIgnoreCase)
    {
        "copilot", "copilot-swe-agent", "copilot-pull-request-reviewer", "github-copilot",
        "dependabot", "dependabot-preview", "renovate", "renovate-bot", "github-actions",
        "devin-ai-integration", "cursoragent", "codex", "chatgpt-codex-connector", "claude", "sweep-ai", "snyk-bot",
    };

    public static bool IsBot(string? login)
    {
        var name = login?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return KnownAgents.Contains(name.StartsWith("app/", StringComparison.OrdinalIgnoreCase) ? name[4..] : name);
    }
}

/// <summary>Why one of my pull requests needs me now. Ordered by urgency; a PR is listed under the first that applies.</summary>
public enum PrAttention
{
    None,
    CiFailing,
    ChangesRequested,
    Conflicts,
    ReadyToMerge,
}

/// <summary>The GitHub page's lists, split out of one poll.</summary>
public sealed record PrBuckets
{
    /// <summary>Review requests worth showing by default.</summary>
    public IReadOnlyList<PullRequestInfo> Review { get; init; } = [];

    /// <summary>Review requests that are old drafts from a bot: hidden unless asked for.</summary>
    public IReadOnlyList<PullRequestInfo> StaleBotDrafts { get; init; } = [];

    /// <summary>Every open PR of mine that is not a review request.</summary>
    public IReadOnlyList<PullRequestInfo> Mine { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> Assigned { get; init; } = [];

    public IReadOnlyList<PullRequestInfo> MineNeeding(PrAttention attention) =>
        Mine.Where(p => PrTriage.Attention(p) == attention).ToList();

    public int MineNeedingAction => Mine.Count(p => PrTriage.Attention(p) != PrAttention.None);
}

/// <summary>Decides which pull requests need action. Used by the GitHub page and the sidebar count.</summary>
public static class PrTriage
{
    /// <summary>A bot's draft that nobody has touched for this long is hidden from review requests by default.</summary>
    public const int StaleBotDraftDays = 30;

    public static PrAttention Attention(PullRequestInfo pr)
    {
        if (pr.Ci == CiState.Failure) { return PrAttention.CiFailing; }
        if (pr.ReviewDecision == "CHANGES_REQUESTED") { return PrAttention.ChangesRequested; }
        if (pr.Mergeable == "CONFLICTING") { return PrAttention.Conflicts; }
        if (pr.ReviewDecision == "APPROVED" && !pr.IsDraft && pr.Ci != CiState.Pending) { return PrAttention.ReadyToMerge; }
        return PrAttention.None;
    }

    public static bool IsStaleBotDraft(PullRequestInfo pr, DateTimeOffset now) =>
        pr.IsDraft && BotAuthors.IsBot(pr.Author) && (now - pr.UpdatedAt).TotalDays > StaleBotDraftDays;

    /// <summary>Each PR lands in one list only: a review request wins, then mine, then assigned.</summary>
    public static PrBuckets Split(GitHubSnapshot snapshot, DateTimeOffset now)
    {
        var requested = snapshot.PullRequests.Where(p => p.ReviewRequested).ToList();
        return new PrBuckets
        {
            Review = requested.Where(p => !IsStaleBotDraft(p, now)).ToList(),
            StaleBotDrafts = requested.Where(p => IsStaleBotDraft(p, now)).ToList(),
            Mine = snapshot.PullRequests.Where(p => p.Authored && !p.ReviewRequested).ToList(),
            Assigned = snapshot.PullRequests.Where(p => p.Assigned && !p.Authored && !p.ReviewRequested).ToList(),
        };
    }
}

/// <summary>The pull requests of one repo, shown as a single collapsed row.</summary>
public sealed record PrGroup(string Repo, string RepoKey, IReadOnlyList<PullRequestInfo> Items)
{
    public string Name => Repo.Contains('/') ? Repo[(Repo.IndexOf('/') + 1)..] : Repo;

    public string? Owner => Repo.Contains('/') ? Repo[..Repo.IndexOf('/')] : null;

    public DateTimeOffset UpdatedAt => Items.Max(p => p.UpdatedAt);

    /// <summary>True when something in the group is failing, conflicting or has changes requested.</summary>
    public bool HasTrouble => Items.Any(p => PrTriage.Attention(p) is PrAttention.CiFailing or PrAttention.ChangesRequested or PrAttention.Conflicts);

    /// <summary>"31 open PRs", "all passing", "2 approved": the parts of the collapsed row, in order.</summary>
    public IReadOnlyList<(string Text, string Tone)> Summary()
    {
        var parts = new List<(string, string)> { ($"{Items.Count} open {(Items.Count == 1 ? "PR" : "PRs")}", "") };
        var failing = Items.Count(p => p.Ci == CiState.Failure);
        var running = Items.Count(p => p.Ci == CiState.Pending);
        var passing = Items.Count(p => p.Ci == CiState.Success);
        if (failing > 0) { parts.Add(($"{failing} failing", "danger")); }
        if (running > 0) { parts.Add(($"{running} running", "warn")); }
        if (failing == 0 && running == 0 && passing == Items.Count) { parts.Add(("all passing", "ok")); }
        else if (passing > 0) { parts.Add(($"{passing} passing", "ok")); }
        else if (failing == 0 && running == 0) { parts.Add(("no CI", "muted")); }

        var conflicts = Items.Count(p => p.Mergeable == "CONFLICTING");
        var changes = Items.Count(p => p.ReviewDecision == "CHANGES_REQUESTED");
        var approved = Items.Count(p => p.ReviewDecision == "APPROVED");
        var drafts = Items.Count(p => p.IsDraft);
        if (conflicts > 0) { parts.Add(($"{conflicts} {(conflicts == 1 ? "conflict" : "conflicts")}", "danger")); }
        if (changes > 0) { parts.Add(($"{changes} changes requested", "danger")); }
        if (approved > 0) { parts.Add(($"{approved} approved", "ok")); }
        if (drafts > 0) { parts.Add(($"{drafts} {(drafts == 1 ? "draft" : "drafts")}", "muted")); }
        return parts;
    }
}

public static class PrGrouping
{
    /// <summary>A repo gets its own collapsed row from this many pull requests.</summary>
    public const int MinGroupSize = 3;

    /// <summary>
    /// Splits a list into per-repo groups (repos with at least <paramref name="minGroupSize"/> PRs,
    /// biggest first) and the remaining PRs in their original order.
    /// </summary>
    public static (IReadOnlyList<PrGroup> Groups, IReadOnlyList<PullRequestInfo> Singles) Group(
        IReadOnlyList<PullRequestInfo> items, int minGroupSize = MinGroupSize)
    {
        var byRepo = items.GroupBy(p => p.RepoKey, StringComparer.OrdinalIgnoreCase).ToList();
        var groups = byRepo
            .Where(g => g.Count() >= minGroupSize)
            .Select(g => new PrGroup(g.First().Repo, g.Key, g.ToList()))
            .OrderByDescending(g => g.Items.Count)
            .ThenBy(g => g.Repo, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var grouped = groups.Select(g => g.RepoKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (groups, items.Where(p => !grouped.Contains(p.RepoKey)).ToList());
    }
}
