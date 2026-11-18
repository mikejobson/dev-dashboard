using System.Globalization;
using System.Text;

namespace DevDash.Core.Git;

public sealed record ChangedPath(string Path, bool Tracked);

/// <summary>Parsed output of <c>git status --porcelain=v2 --branch</c>.</summary>
public sealed record StatusResult
{
    public string? HeadSha { get; init; }
    public string? Branch { get; init; }
    public bool Detached { get; init; }

    /// <summary>True for a repository with no commits yet.</summary>
    public bool Initial { get; init; }
    public string? Upstream { get; init; }

    /// <summary>An upstream is configured but its remote-tracking ref no longer exists.</summary>
    public bool UpstreamGone { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public int Staged { get; init; }
    public int Unstaged { get; init; }
    public int Untracked { get; init; }
    public int Conflicted { get; init; }
    public IReadOnlyList<ChangedPath> Paths { get; init; } = [];
}

public static class StatusParser
{
    public static StatusResult Parse(string output)
    {
        string? sha = null, branch = null, upstream = null;
        bool detached = false, initial = false, sawAb = false;
        int ahead = 0, behind = 0, staged = 0, unstaged = 0, untracked = 0, conflicted = 0;
        var paths = new List<ChangedPath>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var rest = line[2..];
                if (TryHeader(rest, "branch.oid ", out var v))
                {
                    if (v == "(initial)") { initial = true; } else { sha = v; }
                }
                else if (TryHeader(rest, "branch.head ", out v))
                {
                    if (v == "(detached)") { detached = true; } else { branch = v; }
                }
                else if (TryHeader(rest, "branch.upstream ", out v))
                {
                    upstream = v;
                }
                else if (TryHeader(rest, "branch.ab ", out v))
                {
                    var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2
                        && int.TryParse(parts[0].TrimStart('+'), NumberStyles.None, CultureInfo.InvariantCulture, out var a)
                        && int.TryParse(parts[1].TrimStart('-'), NumberStyles.None, CultureInfo.InvariantCulture, out var b))
                    {
                        ahead = a;
                        behind = b;
                        sawAb = true;
                    }
                }

                continue;
            }

            switch (line[0])
            {
                case '1':
                    CountXy(line, ref staged, ref unstaged);
                    AddPath(paths, Field(line, 8), tracked: true);
                    break;
                case '2':
                    CountXy(line, ref staged, ref unstaged);
                    // "<path>\t<origPath>" after 9 fields
                    var renamed = Field(line, 9);
                    var tab = renamed?.IndexOf('\t') ?? -1;
                    AddPath(paths, tab >= 0 ? renamed![..tab] : renamed, tracked: true);
                    break;
                case 'u':
                    conflicted++;
                    AddPath(paths, Field(line, 10), tracked: true);
                    break;
                case '?':
                    untracked++;
                    AddPath(paths, line.Length > 2 ? line[2..] : null, tracked: false);
                    break;
                default:
                    // '!' ignored entries and anything unknown
                    break;
            }
        }

        return new StatusResult
        {
            HeadSha = sha,
            Branch = branch,
            Detached = detached,
            Initial = initial,
            Upstream = upstream,
            UpstreamGone = upstream is not null && !sawAb,
            Ahead = ahead,
            Behind = behind,
            Staged = staged,
            Unstaged = unstaged,
            Untracked = untracked,
            Conflicted = conflicted,
            Paths = paths,
        };
    }

    private static bool TryHeader(string rest, string key, out string value)
    {
        if (rest.StartsWith(key, StringComparison.Ordinal))
        {
            value = rest[key.Length..].Trim();
            return true;
        }

        value = "";
        return false;
    }

    private static void CountXy(string line, ref int staged, ref int unstaged)
    {
        if (line.Length < 4)
        {
            return;
        }

        if (line[2] != '.') { staged++; }
        if (line[3] != '.') { unstaged++; }
    }

    /// <summary>Returns everything after <paramref name="skip"/> space-separated fields.</summary>
    private static string? Field(string line, int skip)
    {
        var index = 0;
        for (var i = 0; i < skip; i++)
        {
            index = line.IndexOf(' ', index);
            if (index < 0)
            {
                return null;
            }

            index++;
        }

        return index < line.Length ? line[index..] : null;
    }

    private static void AddPath(List<ChangedPath> paths, string? path, bool tracked)
    {
        if (!string.IsNullOrEmpty(path))
        {
            paths.Add(new ChangedPath(Unquote(path), tracked));
        }
    }

    /// <summary>Undoes git's C-style quoting of paths with special characters.</summary>
    public static string Unquote(string path)
    {
        if (path.Length < 2 || path[0] != '"' || path[^1] != '"')
        {
            return path;
        }

        var bytes = new List<byte>(path.Length);
        for (var i = 1; i < path.Length - 1; i++)
        {
            var c = path[i];
            if (c != '\\' || i + 1 >= path.Length - 1)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }

            var n = path[++i];
            if (n is >= '0' and <= '7')
            {
                var value = n - '0';
                for (var k = 0; k < 2 && i + 1 < path.Length - 1 && path[i + 1] is >= '0' and <= '7'; k++)
                {
                    value = (value * 8) + (path[++i] - '0');
                }

                bytes.Add((byte)value);
            }
            else
            {
                bytes.Add(n switch
                {
                    'n' => (byte)'\n',
                    't' => (byte)'\t',
                    'r' => (byte)'\r',
                    'a' => 7,
                    'b' => 8,
                    'f' => 12,
                    'v' => 11,
                    _ => (byte)n,
                });
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
