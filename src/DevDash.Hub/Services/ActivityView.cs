using System.Globalization;
using System.Net;
using System.Text;
using DevDash.Contracts;

namespace DevDash.Hub.Services;

/// <summary>One square of the heatmap.</summary>
/// <param name="Level">0 for no commits, then 1 to 4.</param>
public sealed record HeatCell(DateOnly Date, int Count, int Level, int Week, int Weekday);

/// <summary>A year of commits per day, laid out in weeks (columns, Monday at the top) for drawing.</summary>
public sealed record Heatmap(IReadOnlyList<HeatCell> Cells, IReadOnlyList<(int Week, string Label)> Months, int Weeks, int Total, int ActiveDays, HeatCell? Busiest, DateOnly From, DateOnly To)
{
    /// <summary>Commits per calendar month, oldest first: the same data as a table.</summary>
    public IReadOnlyList<(string Month, int Count, int Days)> ByMonth => Cells
        .GroupBy(c => new DateOnly(c.Date.Year, c.Date.Month, 1))
        .OrderBy(g => g.Key)
        .Select(g => (g.Key.ToString("MMM yyyy", CultureInfo.InvariantCulture), g.Sum(c => c.Count), g.Count(c => c.Count > 0)))
        .ToList();
}

/// <summary>
/// The activity heatmap: joins the per-day commit counts the agents send, and lays a year out as a grid.
/// <para>
/// <b>Counting once across machines.</b> Agents send counts, not commit ids, so the hub cannot dedupe
/// commit by commit. Clones of one repo share their history, so for each repo and day it takes the
/// <i>highest</i> count any clone reports, and then adds the repos together. A commit that every machine
/// has is counted once. The price: when two machines each have different commits of the same repo on
/// the same day that the other has not fetched, the day shows the larger number, not the sum.
/// </para>
/// </summary>
public static class ActivityView
{
    public const int Days = 365;

    /// <summary>True when any clone sent activity: older agents send none, and the heatmap is then left out.</summary>
    public static bool Any(IEnumerable<RepoRow> rows) => rows.Any(r => r.Clones.Any(c => c.Git?.Activity is not null));

    /// <summary>True when any clone could say which commits are the user's own (it has a <c>user.email</c>).</summary>
    public static bool AnyMine(IEnumerable<RepoRow> rows) => rows.Any(r => r.Clones.Any(c => c.Git?.Activity?.Mine is not null));

    /// <summary>One repo: for each day, the most any of its clones reports.</summary>
    /// <param name="mine">Only commits authored with the clone's own <c>user.email</c>. A clone that cannot tell is left out.</param>
    public static Dictionary<DateOnly, int> ForRepo(RepoRow row, bool mine)
    {
        var days = new Dictionary<DateOnly, int>();
        foreach (var clone in row.Clones)
        {
            var counts = mine ? clone.Git?.Activity?.Mine : clone.Git?.Activity?.Days;
            if (counts is null)
            {
                continue;
            }

            foreach (var (text, count) in counts)
            {
                if (count > 0 && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    days[day] = Math.Max(days.GetValueOrDefault(day), count);
                }
            }
        }

        return days;
    }

    /// <summary>Every repo added together, each counted once however many machines have it.</summary>
    public static Dictionary<DateOnly, int> Total(IEnumerable<RepoRow> rows, bool mine)
    {
        var total = new Dictionary<DateOnly, int>();
        foreach (var row in rows)
        {
            foreach (var (day, count) in ForRepo(row, mine))
            {
                total[day] = total.GetValueOrDefault(day) + count;
            }
        }

        return total;
    }

