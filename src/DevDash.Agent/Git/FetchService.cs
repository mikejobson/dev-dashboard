using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Git;

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
    public async Task<bool> FetchIfDueAsync(string repoPath, string commonDir, bool hasRemotes, CancellationToken ct)
    {
        var options = _options.CurrentValue.AutoFetch;
        if (!options.Enabled || !hasRemotes)
        {
            return false;
        }

        var now = _time.GetUtcNow();
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
            _logger.LogWarning("git fetch failed in {Path}: {Error}", repoPath, result.StdErr.Trim());
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
