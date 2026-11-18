using System.Text.Json;
using DevDash.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Actions;

/// <summary>
/// While the agent runs with actions enabled: polls the hub for this machine's queued actions, runs
/// each through <see cref="ActionExecutor"/>, reports the result, and asks for a rescan so the
/// dashboard catches up. With actions disabled (the default) it does nothing at all, not even poll.
/// </summary>
public sealed class ActionWorker(
    ActionsClient hub,
    ActionExecutor executor,
    RescanRequests rescans,
    IOptionsMonitor<AgentOptions> options,
    ILogger<ActionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.Actions.Enabled)
        {
            logger.LogInformation("Agent actions are off: this agent only reports. Enable with --enable-actions or Agent:Actions:Enabled.");
            return;
        }

        var poll = TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.Actions.PollSeconds));
        logger.LogWarning("Agent actions are ON: allowlisted fixes confirmed in the dashboard will run in repositories under {Roots}. Polling every {Seconds}s.",
            string.Join(", ", options.CurrentValue.ResolveRoots()), poll.TotalSeconds);

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

        foreach (var item in pending)
        {
            var outcome = await executor.ExecuteAsync(item.Action, ct).ConfigureAwait(false);
            await ReportAsync(item.Id, outcome, ct).ConfigureAwait(false);
            foreach (var path in outcome.AffectedPaths)
            {
                rescans.Request(path);
            }
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
    /// <summary>Registers everything agent actions need. Whether they do anything is decided by <see cref="ActionsOptions.Enabled"/>.</summary>
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
