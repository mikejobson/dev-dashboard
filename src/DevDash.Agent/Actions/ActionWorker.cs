using System.Text.Json;
using DevDash.Agent.Updates;
using DevDash.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Actions;

/// <summary>
/// Polls the hub for this machine's queued actions, runs each through <see cref="ActionExecutor"/>,
/// reports the result, and asks for a rescan so the dashboard catches up.
/// <para>
/// It polls when there is something this machine would accept: git actions (<c>Agent:Actions:Enabled</c>)
/// or an update request (<c>Agent:Updates:Mode</c> notify or auto). With both off it does nothing at
/// all, not even poll. Polling for updates does not make git actions runnable: the executor checks
/// each action against the switch for its own kind. An agent that polls also takes refresh requests,
/// which run nothing in a repository.
/// </para>
/// <para>
/// The poll is a long poll: the agent asks the hub to hold the request open for
/// <c>Agent:Actions:LongPollSeconds</c> and asks again as soon as it answers, so a click in the dashboard
/// reaches the agent at once and the hub still never dials an agent. When the hub answers straight
/// away with nothing (an older hub), or fails, it falls back to asking every
/// <c>Agent:Actions:PollSeconds</c>.
/// </para>
/// </summary>
public sealed class ActionWorker(
    ActionsClient hub,
    ActionExecutor executor,
    RescanRequests rescans,
    IOptionsMonitor<AgentOptions> options,
    ILogger<ActionWorker> logger,
    RestartSignal? restart = null) : BackgroundService
{
    /// <summary>True when the agent has any reason to ask the hub for work.</summary>
    public static bool ShouldPoll(AgentOptions options) => options.Actions.Enabled || options.Updates.AcceptsRequests;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var current = options.CurrentValue;
        if (!ShouldPoll(current))
        {
            logger.LogInformation("Agent actions and updates are both off: this agent only reports.");
            return;
        }

        var poll = TimeSpan.FromSeconds(Math.Max(5, current.Actions.PollSeconds));
        var wait = Math.Clamp(current.Actions.LongPollSeconds, 0, AgentActions.MaxClaimWaitSeconds);
        var how = wait > 0 ? $"Waiting on the hub for requests ({wait}s long poll, or every {poll.TotalSeconds}s if the hub does not hold it)" : $"Polling every {poll.TotalSeconds}s";
        if (current.Actions.Enabled)
        {
            logger.LogWarning("Agent actions are ON: allowlisted fixes confirmed in the dashboard will run in repositories under {Roots}. {How}.",
                string.Join(", ", current.ResolveRoots()), how);
        }
        else
        {
            logger.LogInformation("Agent actions are off (enable with --enable-actions). Updates are '{Mode}'. {How}: only an update or a refresh asked for from the dashboard is accepted.",
                current.Updates.ResolvedMode, how);
        }

        try
        {
            var failures = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var result = await PollAsync(wait, stoppingToken).ConfigureAwait(false);
                failures = result.Failed ? failures + 1 : 0;
                var delay = NextDelay(result, System.Diagnostics.Stopwatch.GetElapsedTime(started), TimeSpan.FromSeconds(wait), poll, failures);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    /// <summary>What one poll came to: how many actions were handled, and whether the hub could not be asked.</summary>
    public readonly record struct PollResult(int Handled, bool Failed);

    /// <summary>
    /// How long to wait before asking again.
    /// <list type="bullet">
    /// <item>Something was handled: ask again at once, there may be more.</item>
    /// <item>Nothing, after the hub held the request for most of the wait: that was a long poll timing out, so ask again at once.</item>
    /// <item>Nothing, straight away: the hub does not long-poll (an older hub, a key not bound yet, a hub shutting down).
    /// Keep the plain polling cadence, one request per <paramref name="poll"/>.</item>
    /// <item>The hub failed or could not be reached: back off, 2 s doubling up to <paramref name="poll"/>, so a hub
    /// that is restarting is found again quickly and one that is gone is not hammered.</item>
    /// </list>
    /// </summary>
    public static TimeSpan NextDelay(PollResult result, TimeSpan elapsed, TimeSpan wait, TimeSpan poll, int failures)
    {
        if (result.Failed)
        {
            var backoff = TimeSpan.FromSeconds(Math.Pow(2, Math.Clamp(failures, 1, 10)));
            return backoff < poll ? backoff : poll;
        }

        if (result.Handled > 0 || (wait > TimeSpan.Zero && elapsed >= wait / 2))
        {
            return TimeSpan.Zero;
        }

        return poll > elapsed ? poll - elapsed : TimeSpan.Zero;
    }

    /// <summary>One poll: claim, run, report, rescan. Returns how many actions were handled. Never throws for hub or git trouble.</summary>
    public async Task<int> PollOnceAsync(CancellationToken ct) => (await PollAsync(0, ct).ConfigureAwait(false)).Handled;

    /// <summary>One poll, holding it open at the hub for up to <paramref name="waitSeconds"/> when nothing is queued.</summary>
    public async Task<PollResult> PollAsync(int waitSeconds, CancellationToken ct)
    {
        IReadOnlyList<PendingAgentAction> pending;
        try
        {
            pending = await hub.ClaimAsync(ct, waitSeconds).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // The hub may be asleep or unreachable; the next poll tries again.
            logger.LogDebug("Could not poll for actions: {Error}", ex.Message);
            return new PollResult(0, Failed: true);
        }

        var restartNeeded = false;
        foreach (var item in pending.Where(p => p.Action is not { Kind: AgentActionKind.Refresh }))
        {
            var outcome = await executor.ExecuteAsync(item.Action, text => _ = ProgressAsync(item.Id, text, ct), ct).ConfigureAwait(false);
            await ReportAsync(item.Id, outcome, ct).ConfigureAwait(false);
            restartNeeded |= outcome.RestartRequired;
            foreach (var path in outcome.AffectedPaths)
            {
                rescans.Request(path);
            }
        }

        // Refreshes last, and together: they then see what the actions above changed, and several for
        // one entry (two worktrees in one folder, two people clicking) cost one rescan.
        var refreshes = pending.Where(p => p.Action is { Kind: AgentActionKind.Refresh }).ToList();
        if (refreshes.Count > 0)
        {
            var outcomes = await executor.RefreshAsync([.. refreshes.Select(r => r.Action)], ct).ConfigureAwait(false);
            for (var i = 0; i < refreshes.Count; i++)
            {
                await ReportAsync(refreshes[i].Id, outcomes[i], ct).ConfigureAwait(false);
            }
        }

        // Only after every result has been reported: the new version starts with nothing left over.
        if (restartNeeded)
        {
            restart?.Request("the agent was updated from the dashboard");
        }

        return new PollResult(pending.Count, Failed: false);
    }

    /// <summary>Tells the hub how a long action is getting on. Best effort: a hub that does not take it (before 0.8.0), or a failed call, changes nothing.</summary>
    private async Task ProgressAsync(string id, string text, CancellationToken ct)
    {
        try
        {
            await hub.ProgressAsync(id, text, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogDebug("Could not report progress of action {Id}: {Error}", id, ex.Message);
        }
    }

    private async Task ReportAsync(string id, ActionOutcome outcome, CancellationToken ct)
    {
        // The hub will not hand the action out again, so try a little harder to say how it went.
        // If this never gets through, the hub records the action as "no result reported".
        TimeSpan[] waits = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];
        foreach (var wait in waits)
        {
            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
                await hub.ReportAsync(id, outcome.ToResult(), ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Could not report the result of action {Id}: {Error}", id, ex.Message);
            }
        }
    }
}

public static class ActionServices
{
    /// <summary>
    /// Registers everything agent actions need. Whether they do anything is decided by
    /// <see cref="ActionsOptions.Enabled"/> (git actions) and <see cref="UpdateOptions.Mode"/> (update requests).
    /// </summary>
    public static IServiceCollection AddAgentActions(this IServiceCollection services)
    {
        services.AddSingleton<RescanRequests>();
        services.AddSingleton<RemovalLog>(_ => new RemovalLog());
        services.AddSingleton<KnownRemotes>();
        services.AddSingleton<ITrash>(sp => Trash.ForThisMachine(sp.GetRequiredService<IOptionsMonitor<AgentOptions>>().CurrentValue.Actions));
        services.AddSingleton<ActionValidator>();
        services.AddSingleton<ProjectActions>();
        services.AddSingleton<ArchiveActions>();
        services.AddSingleton<ActionExecutor>();
        // No client-wide timeout: a poll is held open by the hub, and ActionsClient gives every call its own deadline.
        services.AddHttpClient<ActionsClient>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHostedService<ActionWorker>();
        return services;
    }
}
