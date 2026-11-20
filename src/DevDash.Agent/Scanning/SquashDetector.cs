using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using DevDash.Agent.Git;
using DevDash.Contracts;
using DevDash.Core.Git;

namespace DevDash.Agent.Scanning;

/// <summary>
/// Finds local branches whose unpushed commits are on the trunk already under other names: squash
/// merged (one trunk commit with the same change as the whole branch) or rebased/cherry-picked (every
/// commit has an equivalent patch on the trunk). Such a branch reads as "unpushed" to plain reachability
/// although deleting it loses nothing. Only a match proves it; anything else leaves the branch as it was.
/// <para>
/// Cost, for each candidate branch (unmerged, with commits on no remote; at most <see cref="MaxBranches"/>):
/// one <c>rev-list --count</c>, one <c>log --cherry-mark</c>, and when that does not settle it one
/// <c>merge-base</c>, two <c>diff --name-only</c>/<c>log --name-only</c> listings and a <c>diff</c> per
/// trunk commit that touched exactly the same files. Results are cached per (branch, trunk) commit pair.
/// </para>
/// </summary>
public sealed class SquashDetector(GitRunner git)
{
    /// <summary>Most branches looked at per repository, newest first.</summary>
    public const int MaxBranches = 40;

    /// <summary>A branch whose trunk has moved on by more than this since it forked is not compared: too costly, and too old to matter.</summary>
    public const int MaxTrunkCommits = 1000;

    /// <summary>A branch that changes more files than this is not compared as a squash.</summary>
    private const int MaxFiles = 500;

    /// <summary>Most trunk commits with the same files whose diffs are compared.</summary>
    private const int MaxCandidates = 5;

    private const int MaxCache = 4000;

    private readonly ConcurrentDictionary<(string Branch, string Trunk), string?> _cache = new();

    /// <summary>Returns the branches with <see cref="BranchInfo.SquashMergedInto"/> filled in where it was proven.</summary>
    public async Task<IReadOnlyList<BranchInfo>> DetectAsync(string path, string? trunkRef, IReadOnlyList<BranchInfo> branches, TimeSpan timeout, CancellationToken ct)
    {
        Task<GitResult> Run(IReadOnlyList<string> args) => git.RunAsync(path, args, timeout, ct);

        var candidates = branches
            .Where(b => b is { OnRemote: false, UnpushedCount: > 0 } && (b.MergedIntoTrunk ?? b.MergedIntoDefault) != true && CommitLogParser.IsSha(b.Sha))
            .OrderByDescending(b => b.LastCommitDate)
            .Take(MaxBranches)
            .ToList();
        if (trunkRef is null || candidates.Count == 0)
        {
            return branches;
        }

        // Only a remote-tracking trunk proves anything: a change that is only on a local branch is still only here.
        var trunk = await Run(["rev-parse", "--verify", "--quiet", "refs/remotes/" + trunkRef + "^{commit}"]).ConfigureAwait(false);
        var trunkSha = trunk.Ok ? trunk.StdOut.Trim() : "";
        if (!CommitLogParser.IsSha(trunkSha))
        {
            return branches;
        }

        if (_cache.Count > MaxCache)
        {
            _cache.Clear();
        }

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var branch in candidates)
        {
            if (!_cache.TryGetValue((branch.Sha, trunkSha), out var into))
            {
                into = await DetectOneAsync(Run, branch.Sha, trunkSha).ConfigureAwait(false);
                _cache[(branch.Sha, trunkSha)] = into;
            }

            if (into is not null)
            {
                found[branch.Name] = into;
            }
        }

