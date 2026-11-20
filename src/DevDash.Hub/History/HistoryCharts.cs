using System.Globalization;
using System.Net;
using System.Text;

namespace DevDash.Hub.History;

/// <summary>One line of a chart: a name and a value per day. Days with no value leave a gap.</summary>
/// <param name="Slot">The colour: <c>--dev-N</c>, the same per-device colours as the branch map, so a machine keeps its colour across charts.</param>
public sealed record ChartSeries(string Name, int Slot, IReadOnlyList<(DateOnly Day, double Value)> Points);

/// <summary>
/// Small charts drawn as SVG on the server, like the activity heatmap: no chart library, colours from
/// the theme's variables (so light and dark both work), and every point carries a <c>&lt;title&gt;</c>, which is
/// the hover tooltip and what a screen reader gets. Each chart on the page also has a table of the same data.
/// </summary>
public static class HistoryCharts
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Device colour slots there are (<c>--dev-0</c> … <c>--dev-5</c>).</summary>
    public const int Slots = 6;

    public static string DayLabel(DateOnly day, DateOnly today) =>
        day.ToString(day.Year == today.Year ? "d MMM" : "d MMM yyyy", Inv);

    /// <summary>A line chart of several series over the days from <paramref name="from"/> to <paramref name="to"/>, sharing one axis.</summary>
    public static string Line(IReadOnlyList<ChartSeries> series, DateOnly from, DateOnly to, Func<double, string> format, string label)
    {
        // Drawn about the size it is shown (half a laptop-width page), so its text stays readable; a phone scales it down.
        const int width = 520, height = 180, left = 62, right = 70, top = 12, bottom = 26;
        var plotW = width - left - right;
        var plotH = height - top - bottom;
        var span = Math.Max(1, to.DayNumber - from.DayNumber);
        var max = series.SelectMany(s => s.Points).Select(p => p.Value).DefaultIfEmpty(0).Max();
        var top0 = max <= 0 ? 1 : max * 1.08;

        double X(DateOnly day) => left + (day.DayNumber - from.DayNumber) * (double)plotW / span;
        double Y(double value) => top + plotH - value / top0 * plotH;

        var sb = new StringBuilder(8 * 1024);
        sb.Append(Inv, $"<svg class=\"chart-svg\" viewBox=\"0 0 {width} {height}\" width=\"100%\" preserveAspectRatio=\"xMinYMin meet\" role=\"img\" aria-label=\"{Enc(label)}\">");

        // Recessive grid: the baseline, the middle and the top, each labelled.
        double[] grid = max > 0 ? [0, max / 2, max] : [0];
        foreach (var value in grid)
        {
            var y = Y(value);
            sb.Append(Inv, $"<line class=\"chart-grid{(value == 0 ? " base" : "")}\" x1=\"{left}\" x2=\"{left + plotW}\" y1=\"{y:0.#}\" y2=\"{y:0.#}\"/>");
            sb.Append(Inv, $"<text class=\"chart-label\" x=\"{left - 6}\" y=\"{y + 3:0.#}\" text-anchor=\"end\">{Enc(format(value))}</text>");
        }

        var today = to;
        sb.Append(Inv, $"<text class=\"chart-label\" x=\"{left}\" y=\"{height - 6}\">{Enc(DayLabel(from, today))}</text>");
        sb.Append(Inv, $"<text class=\"chart-label\" x=\"{left + plotW}\" y=\"{height - 6}\" text-anchor=\"end\">{Enc(DayLabel(to, today))}</text>");

        var ends = new List<(double Y, string Value, string Name, int Slot)>();
        foreach (var s in series)
        {
            var points = s.Points.Where(p => p.Day >= from && p.Day <= to).OrderBy(p => p.Day).ToList();
            if (points.Count == 0)
            {
                continue;
            }

            var slot = ((s.Slot % Slots) + Slots) % Slots;
            sb.Append(Inv, $"<g class=\"chart-series s{slot}\">");

            // A missing day breaks the line rather than drawing across it.
            var run = new List<(DateOnly Day, double Value)>();
            void Flush()
            {
                if (run.Count > 1)
                {
                    sb.Append("<polyline points=\"");
                    sb.AppendJoin(' ', run.Select(p => string.Create(Inv, $"{X(p.Day):0.#},{Y(p.Value):0.#}")));
                    sb.Append("\"/>");
                }
                else if (run.Count == 1)
                {
                    sb.Append(Inv, $"<circle class=\"dot\" cx=\"{X(run[0].Day):0.#}\" cy=\"{Y(run[0].Value):0.#}\" r=\"3\"/>");
                }

                run.Clear();
            }

            foreach (var point in points)
            {
                if (run.Count > 0 && point.Day.DayNumber - run[^1].Day.DayNumber > 1)
                {
                    Flush();
                }

                run.Add(point);
            }

            Flush();

            var last = points[^1];
            sb.Append(Inv, $"<circle class=\"dot\" cx=\"{X(last.Day):0.#}\" cy=\"{Y(last.Value):0.#}\" r=\"3.5\"/>");

            // Hover targets bigger than the line, one per day.
            foreach (var point in points)
            {
                sb.Append(Inv, $"<circle class=\"hit\" cx=\"{X(point.Day):0.#}\" cy=\"{Y(point.Value):0.#}\" r=\"7\"><title>{Enc($"{s.Name}: {format(point.Value)} on {DayLabel(point.Day, today)}")}</title></circle>");
            }

            sb.Append("</g>");
            ends.Add((Y(last.Value), format(last.Value), s.Name, slot));
        }

        // The latest value at the end of each line, nudged apart so they do not overlap. The legend names the
        // lines; a machine name here would not fit beside the plot once there are three of them.
        if (ends.Count is > 0 and <= 6)
        {
            var placed = new List<double>();
            foreach (var (y, value, name, slot) in ends.OrderBy(e => e.Y))
            {
                var at = Math.Max(y + 4, placed.Count == 0 ? top + 6 : placed[^1] + 15);
                placed.Add(at);
                sb.Append(Inv, $"<text class=\"chart-end s{slot}\" x=\"{left + plotW + 8}\" y=\"{at:0.#}\"><title>{Enc(name)}</title>{Enc(value)}</text>");
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>A sparkline: one series, no axes, the last value marked. The title says first and last.</summary>
    public static string Sparkline(IReadOnlyList<(DateOnly Day, double Value)> points, Func<double, string> format, string label, int slot = 0, int width = 120, int height = 28)
    {
        var ordered = points.OrderBy(p => p.Day).ToList();
        var sb = new StringBuilder(1024);
        var summary = ordered.Count == 0
            ? $"{label}: no history yet"
            : $"{label}: {format(ordered[0].Value)} on {ordered[0].Day.ToString("d MMM", Inv)}, {format(ordered[^1].Value)} on {ordered[^1].Day.ToString("d MMM", Inv)}";
        sb.Append(Inv, $"<svg class=\"spark-svg s{((slot % Slots) + Slots) % Slots}\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\" role=\"img\" aria-label=\"{Enc(summary)}\"><title>{Enc(summary)}</title>");
        if (ordered.Count > 0)
        {
            var min = ordered.Min(p => p.Value);
            var max = ordered.Max(p => p.Value);
            var range = max - min;
            var first = ordered[0].Day.DayNumber;
            var span = Math.Max(1, ordered[^1].Day.DayNumber - first);
            double X(DateOnly d) => 2 + (d.DayNumber - first) * (width - 6.0) / span;
            double Y(double v) => range <= 0 ? height / 2.0 : height - 3 - (v - min) / range * (height - 6);

            if (ordered.Count > 1)
            {
                sb.Append("<polyline points=\"");
                sb.AppendJoin(' ', ordered.Select(p => string.Create(Inv, $"{X(p.Day):0.#},{Y(p.Value):0.#}")));
                sb.Append("\"/>");
            }

            sb.Append(Inv, $"<circle cx=\"{X(ordered[^1].Day):0.#}\" cy=\"{Y(ordered[^1].Value):0.#}\" r=\"2.5\"/>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Whole numbers, with k past ten thousand.</summary>
    public static string Number(double value) =>
        value >= 10_000 ? (value / 1000).ToString("0.#", Inv) + "k" : Math.Round(value).ToString("0", Inv);

    public static string Bytes(double value) => Fmt.Size((long)Math.Round(value));

    private static string Enc(string text) => WebUtility.HtmlEncode(text);
}
