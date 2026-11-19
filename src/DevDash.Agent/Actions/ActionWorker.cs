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
/// each action against the switch for its own kind.
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
        if (current.Actions.Enabled)
        {
            logger.LogWarning("Agent actions are ON: allowlisted fixes confirmed in the dashboard will run in repositories under {Roots}. Polling every {Seconds}s.",
                string.Join(", ", current.ResolveRoots()), poll.TotalSeconds);
        }
        else
        {
            logger.LogInformation("Agent actions are off (enable with --enable-actions). Updates are '{Mode}': polling every {Seconds}s for an update requested from the dashboard only.",
                current.Updates.ResolvedMode, poll.TotalSeconds);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    /// <summary>One poll: claim, run, report, rescan. Returns how many actions were handled. Never throws for hub or git trouble.</summary>
    public async Task<int> PollOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<PendingAgentAction> pending;
        try
        {
            pending = await hub.ClaimAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // The hub may be asleep or unreachable; the next poll tries again.
            logger.LogDebug("Could not poll for actions: {Error}", ex.Message);
            return 0;
        }

        var restartNeeded = false;
        foreach (var item in pending)
        {
            var outcome = await executor.ExecuteAsync(item.Action, ct).ConfigureAwait(false);
            await ReportAsync(item.Id, outcome, ct).ConfigureAwait(false);
            restartNeeded |= outcome.RestartRequired;
            foreach (var path in outcome.AffectedPaths)
            {
                rescans.Request(path);
            }
        }

        // Only after every result has been reported: the new version starts with nothing left over.
        if (restartNeeded)
        {
            restart?.Request("the agent was updated from the dashboard");
        }

        return pending.Count;
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
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
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
        services.AddSingleton<ActionValidator>();
        services.AddSingleton<ActionExecutor>();
        services.AddHttpClient<ActionsClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHostedService<ActionWorker>();
        return services;
    }
}
