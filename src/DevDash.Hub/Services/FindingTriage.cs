using DevDash.Core;
using DevDash.Core.Cleanup;

namespace DevDash.Hub.Services;

/// <summary>How old the thing behind a finding is.</summary>
public enum FindingAge
{
    /// <summary>No date is known for it.</summary>
    Unknown,

    /// <summary>Touched within <see cref="FindingTriage.RecentDays"/>: live work.</summary>
    Recent,

    /// <summary>Between recent and neglected.</summary>
    Idle,

    /// <summary>Untouched for more than <see cref="FindingTriage.NeglectedDays"/>: abandoned, not urgent.</summary>
    Neglected,
}

/// <summary>The filters of the Cleanup page. Every one is optional and they all have to match.</summary>
public sealed record FindingFilter
{
    public static readonly FindingFilter None = new();

    public string? MachineId { get; init; }
    public FindingSeverity MinSeverity { get; init; } = FindingSeverity.Info;

    /// <summary>Null for any age. <see cref="FindingAge.Recent"/> and <see cref="FindingAge.Neglected"/> need a known date.</summary>
    public FindingAge? Age { get; init; }
    public string? Text { get; init; }

    public bool IsEmpty => MachineId is null && MinSeverity == FindingSeverity.Info && Age is null && string.IsNullOrWhiteSpace(Text);
}

/// <summary>Findings about one repo (or folder) on one machine.</summary>
public sealed record RepoFindings(string Key, string Name, string? RepoKey, string? MachineId, string? MachineName, IReadOnlyList<FindingView> Items)
{
    public FindingSeverity Worst => Items.Max(v => v.Finding.Severity);

    public DateTimeOffset? LastActivityAt => Items.Max(v => v.Finding.LastActivityAt);
}

/// <summary>
/// Decides what is urgent and what is merely untidy. The thresholds live here and nowhere else:
/// the Overview and the Cleanup filters both read them.
/// </summary>
public static class FindingTriage
{
    /// <summary>Work touched within this many days is live: losing it would hurt today.</summary>
    public const int RecentDays = 30;

    /// <summary>Anything idle for longer than this is neglected. It is tidy-up, never urgent.</summary>
    public const int NeglectedDays = 90;

    /// <summary>How many rows of each kind the Overview's Today list shows before linking onwards.</summary>
    public const int TodayRows = 6;

    /// <summary>Rules about work that exists in one place only and would be lost with the disk.</summary>
    private static readonly HashSet<string> AtRiskRules = new(StringComparer.Ordinal) { "unpushed", "old-uncommitted", "diverged-across-machines" };

    public static FindingAge AgeOf(Finding finding, DateTimeOffset now)
    {
        if (finding.LastActivityAt is not { } at)
        {
            return FindingAge.Unknown;
        }

        var days = (now - at).TotalDays;
        return days <= RecentDays ? FindingAge.Recent : days > NeglectedDays ? FindingAge.Neglected : FindingAge.Idle;
    }

    /// <summary>
    /// True for local work that needs a look today: live (touched recently) and either high severity or
    /// at risk of being lost. PR findings are left out; the Overview lists those from the PR data.
    /// Work that has sat for months is never urgent, however severe its rule calls it.
    /// </summary>
    public static bool NeedsActionToday(Finding finding, DateTimeOffset now)
    {
        if (finding.RuleId is "failing-ci-pr" or "stale-pr")
        {
            return false;
        }

        return AgeOf(finding, now) switch
        {
            FindingAge.Recent => finding.Severity >= FindingSeverity.High
                || (finding.Severity >= FindingSeverity.Medium && AtRiskRules.Contains(finding.RuleId)),
            // No date to go on: only what the rule itself calls high.
            FindingAge.Unknown => finding.Severity >= FindingSeverity.High,
            _ => false,
        };
    }

    public static bool Matches(FindingFilter filter, Finding finding, DateTimeOffset now)
    {
        if (filter.MachineId is { } machine && !OnMachine(finding, machine))
        {
            return false;
        }

        if (finding.Severity < filter.MinSeverity)
        {
            return false;
        }

        if (filter.Age is { } age && AgeOf(finding, now) != age)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            // Every word has to appear somewhere: title (repo and branch names are in it), repo key, path or machine.
            string?[] haystack = [finding.Title, finding.RepoKey, finding.Path, finding.MachineName, finding.Explanation];
            foreach (var word in filter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!haystack.Any(h => h is not null && h.Contains(word, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>A finding is on a machine when it, or any of its commands, is meant for that machine.</summary>
    public static bool OnMachine(Finding finding, string machineId) =>
        string.Equals(finding.MachineId, machineId, StringComparison.OrdinalIgnoreCase)
        || finding.Commands.Any(c => string.Equals(c.MachineId, machineId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Worst first; among equals, the most recently touched first.</summary>
    public static IOrderedEnumerable<FindingView> Ranked(IEnumerable<FindingView> views) =>
        views.OrderByDescending(v => v.Finding.Severity)
            .ThenByDescending(v => v.Finding.LastActivityAt ?? DateTimeOffset.MinValue)
            .ThenBy(v => v.Finding.Title, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One group per repo per machine. A finding with no machine (a pull request, or a repo that has
    /// diverged across machines) gets a group of its own under the repo's name, so a group never mixes machines.
    /// </summary>
    public static IReadOnlyList<RepoFindings> ByRepo(IEnumerable<FindingView> views, DateTimeOffset now) =>
        views
            .GroupBy(v => ((v.Finding.RepoKey ?? "path:" + v.Finding.Path) + "\n" + v.Finding.MachineId).ToLowerInvariant())
            .Select(g =>
            {
                var first = g.First().Finding;
                return new RepoFindings(g.Key, RepoName(first), first.RepoKey, first.MachineId, first.MachineName, Ranked(g).ToList());
            })
            // Repos with live work at risk lead; then the worst, then the most recently touched.
            .OrderByDescending(g => g.Items.Any(v => v.IsActive && NeedsActionToday(v.Finding, now)))
            .ThenByDescending(g => g.Worst)
            .ThenByDescending(g => g.LastActivityAt ?? DateTimeOffset.MinValue)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary><c>owner/repo</c> for a hosted repo, else the folder's name.</summary>
    public static string RepoName(Finding finding)
    {
        if (finding.RepoKey is { } key && !RepoIdentity.IsLocalOnly(key))
        {
            var slash = key.IndexOf('/', StringComparison.Ordinal);
            return slash > 0 && slash < key.Length - 1 ? key[(slash + 1)..] : key;
        }

        var path = (finding.Path ?? finding.RepoKey ?? finding.Title).TrimEnd('/', '\\');
        var cut = path.LastIndexOfAny(['/', '\\']);
        return cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;
    }

    /// <summary>How many distinct repos (or folders) the findings are about, counting a repo once however many machines hold it.</summary>
    public static int RepoCount(IEnumerable<FindingView> views) =>
        views.Select(v => (v.Finding.RepoKey ?? "path:" + v.Finding.MachineId + ":" + v.Finding.Path).ToLowerInvariant()).Distinct().Count();
}
