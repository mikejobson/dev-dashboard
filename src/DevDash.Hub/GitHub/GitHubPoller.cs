using DevDash.Core;
using DevDash.Core.GitHub;
using DevDash.Hub.Services;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.GitHub;

/// <summary>Polls GitHub on a timer, and sooner when a new local remote shows up or someone asks.</summary>
public sealed class GitHubPoller(
    GitHubClient client,
    GitHubStore store,
    SnapshotStore snapshots,
    DashboardNotifier notifier,
    IOptionsMonitor<GitHubOptions> options,
    TimeProvider time,
    ILogger<GitHubPoller> logger) : BackgroundService
{
    /// <summary>Early polls never run closer together than this, so a lookup that keeps failing cannot hammer the API.</summary>
    private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile HashSet<string> _unanswered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ask for a poll now (ignored while one is already queued).</summary>
    public void RequestPoll()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // already queued
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Enabled)
        {
            store.SetStatus(s => s with { Enabled = false });
            return;
        }

        store.SetStatus(s => s with { Enabled = true });
        notifier.Changed += OnChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PollOnceAsync(stoppingToken);
                var polledAt = time.GetUtcNow();
                var interval = TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.PollIntervalMinutes));
                if (await _wake.WaitAsync(interval, stoppingToken))
                {
                    // Woken early (new local remote, or "Refresh now"): still leave a gap between polls.
                    var gap = MinimumGap - (time.GetUtcNow() - polledAt);
                    if (gap > TimeSpan.Zero)
                    {
                        await Task.Delay(gap, stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            notifier.Changed -= OnChanged;
        }
    }

    /// <summary>A snapshot that mentions a GitHub remote we have never looked up triggers an early poll.</summary>
    private void OnChanged(DashboardChange change)
    {
        if (change != DashboardChange.Snapshot)
        {
            return;
        }

        var known = store.Current.LocalRemotes;
        var unanswered = _unanswered;
        if (!store.HasData || LocalRemoteKeys().Any(k => !known.ContainsKey(k) && !unanswered.Contains(k)))
        {
            RequestPoll();
        }
    }

    private List<string> LocalRemoteKeys() =>
        snapshots.Machines
            .SelectMany(m => RepoIdentity.Flatten(m.Snapshot.Entries))
            .Select(e => RepoIdentity.PrimaryRemote(e.Git)?.Normalized)
            .Where(k => k is not null && GitHubKeys.IsGitHubKey(k))
            .Select(k => k!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        store.SetStatus(s => s with { Polling = true });
        try
        {
            var keys = LocalRemoteKeys();
            var snapshot = await client.PollAsync(keys, ct, store.Current.LocalRemotes);
            await store.SetAsync(snapshot, ct);
            // Keys this poll had no answer for (over the per-poll cap, or not checkable) do not wake the next one early.
            _unanswered = keys.Where(k => !snapshot.LocalRemotes.ContainsKey(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            logger.LogInformation("GitHub poll: {Prs} PRs, {Issues} issues, {Repos} recent repos across {Orgs} orgs, {Lookups} local remotes, {Warnings} warnings",
                snapshot.PullRequests.Count, snapshot.Issues.Count, snapshot.RecentRepos.Count, snapshot.Organizations.Count,
                snapshot.LocalRemotes.Count, snapshot.Warnings.Count);
        }
        catch (Exception ex) when (ex is GitHubAuthException or GitHubQueryException or HttpRequestException or System.Text.Json.JsonException or KeyNotFoundException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("GitHub poll failed: {Error}", ex.Message);
            store.SetStatus(s => s with { Polling = false, LastAttemptAt = time.GetUtcNow(), LastError = ex.Message });
        }
    }
}
