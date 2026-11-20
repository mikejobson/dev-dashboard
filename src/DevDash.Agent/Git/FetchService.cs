using System.Collections.Concurrent;
using DevDash.Core.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Git;

/// <summary>
/// A fetch asked for by a refresh from the dashboard: fetch now, whatever the interval says, but only
/// once per repository since <see cref="Since"/>, so a repository and its linked worktrees fetch once.
/// It never overrides <c>Agent:AutoFetch:Enabled</c>: a machine with fetching off does not fetch.
/// </summary>
public sealed class ForcedFetch(DateTimeOffset since)
{
    private int _fetched;

    public DateTimeOffset Since => since;

    /// <summary>How many repositories were fetched.</summary>
    public int Fetched => Volatile.Read(ref _fetched);

    /// <summary>One line per fetch that failed.</summary>
    public ConcurrentQueue<string> Failures { get; } = new();

    internal void Succeeded() => Interlocked.Increment(ref _fetched);
}

/// <summary>Decides when a repository is due for <c>git fetch --prune --quiet</c> and runs it.</summary>
public sealed class FetchService
{
    private readonly GitRunner _git;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<FetchService> _logger;
    private readonly TimeProvider _time;

    // Keyed by the shared .git directory so linked worktrees do not trigger a second fetch,
    // and so a repo whose fetch fails is not retried on every scan.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.Ordinal);

    public FetchService(GitRunner git, IOptionsMonitor<AgentOptions> options, ILogger<FetchService> logger, TimeProvider? time = null)
    {
        _git = git;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Fetches when auto-fetch is on and the last fetch is older than the interval. Never throws.</summary>
    /// <param name="forced">A refresh asked for from the dashboard: skips the interval, not the on/off switch.</param>
    public async Task<bool> FetchIfDueAsync(string repoPath, string commonDir, bool hasRemotes, CancellationToken ct, ForcedFetch? forced = null)
    {
        var options = _options.CurrentValue.AutoFetch;
        if (!options.Enabled || !hasRemotes)
        {
            return false;
        }

        var now = _time.GetUtcNow();
        if (forced is not null)
        {
            // Already tried for this refresh (a linked worktree of a repository just fetched).
            if (_lastAttempt.TryGetValue(commonDir, out var attempted) && attempted >= forced.Since)
            {
                return false;
            }

            _lastAttempt[commonDir] = now;
            var fetched = await _git.FetchAsync(repoPath, TimeSpan.FromSeconds(options.TimeoutSeconds), ct).ConfigureAwait(false);
            if (!fetched.Ok)
            {
                // git repeats the remote's URL, token and all, in its errors; this text goes to the hub.
                var error = RemoteUrlNormalizer.RedactCredentials(fetched.StdErr.Trim());
                _logger.LogWarning("git fetch failed in {Path}: {Error}", repoPath, error);
                forced.Failures.Enqueue($"{repoPath}: {(error.Length > 0 ? error : $"git fetch exited with {fetched.ExitCode}")}");
                return false;
            }

            forced.Succeeded();
            return true;
        }

        var interval = TimeSpan.FromHours(options.IntervalHours);
        var last = LastFetch(commonDir);
        if (last is not null && now - last < interval)
        {
            return false;
        }

        if (!_lastAttempt.TryAdd(commonDir, now))
        {
            if (now - _lastAttempt[commonDir] < interval)
            {
                return false;
            }

            _lastAttempt[commonDir] = now;
        }

        var result = await _git.FetchAsync(repoPath, TimeSpan.FromSeconds(options.TimeoutSeconds), ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            _logger.LogWarning("git fetch failed in {Path}: {Error}", repoPath, RemoteUrlNormalizer.RedactCredentials(result.StdErr.Trim()));
            return false;
        }

        _logger.LogDebug("Fetched {Path}", repoPath);
        return true;
    }

    /// <summary>When remote-tracking refs were last updated, from the mtime of FETCH_HEAD.</summary>
    public static DateTimeOffset? LastFetch(string commonDir)
    {
        var file = Path.Combine(commonDir, "FETCH_HEAD");
        return File.Exists(file) ? new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) : null;
    }
}
