using System.Globalization;
using DevDash.Contracts;

namespace DevDash.Core.Git;

/// <summary>Parses <c>git log --format=&lt;Format&gt;</c>: one commit per line, with its parents.</summary>
public static class CommitLogParser
{
    public const char Separator = '\u001f';

    /// <summary>Sha, parents (space-separated), committer date, author name, subject.</summary>
    public const string Format = "%H%x1f%P%x1f%cI%x1f%an%x1f%s";

    /// <summary>Longer subjects are cut: the branch map shows one line per commit.</summary>
    public const int MaxSubjectLength = 100;

    public const int MaxAuthorLength = 40;

    public static IReadOnlyList<GraphCommit> Parse(string output)
    {
        var commits = new List<GraphCommit>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            // The subject comes last and keeps any separator characters of its own.
            var f = line.Split(Separator, 5);
            if (f.Length < 5 || !IsSha(f[0]))
            {
                continue;
            }

            commits.Add(new GraphCommit
            {
                Sha = f[0],
                Parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(IsSha).ToList(),
                Date = DateTimeOffset.TryParse(f[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : default,
                Author = Cut(f[3], MaxAuthorLength),
                Subject = Cut(f[4], MaxSubjectLength),
            });
        }

        return commits;
    }

    /// <summary>Parses output that is one sha per line (<c>--format=%H</c>), skipping anything else.</summary>
    public static IReadOnlyList<string> ParseShas(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsSha).ToList();

    public static bool IsSha(string value)
    {
        if (value.Length is not (40 or 64))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static string Cut(string text, int max)
    {
        text = text.Trim();
        if (text.Length <= max)
        {
            return text;
        }

        // Do not split a surrogate pair.
        var end = char.IsHighSurrogate(text[max - 2]) ? max - 2 : max - 1;
        return text[..end].TrimEnd() + "…";
    }
}