    /// <summary>Lays out the year that ends on <paramref name="today"/>. Days outside it are dropped.</summary>
    public static Heatmap Build(IReadOnlyDictionary<DateOnly, int> days, DateOnly today)
    {
        var from = today.AddDays(-(Days - 1));
        // Columns are whole weeks, Monday first, so the first column starts on the Monday on or before the first day.
        var gridStart = from.AddDays(-(((int)from.DayOfWeek + 6) % 7));

        // The scale tops out at the 90th percentile of the days that have commits, so one enormous day
        // (an import, a squash of a month's work) does not turn every other day pale.
        var counts = days.Where(d => d.Key >= from && d.Key <= today && d.Value > 0).Select(d => d.Value).Order().ToList();
        var top = counts.Count == 0 ? 1 : Math.Max(4, counts[(int)Math.Floor((counts.Count - 1) * 0.9)]);

        var cells = new List<HeatCell>(Days);
        var months = new List<(int, string)>();
        var lastMonthWeek = -4;
        for (var day = from; day <= today; day = day.AddDays(1))
        {
            var offset = day.DayNumber - gridStart.DayNumber;
            var count = Math.Max(0, days.GetValueOrDefault(day));
            var level = count == 0 ? 0 : Math.Clamp((int)Math.Ceiling(count * 4.0 / top), 1, 4);
            cells.Add(new HeatCell(day, count, level, offset / 7, offset % 7));

            // A month is labelled above the week its first day falls in, when there is room after the last label.
            if ((day.Day == 1 || day == from) && offset / 7 - lastMonthWeek >= 3)
            {
                months.Add((offset / 7, day.ToString("MMM", CultureInfo.InvariantCulture)));
                lastMonthWeek = offset / 7;
            }
        }

        var busiest = cells.Where(c => c.Count > 0).OrderByDescending(c => c.Count).ThenByDescending(c => c.Date).FirstOrDefault();
        return new Heatmap(cells, months, cells[^1].Week + 1, cells.Sum(c => c.Count), cells.Count(c => c.Count > 0), busiest, from, today);
    }

    /// <summary>"12 commits on 3 Nov", with the year when it is not this one.</summary>
    public static string Tooltip(HeatCell cell, DateOnly today)
    {
        var date = cell.Date.ToString(cell.Date.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
        return cell.Count == 0 ? $"No commits on {date}" : $"{Fmt.Count(cell.Count, "commit")} on {date}";
    }

    /// <summary>What a screen reader is told in place of 365 squares.</summary>
    public static string Summary(Heatmap map, DateOnly today) => map.Total == 0
        ? "No commits in the last year."
        : $"{Fmt.Count(map.Total, "commit")} on {Fmt.Count(map.ActiveDays, "day")} in the last year."
            + (map.Busiest is { } busiest ? $" Busiest: {Tooltip(busiest, today)}." : "");

    /// <summary>
    /// The grid as SVG, drawn on the server. Every square carries its own <c>&lt;title&gt;</c>, which is both
    /// the hover tooltip and what assistive technology reads; colours come from the theme's
    /// <c>--heat-0</c>…<c>--heat-4</c> through the <c>h0</c>…<c>h4</c> classes.
    /// </summary>
    public static string Svg(Heatmap map, DateOnly today, bool compact)
    {
        var cell = compact ? 9 : 11;
        var step = cell + (compact ? 2 : 3);
        var left = compact ? 0 : 30;
        var top = 16;
        var width = left + map.Weeks * step;
        var height = top + 7 * step;
        var inv = CultureInfo.InvariantCulture;

        var sb = new StringBuilder(64 * 1024);
        sb.Append(inv, $"<svg class=\"heat-svg\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\" role=\"img\" aria-label=\"{WebUtility.HtmlEncode(Summary(map, today))}\">");
        foreach (var (week, label) in map.Months)
        {
            sb.Append(inv, $"<text class=\"heat-label\" x=\"{left + week * step}\" y=\"10\">{label}</text>");
        }

        if (!compact)
        {
            foreach (var (row, label) in (ReadOnlySpan<(int, string)>)[(0, "Mon"), (2, "Wed"), (4, "Fri")])
            {
                sb.Append(inv, $"<text class=\"heat-label\" x=\"0\" y=\"{top + row * step + cell - 2}\">{label}</text>");
            }
        }

        foreach (var c in map.Cells)
        {
            sb.Append(inv, $"<rect class=\"h{c.Level}\" x=\"{left + c.Week * step}\" y=\"{top + c.Weekday * step}\" width=\"{cell}\" height=\"{cell}\" rx=\"2\"><title>{WebUtility.HtmlEncode(Tooltip(c, today))}</title></rect>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }
}
