using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DevDash.Agent.Git;
using DevDash.Contracts;

namespace DevDash.Agent.Scanning;

/// <summary>
/// Counts a repository's commits per day over the last year, for the activity heatmap:
/// <c>git log --since=1.year --exclude=refs/stash --all --format=%ad%x1f%ae --date=short</c>.
/// <list type="bullet">
/// <item>The day is the <b>author</b> date, in the author's own time zone: when the work was done. A rebase or
/// a cherry-pick does not move it. (<c>--since</c> itself goes by commit date, so a commit authored long ago
/// and rebased this year is read and then dropped for being outside the window.)</item>
/// <item>Every branch, remote-tracking ref and tag counts; stashes do not (git stores them as commits).</item>
/// <item>"Mine" is the commits whose author email equals the repository's <c>user.email</c>. The comparison
/// happens here; no email is ever put in the snapshot.</item>
/// </list>
/// The result is kept per repository against a hash of its refs, the configured email and today's date,
/// so a repository where nothing moved costs one <c>for-each-ref</c> and one <c>config --get</c>.
/// </summary>
public sealed class ActivityCollector(GitRunner git, TimeProvider? time = null)
{
    public const int Days = 365;
    private const char Sep = '\u001f';

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (string Key, CommitActivity Activity)> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>How many times <c>git log</c> has actually been run. For tests of the cache.</summary>
    public int LogRuns { get; private set; }

    public async Task<CommitActivity?> CollectAsync(string path, string commonDir, TimeSpan timeout, CancellationToken ct)
    {
        // A repository and its linked worktrees are scanned side by side: the first one reads the log, the rest reuse it.
        var gate = _locks.GetOrAdd(commonDir, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var refs = await git.RunAsync(path, ["for-each-ref", "--format=%(objectname) %(refname)", "refs/heads", "refs/remotes", "refs/tags"], timeout, ct).ConfigureAwait(false);
            if (!refs.Ok)
            {
                return null;
            }

            // Exit code 1 with no output is "not set", which is an answer.
            var config = await git.RunAsync(path, ["config", "--get", "user.email"], timeout, ct).ConfigureAwait(false);
            var email = config.Ok ? config.StdOut.Trim() : "";

            var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{refs.StdOut}\n{email}\n{today:O}")));
            if (_cache.TryGetValue(commonDir, out var cached) && cached.Key == key)
            {
                return cached.Activity;
            }

            var log = await git.RunAsync(path, ["log", "--since=1.year", "--exclude=refs/stash", "--all", "--format=%ad%x1f%ae", "--date=short"], timeout, ct).ConfigureAwait(false);
            LogRuns++;
            if (!log.Ok)
            {
                // No commits yet, or the log could not be read: nothing to say, and nothing to remember.
                return null;
            }

            var activity = Parse(log.StdOut, email, today);
            _cache[commonDir] = (key, activity);
            return activity;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Turns <c>date␟email</c> lines into the two per-day counts, keeping the last <see cref="Days"/> days up to <paramref name="today"/> (and the day after: an author may be a time zone ahead).</summary>
    public static CommitActivity Parse(string log, string? email, DateOnly today)
    {
        var first = today.AddDays(-(Days - 1));
        var last = today.AddDays(1);
        var all = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var mine = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var hasEmail = !string.IsNullOrWhiteSpace(email);

        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var sep = line.IndexOf(Sep);
            var day = sep < 0 ? line : line[..sep];
            if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date < first || date > last)
            {
                continue;
            }

            all[day] = all.GetValueOrDefault(day) + 1;
            if (hasEmail && sep >= 0 && string.Equals(line[(sep + 1)..].Trim(), email!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                mine[day] = mine.GetValueOrDefault(day) + 1;
            }
        }

        return new CommitActivity { Days = all, Mine = hasEmail ? mine : null };
    }
}
