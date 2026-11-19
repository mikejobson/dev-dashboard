using DevDash.Contracts;

namespace DevDash.Hub.Services;

public enum LocalSort
{
    Activity,
    Name,
    State,
}

/// <summary>Sorting, grouping and naming for the Local page's rows.</summary>
public static class LocalView
{
    public const string NoOwner = "(local)";

    /// <summary>Parses the <c>sort</c> query value; anything unknown is the default, last activity.</summary>
    public static LocalSort ParseSort(string? value) =>
        Enum.TryParse<LocalSort>(value, ignoreCase: true, out var sort) && Enum.IsDefined(sort) ? sort : LocalSort.Activity;

    /// <summary>The direction a column sorts in when nothing says otherwise: newest and most urgent first, names A to Z.</summary>
    public static bool DefaultDescending(LocalSort sort) => sort != LocalSort.Name;

    /// <summary>How much a row wants attention: higher is more urgent. Used by the State sort.</summary>
    public static int StateRank(RepoRow row)
    {
        if (row.Kind != EntryKind.GitRepo) { return 0; }
        if (row.Clones.Any(c => c.Git is { Conflicted: > 0 })) { return 6; }
        if (row.Clones.Any(c => c.Git is { UpstreamGone: true })) { return 5; }
        if (row.AnyDirty) { return 4; }
        if (row.AnyAhead) { return 3; }
        if (row.AnyBehind) { return 2; }
        return 1;
    }

    public static List<RepoRow> Sort(IEnumerable<RepoRow> rows, LocalSort sort, bool descending)
    {
        IOrderedEnumerable<RepoRow> Direction<T>(Func<RepoRow, T> key, IComparer<T>? comparer = null) =>
            descending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

        var ordered = sort switch
        {
            LocalSort.Name => Direction(r => r.Name, StringComparer.OrdinalIgnoreCase),
            LocalSort.State => Direction(StateRank).ThenByDescending(r => r.LastActivity ?? DateTimeOffset.MinValue),
            // Rows with no known activity sort as the oldest.
            _ => Direction(r => r.LastActivity ?? DateTimeOffset.MinValue),
        };
        return ordered.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>The owner filter's value for a row: the remote's owner, or <see cref="NoOwner"/> without one.</summary>
    public static string OwnerOf(RepoRow row) => row.Owner ?? NoOwner;

    /// <summary>Owners with how many rows each has, biggest first; rows with no remote count under <see cref="NoOwner"/>.</summary>
    public static IReadOnlyList<(string Owner, int Count)> Owners(IEnumerable<RepoRow> rows) => rows
        .GroupBy(OwnerOf, StringComparer.OrdinalIgnoreCase)
        .Select(g => (Owner: g.Key, Count: g.Count()))
        .OrderBy(o => o.Owner == NoOwner)
        .ThenByDescending(o => o.Count)
        .ThenBy(o => o.Owner, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// For rows whose name is shared with another row, something that tells them apart: the remote's
    /// owner when that is enough, otherwise the short path of the first clone. Rows with a unique
    /// name are not in the result.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Disambiguators(IReadOnlyList<RepoRow> rows)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var ownersDiffer = group.All(r => r.Owner is not null)
                && group.Select(r => r.Owner!).Distinct(StringComparer.OrdinalIgnoreCase).Count() == group.Count();
            foreach (var row in group)
            {
                if (ownersDiffer)
                {
                    result[row.Key] = row.Owner!;
                    continue;
                }

                var clone = row.Clones.Count > 0 ? row.Clones[0] : null;
                var path = clone is null ? row.Key : Fmt.Tilde(clone.Entry.Path, Fmt.HomeOf(clone.Machine.Snapshot.Roots));
                result[row.Key] = row.Owner is null ? path : $"{row.Owner} · {path}";
            }
        }

        return result;
    }
}
