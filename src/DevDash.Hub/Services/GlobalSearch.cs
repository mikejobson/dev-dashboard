using DevDash.Core.GitHub;

namespace DevDash.Hub.Services;

public enum SearchHitKind
{
    Repo,
    PullRequest,
    Machine,
}

/// <summary>One result of the search box: where it goes and what to show.</summary>
public sealed record SearchHit(SearchHitKind Kind, string Title, string Detail, string Url, bool External);

/// <summary>
/// Finds repos by name, owner or branch, pull requests by title, repo or number, and machines. Plain
/// substring matching over what is already in memory, so it stays fast. The command palette
/// (<see cref="Palette"/>) is built on the same fields and adds pages, actions and its own ranking.
/// </summary>
public static class GlobalSearch
{
    public const int DefaultLimit = 12;

    public static IReadOnlyList<SearchHit> Search(
        string? query,
        IReadOnlyList<RepoRow> repos,
        IReadOnlyList<PullRequestInfo> pullRequests,
        IReadOnlyList<MachineView> machines,
        int limit = DefaultLimit)
    {
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return [];
        }

        var hits = new List<(int Score, SearchHit Hit)>();

        foreach (var machine in machines)
        {
            var score = Score(terms, MachineFields(machine));
            if (score > 0)
            {
                hits.Add((score + 1, MachineHit(machine)));
            }
        }

        foreach (var row in repos)
        {
            var score = Score(terms, RepoFields(row));
            if (score > 0)
            {
                hits.Add((score, RepoHit(row)));
            }
        }

        foreach (var pr in pullRequests)
        {
            var score = Score(terms, PullRequestFields(pr));
            if (score > 0)
            {
                hits.Add((score, PullRequestHit(pr)));
            }
        }

        // Best match first; within a score repos come before pull requests, in their given order.
        return hits.OrderByDescending(h => h.Score).Take(limit).Select(h => h.Hit).ToList();
    }

    // What each kind of thing is found by (the first field is its name) and how it is shown. The
    // command palette lists the same things with the same words, so these are shared with it.

    public static string?[] MachineFields(MachineView machine) => [machine.Machine.Hostname, machine.MachineId, machine.Machine.TailscaleName, machine.Machine.Os];

    public static SearchHit MachineHit(MachineView machine) => new(SearchHitKind.Machine, machine.Machine.Hostname,
        $"{machine.Machine.Os} · {machine.Snapshot.Entries.Count} entries", "machines#machine-" + Uri.EscapeDataString(machine.MachineId), false);

    public static string?[] RepoFields(RepoRow row) => [row.Name, row.Owner, .. row.Clones.Select(c => c.Git?.Branch), .. row.Clones.Select(c => c.Entry.Name)];

    public static SearchHit RepoHit(RepoRow row)
    {
        var branches = row.Clones.Select(c => c.Git?.Branch).Where(b => b is not null).Distinct().Take(2).ToList();
        var where = string.Join(", ", row.Clones.Select(c => c.Machine.Machine.Hostname).Distinct());
        var detail = string.Join(" · ", new[] { row.Owner, branches.Count > 0 ? string.Join(", ", branches) : null, where }.Where(s => !string.IsNullOrEmpty(s)));
        return new SearchHit(SearchHitKind.Repo, row.Name, detail, Fmt.RepoUrl(row.Key), false);
    }

    public static string?[] PullRequestFields(PullRequestInfo pr) => [pr.Title, pr.Repo, "#" + pr.Number, pr.HeadRef, pr.Author];

    public static SearchHit PullRequestHit(PullRequestInfo pr) => new(SearchHitKind.PullRequest, pr.Title, $"{pr.Repo}#{pr.Number}", pr.Url, true);

    /// <summary>
    /// 0 when some term is found in none of the fields. Otherwise higher is better: the first field
    /// (the name or title) counts most, and a match at its start more still.
    /// </summary>
    private static int Score(string[] terms, params string?[] fields)
    {
        var total = 0;
        foreach (var term in terms)
        {
            var best = 0;
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (string.IsNullOrEmpty(field))
                {
                    continue;
                }

                var at = field.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    continue;
                }

                var score = i == 0 ? (field.Length == term.Length ? 8 : at == 0 ? 6 : 4) : 2;
                best = Math.Max(best, score);
            }

            if (best == 0)
            {
                return 0;
            }

            total += best;
        }

        return total;
    }
}