        return found.Count == 0 ? branches : branches.Select(b => found.TryGetValue(b.Name, out var into) ? b with { SquashMergedInto = into } : b).ToList();
    }

    /// <summary>The trunk commit that carries <paramref name="tip"/>'s change, or null when that is not proven.</summary>
    internal static async Task<string?> DetectOneAsync(Func<IReadOnlyList<string>, Task<GitResult>> run, string tip, string trunk)
    {
        // Nothing new on the trunk since the branch forked: nothing can have landed there.
        var behind = await run(["rev-list", "--count", $"{tip}..{trunk}"]).ConfigureAwait(false);
        if (!behind.Ok || !int.TryParse(behind.StdOut.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n == 0 || n > MaxTrunkCommits)
        {
            return null;
        }

        // 1. Rebased or cherry-picked: every commit of the branch has a commit with the same patch on the trunk.
        var right = await run(["log", "--cherry-mark", "--right-only", "--no-merges", "--format=%m%H", "-n", "501", $"{trunk}...{tip}"]).ConfigureAwait(false);
        if (right.Ok)
        {
            var marks = Lines(right.StdOut);
            if (marks.Count is > 0 and <= 500 && marks.All(l => l.StartsWith('=')))
            {
                // Which trunk commit to name: the newest equivalent one that changes something. Empty
                // commits ("Initial plan") all share one patch id, so they match each other and say nothing.
                var left = await run(["log", "--cherry-mark", "--left-only", "--no-merges", "--no-ext-diff", "--no-textconv", "--shortstat", "--format=%m%H", "-n", MaxTrunkCommits.ToString(CultureInfo.InvariantCulture), $"{trunk}...{tip}"]).ConfigureAwait(false);
                string? current = null;
                foreach (var line in left.Ok ? Lines(left.StdOut) : [])
                {
                    if (line[0] is '=' or '<' or '>' or '+' && CommitLogParser.IsSha(line[1..]))
                    {
                        current = line[0] == '=' ? line[1..] : null;
                    }
                    else if (current is not null && line.Contains("changed", StringComparison.Ordinal))
                    {
                        return current;
                    }
                }
            }
        }

        // 2. Squash merged: one trunk commit since the fork makes the same change as the whole branch.
        var mergeBase = await run(["merge-base", trunk, tip]).ConfigureAwait(false);
        var fork = mergeBase.Ok ? mergeBase.StdOut.Trim() : "";
        if (!CommitLogParser.IsSha(fork) || fork == tip)
        {
            return null;
        }

        var files = await run(["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--name-only", fork, tip]).ConfigureAwait(false);
        var branchFiles = files.Ok ? Lines(files.StdOut).ToHashSet(StringComparer.Ordinal) : [];
        if (branchFiles.Count is 0 or > MaxFiles)
        {
            return null;
        }

        var log = await run(["log", "--first-parent", "--no-merges", "--no-renames", "--name-only", "--format=%x1e%H%x1f%P", "-n", MaxTrunkCommits.ToString(CultureInfo.InvariantCulture), $"{fork}..{trunk}"]).ConfigureAwait(false);
        if (!log.Ok)
        {
            return null;
        }

        var candidates = new List<(string Sha, string Parent)>();
        foreach (var record in log.StdOut.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = Lines(record);
            if (lines.Count == 0 || lines[0].Split('\u001f') is not [var sha, var parents] || parents.Split(' ', StringSplitOptions.RemoveEmptyEntries) is not [var parent])
            {
                continue;
            }

            var touched = lines.Skip(1).ToHashSet(StringComparer.Ordinal);
            if (touched.SetEquals(branchFiles) && CommitLogParser.IsSha(sha) && CommitLogParser.IsSha(parent))
            {
                candidates.Add((sha, parent));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var mine = await ChangeAsync(run, fork, tip).ConfigureAwait(false);
        if (mine is null)
        {
            return null;
        }

        foreach (var (sha, parent) in candidates.Take(MaxCandidates))
        {
            if (await ChangeAsync(run, parent, sha).ConfigureAwait(false) == mine)
            {
                return sha;
            }
        }

        return null;
    }

    /// <summary>The change between two commits, reduced to what a squash keeps whatever the trunk did around it.</summary>
    private static async Task<string?> ChangeAsync(Func<IReadOnlyList<string>, Task<GitResult>> run, string from, string to)
    {
        var diff = await run(["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--full-index", "--unified=0", from, to]).ConfigureAwait(false);
        return diff.Ok ? NormalizeDiff(diff.StdOut) : null;
    }

    /// <summary>
    /// Keeps, per file, which file it is and the lines removed and added, in order: no line numbers, no
    /// context, no blob ids, so a squash applied on a trunk that moved on still matches. A binary file has
    /// no lines to compare, so its resulting blob id stands in for them.
    /// </summary>
    internal static string NormalizeDiff(string diff)
    {
        var sb = new StringBuilder(diff.Length / 2);
        string? newBlob = null;
        var header = false;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                sb.Append('\u001e').Append(line).Append('\n');
                newBlob = null;
                header = true;
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                header = false;
            }
            else if (header && (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal)))
            {
                continue;
            }
            else if (line.StartsWith("index ", StringComparison.Ordinal))
            {
                var range = line[6..].Split(' ')[0];
                var dots = range.IndexOf("..", StringComparison.Ordinal);
                newBlob = dots >= 0 ? range[(dots + 2)..] : null;
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch")
            {
                sb.Append("binary ").Append(newBlob ?? "?").Append('\n');
            }
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal) || line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                sb.Append(line.AsSpan(0, line.IndexOf(" mode", StringComparison.Ordinal))).Append('\n');
            }
            else if (!header && (line.StartsWith('+') || line.StartsWith('-')))
            {
                sb.Append(line.TrimEnd()).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static List<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
}
