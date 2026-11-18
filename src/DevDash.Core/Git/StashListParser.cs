using System.Globalization;
using System.Text.RegularExpressions;
using DevDash.Contracts;

namespace DevDash.Core.Git;

/// <summary>Parses <c>git stash list --format=&lt;Format&gt;</c>.</summary>
public static partial class StashListParser
{
    public const char Separator = '\u001f';

    /// <summary>Selector, committer time (unix seconds), stash commit and reflog subject.</summary>
    public const string Format = "%gd%x1f%ct%x1f%H%x1f%gs";

    [GeneratedRegex(@"^(?:[0-9a-f]{40}|[0-9a-f]{64})$")]
    private static partial Regex ShaPattern();

    [GeneratedRegex(@"^stash@\{(\d+)\}$")]
    private static partial Regex Selector();

    [GeneratedRegex(@"^(?:WIP on|On) (?<branch>[^:]+):")]
    private static partial Regex BranchPattern();

    public static IReadOnlyList<StashInfo> Parse(string output)
    {
        var result = new List<StashInfo>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var f = line.Split(Separator, 4);
            if (f.Length < 3)
            {
                continue;
            }

            // Output captured before the sha was added has three fields: selector, time, subject.
            string? sha = null;
            if (f.Length == 4 && ShaPattern().IsMatch(f[2]))
            {
                sha = f[2];
                f = [f[0], f[1], f[3]];
            }
            else if (f.Length == 4)
            {
                f = [f[0], f[1], f[2] + Separator + f[3]];
            }

            var selector = Selector().Match(f[0]);
            if (!selector.Success || !long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                continue;
            }

            var branch = BranchPattern().Match(f[2]);
            result.Add(new StashInfo
            {
                Index = int.Parse(selector.Groups[1].Value, CultureInfo.InvariantCulture),
                Sha = sha,
                Date = DateTimeOffset.FromUnixTimeSeconds(seconds),
                Message = f[2],
                Branch = branch.Success ? branch.Groups["branch"].Value : null,
            });
        }

        return result;
    }
}
