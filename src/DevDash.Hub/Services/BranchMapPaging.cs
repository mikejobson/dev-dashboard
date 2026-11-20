namespace DevDash.Hub.Services;

/// <summary>What the branch map shows of its rows, and what "show more" offers.</summary>
/// <param name="Shown">Rows drawn.</param>
/// <param name="Rest">Rows below them.</param>
/// <param name="Next">How many more the next "show more" adds.</param>
/// <param name="RestBranches">Local branch names on the rows not drawn yet.</param>
public sealed record MapPage(int Shown, int Rest, int Next, int RestBranches);

/// <summary>
/// The branch map draws its newest rows first and the rest a page at a time. Lanes are worked out over
/// every row before any are drawn, so rows that appear later never move the ones above them.
/// </summary>
public static class BranchMapPaging
{
    /// <summary>Rows drawn at first, and added by each "show more".</summary>
    public const int PageSize = 100;

    /// <summary>A last page smaller than this is folded into the one before, rather than leaving a "show 7 more".</summary>
    private const int MinLastPage = 20;

    /// <param name="limit">Rows the viewer asked for so far (<see cref="PageSize"/> at first, <see cref="int.MaxValue"/> for all).</param>
    public static MapPage Page(BranchMap map, int limit)
    {
        var total = map.Rows.Count;
        var shown = total - limit < MinLastPage ? total : Math.Min(limit, total);
        var rest = total - shown;
        var next = rest - PageSize < MinLastPage ? rest : PageSize;
        var branches = rest == 0 ? 0 : map.Rows.Skip(shown).SelectMany(r => r.Refs).Where(r => r.Kind == MapRefKind.Local).Select(r => r.Name).Distinct(StringComparer.Ordinal).Count();
        return new MapPage(shown, rest, next, branches);
    }
}
