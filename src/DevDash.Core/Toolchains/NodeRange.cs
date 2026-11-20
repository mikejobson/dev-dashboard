namespace DevDash.Core.Toolchains;

/// <summary>
/// The npm range syntax, as far as <c>engines.node</c> uses it: <c>&gt;=18</c>, <c>^20.1.0</c>, <c>~20.11</c>,
/// <c>18.x || 20.x</c>, <c>&gt;=18 &lt;21</c>, <c>18 - 20</c>, <c>*</c>. Pre-release tags in a range are not
/// understood, and a range that cannot be read is "unknown", never "not satisfied".
/// </summary>
public static class NodeRange
{
    private readonly record struct Partial(int? Major, int? Minor, int? Patch)
    {
        public (int, int, int) Floor => (Major ?? 0, Minor ?? 0, Patch ?? 0);

        /// <summary>The first version past everything this partial version covers. Null when it covers everything.</summary>
        public (int, int, int)? Ceiling => Major is null ? null
            : Minor is null ? (Major.Value + 1, 0, 0)
            : Patch is null ? (Major.Value, Minor.Value + 1, 0)
            : (Major.Value, Minor.Value, Patch.Value + 1);
    }

    /// <summary>True or false when the range could be read; null when it could not.</summary>
    public static bool? Satisfies(string? range, ToolVersion version)
    {
        if (range is null)
        {
            return null;
        }

        var v = (version.Major, version.Minor, version.Patch);
        var any = false;
        foreach (var alternative in range.Split("||"))
        {
            var result = SatisfiesSet(alternative.Trim(), v);
            if (result is null)
            {
                return null;
            }

            any |= result.Value;
        }

        return any;
    }

    private static bool? SatisfiesSet(string set, (int, int, int) v)
    {
        if (set.Length == 0)
        {
            return true; // an empty range is "*"
        }

        var hyphen = set.IndexOf(" - ", StringComparison.Ordinal);
        if (hyphen > 0)
        {
            if (ParsePartial(set[..hyphen]) is not { } low || ParsePartial(set[(hyphen + 3)..]) is not { } high)
            {
                return null;
            }

            return Compare(v, low.Floor) >= 0 && (high.Ceiling is not { } top || (high.Patch is null ? Compare(v, top) < 0 : Compare(v, high.Floor) <= 0));
        }

        // ">= 18" is one comparator written with a space: join an operator to what follows it.
        var tokens = new List<string>();
        string? pending = null;
        foreach (var token in set.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.All(c => c is '>' or '<' or '=' or '^' or '~'))
            {
                pending = (pending ?? "") + token;
                continue;
            }

            tokens.Add((pending ?? "") + token);
            pending = null;
        }

        if (pending is not null || tokens.Count == 0)
        {
            return null;
        }

        foreach (var token in tokens)
        {
            var result = SatisfiesComparator(token, v);
            if (result != true)
            {
                return result;
            }
        }

        return true;
    }

    private static bool? SatisfiesComparator(string token, (int, int, int) v)
    {
        var at = 0;
        while (at < token.Length && token[at] is '>' or '<' or '=' or '^' or '~')
        {
            at++;
        }

        var op = token[..at];
        if (ParsePartial(token[at..]) is not { } p)
        {
            return null;
        }

        var floor = p.Floor;
        var ceiling = p.Ceiling;
        switch (op)
        {
            case "" or "=":
                return Compare(v, floor) >= 0 && (ceiling is not { } top || Compare(v, top) < 0);
            case ">=":
                return Compare(v, floor) >= 0;
            case ">":
                // ">18" means past all of 18.x; ">18.2.0" means past that one version.
                return p.Patch is null ? ceiling is { } past && Compare(v, past) >= 0 : Compare(v, floor) > 0;
            case "<":
                return Compare(v, floor) < 0;
            case "<=":
                return p.Patch is null ? ceiling is not { } upTo || Compare(v, upTo) < 0 : Compare(v, floor) <= 0;
            case "~" or "~>":
                return Compare(v, floor) >= 0
                    && (p.Major is null || Compare(v, p.Minor is null ? (p.Major.Value + 1, 0, 0) : (p.Major.Value, p.Minor.Value + 1, 0)) < 0);
            case "^":
                if (p.Major is not { } major)
                {
                    return true;
                }

                // Up to the next change of the left-most part that is not zero.
                var next = major > 0 || p.Minor is null ? (major + 1, 0, 0)
                    : p.Minor.Value > 0 || p.Patch is null ? (0, p.Minor.Value + 1, 0)
                    : (0, 0, p.Patch.Value + 1);
                return Compare(v, floor) >= 0 && Compare(v, next) < 0;
            default:
                return null;
        }
    }

    private static Partial? ParsePartial(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        if (value.Length == 0 || value.Contains('-') || value.Contains('+'))
        {
            return null;
        }

        var parts = value.Split('.');
        if (parts.Length > 3)
        {
            return null;
        }

        var numbers = new int?[3];
        var wild = false;
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "x" or "X" or "*")
            {
                wild = true;
                continue;
            }

            // Nothing concrete may follow a wildcard: "1.x.3" is not a version.
            if (wild || parts[i].Length is 0 or > 9 || !int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            numbers[i] = number;
        }

        return new Partial(numbers[0], numbers[1], numbers[2]);
    }

    private static int Compare((int, int, int) a, (int, int, int) b) => a.CompareTo(b);
}
