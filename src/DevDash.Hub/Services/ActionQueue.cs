using System.Text.Json;
using DevDash.Contracts;
using DevDash.Core;
using DevDash.Core.Cleanup;
using DevDash.Hub.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Services;

public sealed record QueueOutcome(AgentActionEntity? Action, string? Error)
{
    public bool Ok => Action is not null;
}

/// <summary>
/// The queue of agent actions and its audit trail.
/// <list type="bullet">
/// <item>An action belongs to one machine and is only ever handed to an agent whose key is bound to that machine.</item>
/// <item>It is handed out at most once: claiming flips Queued to Claimed in a single conditional update, and nothing flips it back.</item>
/// <item>It expires if no agent claims it in time, and is recorded as failed if a claim never reports a result.</item>
/// </list>
/// The hub never sends a command line: only the typed <see cref="AgentAction"/>, which the agent validates again.
/// </summary>
public sealed class ActionQueue(
    IDbContextFactory<DevDashDbContext> dbFactory,
    SnapshotStore snapshots,
    DashboardNotifier notifier,
    IOptionsMonitor<HubOptions> options,
    TimeProvider time,
    ILogger<ActionQueue> logger)
{
    private const int MaxPerClaim = 20;

    /// <summary>How long a finished action keeps showing on its finding, while the rescan catches up.</summary>
    public static readonly TimeSpan ShownOnFindingFor = TimeSpan.FromMinutes(15);

    private HubActionOptions Options => options.CurrentValue.Actions;

    /// <summary>
    /// True when Run buttons may be shown for the machine: actions are on at the hub, the machine's own
    /// agent reported that it has them enabled, and the machine has been seen recently.
    /// </summary>
    public bool CanRun(string? machineId) =>
        Options.Enabled
        && machineId is not null
        && snapshots.Get(machineId) is { Machine.ActionsEnabled: true } machine
        && snapshots.IsOnline(machine);

    public IReadOnlySet<string> RunnableMachines() =>
        snapshots.Machines.Where(m => CanRun(m.MachineId)).Select(m => m.MachineId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Queues an action that a browser user has confirmed. Returns the reason when it is refused.</summary>
    public async Task<QueueOutcome> QueueAsync(string machineId, AgentAction action, string queuedBy, Finding? finding = null, CancellationToken ct = default)
    {
        if (!Options.Enabled)
        {
            return new QueueOutcome(null, "Agent actions are switched off on this hub (Hub:Actions:Enabled).");
        }

        if (string.IsNullOrWhiteSpace(queuedBy))
        {
            return new QueueOutcome(null, "Could not tell who is asking, so nothing was queued.");
        }

        if (snapshots.Get(machineId) is not { } machine)
        {
            return new QueueOutcome(null, "That machine is not known to this hub.");
        }

        if (!machine.Machine.ActionsEnabled)
        {
            return new QueueOutcome(null, $"{machine.Machine.Hostname} has not enabled agent actions. Start its agent with --enable-actions to allow them.");
        }

        if (!snapshots.IsOnline(machine))
        {
            return new QueueOutcome(null, $"{machine.Machine.Hostname} is offline, so the action would expire before it ran.");
        }

        if (AgentActions.Validate(action) is { } problem)
        {
            return new QueueOutcome(null, problem);
        }

        // The agent checks the path against its own scan roots. This catches a stale page earlier.
        if (!ReportedRepoPaths(machine.Snapshot).Contains(action.RepoPath))
        {
            return new QueueOutcome(null, $"{machine.Machine.Hostname} no longer reports a repository at {action.RepoPath}.");
        }

        var now = time.GetUtcNow();
        var json = AgentActions.Key(action);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = await db.AgentActions
            .Where(a => a.MachineId == machine.MachineId && (a.Status == AgentActionStatus.Queued || a.Status == AgentActionStatus.Claimed))
            .ToListAsync(ct);
        if (pending.FirstOrDefault(a => a.ActionJson == json && (a.Status == AgentActionStatus.Claimed || a.ExpiresAt > now)) is { } same)
        {
            // A double click, or a second tab: the same action is already on its way.
            return new QueueOutcome(same, null);
        }

        var entity = new AgentActionEntity
        {
            MachineId = machine.MachineId,
            MachineName = machine.Machine.Hostname,
            Kind = action.Kind,
            ActionJson = json,
            CommandLine = AgentActions.CommandLine(action),
            RepoPath = action.RepoPath,
            FindingFingerprint = finding?.Fingerprint,
            FindingTitle = finding?.Title,
            QueuedBy = queuedBy.Trim(),
            Status = AgentActionStatus.Queued,
            QueuedAt = now,
            ExpiresAt = now.AddMinutes(Math.Max(1, Options.ExpiryMinutes)),
        };
        db.AgentActions.Add(entity);
        await db.SaveChangesAsync(ct);

        var oldest = entity.Id - Math.Max(50, Options.KeepLast);
        if (oldest > 0)
        {
            await db.AgentActions.Where(a => a.Id <= oldest).ExecuteDeleteAsync(ct);
        }

        logger.LogInformation("Action {Id} queued for {Machine} by {User}: {Command}", entity.Id, entity.MachineId, entity.QueuedBy, entity.CommandLine);
        notifier.Notify(DashboardChange.Actions);
        return new QueueOutcome(entity, null);
    }

    /// <summary>
    /// Hands the machine's queued actions to its agent and marks them claimed. Anything returned here
    /// is never returned again, whatever happens to the agent afterwards.
    /// </summary>
    public async Task<IReadOnlyList<PendingAgentAction>> ClaimAsync(string machineId, CancellationToken ct = default)
    {
        await ExpireStaleAsync(ct);

        // An agent that has since been restarted without actions gets nothing, and its queue expires.
        if (!Options.Enabled || snapshots.Get(machineId) is not { Machine.ActionsEnabled: true } machine)
        {
            return [];
        }

        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var queued = await db.AgentActions.AsNoTracking()
            .Where(a => a.MachineId == machine.MachineId && a.Status == AgentActionStatus.Queued)
            .OrderBy(a => a.Id)
            .Take(MaxPerClaim)
            .ToListAsync(ct);

        var claimed = new List<PendingAgentAction>();
        foreach (var row in queued.Where(a => a.ExpiresAt > now))
        {
            AgentAction? action;
            try
            {
                action = JsonSerializer.Deserialize<AgentAction>(row.ActionJson, SnapshotSchema.JsonOptions);
            }
            catch (JsonException)
            {
                action = null;
            }

            // The conditional update is the claim: of any number of concurrent polls, one gets the row.
            var won = await db.AgentActions
                .Where(a => a.Id == row.Id && a.Status == AgentActionStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentActionStatus.Claimed).SetProperty(a => a.ClaimedAt, now), ct);
            if (won == 1 && action is not null)
            {
                claimed.Add(new PendingAgentAction { Id = Wire(row.Id), Action = action, QueuedAt = row.QueuedAt, ExpiresAt = row.ExpiresAt });
            }
        }

        if (claimed.Count > 0)
        {
            logger.LogInformation("Agent for {Machine} claimed {Count} action(s): {Ids}", machine.MachineId, claimed.Count, string.Join(", ", claimed.Select(c => c.Id)));
            notifier.Notify(DashboardChange.Actions);
        }

        return claimed;
    }

    /// <summary>Records the result an agent reports for an action it claimed. False when the action is not that machine's, or not awaiting a result.</summary>
    public async Task<bool> CompleteAsync(string machineId, string actionId, ActionResultRequest result, CancellationToken ct = default)
    {
        if (!int.TryParse(actionId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            return false;
        }

        var owner = snapshots.Get(machineId)?.MachineId ?? machineId;
        var now = time.GetUtcNow();
        var status = result.Succeeded ? AgentActionStatus.Succeeded : AgentActionStatus.Failed;
        var output = AgentActions.Truncate(result.Output);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var updated = await db.AgentActions
            .Where(a => a.Id == id && a.MachineId == owner && a.Status == AgentActionStatus.Claimed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.CompletedAt, now)
                .SetProperty(a => a.ExitCode, result.ExitCode)
                .SetProperty(a => a.Output, output), ct);
        if (updated != 1)
        {
            return false;
        }

        logger.LogInformation("Action {Id} on {Machine} {Status} (exit {Exit})", id, owner, status, result.ExitCode);
        notifier.Notify(DashboardChange.Actions);
        return true;
    }

    /// <summary>Expires queued actions nobody claimed and fails claimed ones that never reported back. Returns how many changed.</summary>
    public async Task<int> ExpireStaleAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var lease = TimeSpan.FromMinutes(Math.Max(1, Options.LeaseMinutes));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var open = await db.AgentActions.AsNoTracking()
            .Where(a => a.Status == AgentActionStatus.Queued || a.Status == AgentActionStatus.Claimed)
            .ToListAsync(ct);

        var changed = 0;
        foreach (var row in open)
        {
            if (row.Status == AgentActionStatus.Queued && row.ExpiresAt <= now)
            {
                changed += await db.AgentActions
                    .Where(a => a.Id == row.Id && a.Status == AgentActionStatus.Queued)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.Status, AgentActionStatus.Expired)
                        .SetProperty(a => a.CompletedAt, now)
                        .SetProperty(a => a.Output, "The agent did not pick this up in time, so it was not run."), ct);
            }
            else if (row.Status == AgentActionStatus.Claimed && row.ClaimedAt + lease <= now)
            {
                changed += await db.AgentActions
                    .Where(a => a.Id == row.Id && a.Status == AgentActionStatus.Claimed)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.Status, AgentActionStatus.Failed)
                        .SetProperty(a => a.CompletedAt, now)
                        .SetProperty(a => a.Output, "The agent took this action but never reported a result. It may or may not have run; it will not be retried."), ct);
            }
        }

        if (changed > 0)
        {
            notifier.Notify(DashboardChange.Actions);
        }

        return changed;
    }

    /// <summary>The audit trail, newest first.</summary>
    public async Task<IReadOnlyList<AgentActionEntity>> RecentAsync(int take = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AgentActions.AsNoTracking().OrderByDescending(a => a.Id).Take(take).ToListAsync(ct);
    }

    /// <summary>
    /// The latest action per (machine, action) that is still worth showing on a finding: anything
    /// pending, and anything that finished in the last few minutes.
    /// </summary>
    public async Task<IReadOnlyDictionary<(string MachineId, string ActionJson), AgentActionEntity>> ShownOnFindingsAsync(CancellationToken ct = default)
    {
        var cutoff = time.GetUtcNow() - ShownOnFindingFor;
        var result = new Dictionary<(string, string), AgentActionEntity>();
        foreach (var row in await RecentAsync(200, ct))
        {
            if (row.IsPending || row.CompletedAt >= cutoff)
            {
                result.TryAdd((row.MachineId.ToLowerInvariant(), row.ActionJson), row); // newest first, so the first one wins
            }
        }

        return result;
    }

    public static string Wire(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Every working tree path the machine's last snapshot mentions.</summary>
    private static HashSet<string> ReportedRepoPaths(MachineSnapshot snapshot)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in RepoIdentity.Flatten(snapshot.Entries).Where(e => e.Git is not null))
        {
            paths.Add(entry.Path);
            foreach (var worktree in entry.Git!.Worktrees)
            {
                paths.Add(worktree.Path);
            }
        }

        return paths;
    }
}

/// <summary>Expires stale actions on a timer, so the dashboard shows it without waiting for an agent to poll.</summary>
public sealed class ActionExpiryService(ActionQueue queue, ILogger<ActionExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await queue.ExpireStaleAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not expire stale agent actions");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
