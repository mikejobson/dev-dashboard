using System.Globalization;
using System.Text.RegularExpressions;
using DevDash.Contracts;

namespace DevDash.Core.Git;

/// <summary>Parses <c>git for-each-ref --format=&lt;Format&gt; refs/heads</c>.</summary>
public static partial class ForEachRefParser
{
    public const char Separator = '\u001f';

    /// <summary>Format string to pass to git. Fields are separated by the ASCII unit separator.</summary>
    public const string Format =
        "%(refname:short)%1f%(objectname)%1f%(upstream:short)%1f%(upstream:track)%1f%(committerdate:iso8601-strict)%1f%(HEAD)%1f%(contents:subject)";

    [GeneratedRegex(@"ahead (\d+)")]
    private static partial Regex AheadPattern();

    [GeneratedRegex(@"behind (\d+)")]
    private static partial Regex BehindPattern();

    public static IReadOnlyList<BranchInfo> Parse(string output)
    {
        var branches = new List<BranchInfo>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var f = line.Split(Separator);
            if (f.Length < 6 || f[0].Length == 0)
            {
                continue;
            }

            var track = f[3];
            var ahead = AheadPattern().Match(track);
            var behind = BehindPattern().Match(track);

            branches.Add(new BranchInfo
            {
                Name = f[0],
                Sha = f[1],
                Upstream = f[2].Length == 0 ? null : f[2],
                UpstreamGone = track.Contains("gone", StringComparison.Ordinal),
                Ahead = ahead.Success ? int.Parse(ahead.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
                Behind = behind.Success ? int.Parse(behind.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
                LastCommitDate = DateTimeOffset.TryParse(f[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
                IsCurrent = f[5].Trim() == "*",
                Subject = f.Length > 6 && f[6].Length > 0 ? string.Join(Separator, f.Skip(6)) : null,
            });
        }

        return branches;
    }

    /// <summary>Parses plain <c>--format=%(refname:short)</c> output (used with <c>--merged</c>).</summary>
    public static IReadOnlySet<string> ParseNames(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
}
