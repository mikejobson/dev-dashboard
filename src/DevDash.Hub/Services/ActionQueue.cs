using System.Text.Json;
using DevDash.Contracts;
using DevDash.Core;
using DevDash.Core.Cleanup;
using DevDash.Core.Safety;
using DevDash.Hub.Data;
using DevDash.Hub.Updates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Services;

/// <summary>Whether an action can be offered for a machine, and the hint to show on a disabled button when it cannot.</summary>
public sealed record ActionAvailability(bool Available, string? WhyNot);

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
/// <para>
/// Two kinds of action travel through here, each with its own switches. Git actions need
/// <c>Hub:Actions:Enabled</c> and a machine that reports <c>ActionsEnabled</c>. An agent update
/// (<see cref="AgentActionKind.UpdateAgent"/>) needs <c>Hub:Updates:Enabled</c> and a machine whose
/// reported update mode is notify or auto. Neither set of switches affects the other kind.
/// A refresh (<see cref="AgentActionKind.Refresh"/>) is a third kind with no switch: it is read-only,
/// so it only needs an agent that says it takes them (<see cref="RefreshGate"/>).
/// </para>
/// <para>
/// The kinds added in 0.6.0 (remove a project, clone it back, delete build output, fast-forward) are
/// git actions with one more condition: the agent must say it knows the kind
/// (<see cref="AgentActions.Accepts"/>). The hub also checks each against its own snapshot before
/// queueing (<see cref="RemoveRefusal"/>, <see cref="CloneRefusalAsync"/>), and settles the
/// removed-projects list when a removal or a clone finishes.
/// </para>
/// </summary>
public sealed class ActionQueue(
    IDbContextFactory<DevDashDbContext> dbFactory,
    SnapshotStore snapshots,
    ReleaseStore releases,
    DashboardNotifier notifier,
    ActionSignals signals,
    IOptionsMonitor<HubOptions> options,
    TimeProvider time,
    ILogger<ActionQueue> logger)
{
    private const int MaxPerClaim = 20;

    /// <summary>How many refreshes are kept. They are housekeeping, not fixes, and are trimmed apart from the audit trail's <c>KeepLast</c>.</summary>
    public const int KeepRefreshes = 100;

    /// <summary>How long a finished refresh keeps showing beside its button.</summary>
    public static readonly TimeSpan RefreshShownFor = TimeSpan.FromMinutes(5);

    /// <summary>How long a finished action keeps showing on its finding, while the rescan catches up.</summary>
    public static readonly TimeSpan ShownOnFindingFor = TimeSpan.FromMinutes(15);

    private HubActionOptions Options => options.CurrentValue.Actions;

    private HubUpdateOptions UpdateOptions => options.CurrentValue.Updates;

    /// <summary>Whether this hub and this machine allow actions of the given kind at all.</summary>
    private bool Allows(MachineView machine, AgentActionKind kind) => kind switch
    {
        AgentActionKind.UpdateAgent => UpdateOptions.Enabled && AgentUpdateModes.AcceptsRequests(machine.Machine.UpdateMode),
        AgentActionKind.Refresh => RefreshGate.WhyUnsupported(machine.Machine) is null,
        // Includes "new enough to know the kind": an older agent could not even read a claim that names it.
        _ => Options.Enabled && AgentActions.Accepts(machine.Machine, kind),
    };

    /// <summary>
    /// True when Run buttons may be shown for the machine: actions are on at the hub, the machine's own
    /// agent reported that it has them enabled, and the machine has been seen recently.
    /// </summary>
    public bool CanRun(string? machineId) =>
        Options.Enabled
        && machineId is not null
        && snapshots.Get(machineId) is { Machine.ActionsEnabled: true } machine
        && snapshots.IsOnline(machine);

    /// <summary>As <see cref="CanRun(string?)"/>, for one kind of action: the machine's agent must also know that kind.</summary>
    public bool CanRun(string? machineId, AgentActionKind kind) =>
        CanRun(machineId) && snapshots.Get(machineId!) is { } machine && Allows(machine, kind);

    /// <summary>
    /// Whether an action of a kind can be offered for a machine right now, and what to say when it cannot:
    /// the hub's switch, the agent's switch, the agent's version, or the machine being offline.
    /// </summary>
    public ActionAvailability Availability(MachineView machine, AgentActionKind kind)
    {
        if (!Options.Enabled)
        {
            return new ActionAvailability(false, "Agent actions are switched off on this hub.");
        }

        if (AgentActions.WhyNotAccepted(machine.Machine, kind) is { } why)
        {
            return new ActionAvailability(false, why);
        }

        return snapshots.IsOnline(machine)
            ? new ActionAvailability(true, null)
            : new ActionAvailability(false, $"{machine.Machine.Hostname} is offline.");
    }

    public IReadOnlySet<string> RunnableMachines() =>
        snapshots.Machines.Where(m => CanRun(m.MachineId)).Select(m => m.MachineId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Queues an action that a browser user has confirmed. Returns the reason when it is refused.</summary>
    public async Task<QueueOutcome> QueueAsync(string machineId, AgentAction action, string queuedBy, Finding? finding = null, CancellationToken ct = default)
    {
        var isUpdate = action is { Kind: AgentActionKind.UpdateAgent };
        var isRefresh = action is { Kind: AgentActionKind.Refresh };
        if (!isRefresh && (isUpdate ? !UpdateOptions.Enabled : !Options.Enabled))
        {
            return new QueueOutcome(null, isUpdate
                ? "Agent updates are switched off on this hub (Hub:Updates:Enabled)."
                : "Agent actions are switched off on this hub (Hub:Actions:Enabled).");
        }

        if (string.IsNullOrWhiteSpace(queuedBy))
        {
            return new QueueOutcome(null, "Could not tell who is asking, so nothing was queued.");
        }

        if (snapshots.Get(machineId) is not { } machine)
        {
            return new QueueOutcome(null, "That machine is not known to this hub.");
        }

        if (isUpdate && !AgentUpdateModes.AcceptsRequests(machine.Machine.UpdateMode))
        {
            return new QueueOutcome(null, machine.Machine.UpdateMode is null
                ? $"The agent on {machine.Machine.Hostname} predates self-update. Reinstall it once with the install one-liner."
                : $"{machine.Machine.Hostname} has updates switched off (Agent:Updates:Mode).");
        }

        if (isRefresh && RefreshGate.WhyUnsupported(machine.Machine) is { } noRefresh)
        {
            return new QueueOutcome(null, noRefresh);
        }

        if (!isUpdate && !isRefresh && !machine.Machine.ActionsEnabled)
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

        // An agent from before the kind existed could not read the request, so it is never sent one.
        if (!isUpdate && !isRefresh && AgentActions.WhyNotAccepted(machine.Machine, action!.Kind) is { } tooOld)
        {
            return new QueueOutcome(null, tooOld);
        }

        if (isUpdate)
        {
            // The hub only ever asks for the release it currently sees as latest, and only as a step forward.
            if (releases.Current is not { } latest || latest.Version != action.Version)
            {
                return new QueueOutcome(null, $"{action.Version} is not the latest release this hub knows of.");
            }

            if (!latest.Signed)
            {
                return new QueueOutcome(null, $"Release {latest.Version} is not signed, so agents will not install it.");
            }

            if (SemVer.TryParse(machine.Machine.AgentVersion, out var installed) && SemVer.TryParse(latest.Version, out var target) && installed >= target)
            {
                return new QueueOutcome(null, $"{machine.Machine.Hostname} is already on {machine.Machine.AgentVersion}.");
            }
        }
        // The agent checks the path against its own scan roots. This catches a stale page earlier.
        else if (isRefresh)
        {
            if (!RepoIdentity.Flatten(machine.Snapshot.Entries).Any(e => e.Path == action!.RepoPath))
            {
                return new QueueOutcome(null, $"{machine.Machine.Hostname} no longer reports anything at {action!.RepoPath}.");
            }
        }
        else if (action!.Kind == AgentActionKind.Clone)
        {
            // The agent decides for itself which URLs it will clone. This is the hub's own, separate, check.
            if (await CloneRefusalAsync(machine, action, ct) is { } noClone)
            {
                return new QueueOutcome(null, noClone);
            }
        }
        else if (!ReportedRepoPaths(machine.Snapshot).Contains(action.RepoPath!))
        {
            return new QueueOutcome(null, $"{machine.Machine.Hostname} no longer reports a repository at {action.RepoPath}.");
        }

        var now = time.GetUtcNow();
        CloneSafety? removing = null;
        if (action.Kind == AgentActionKind.RemoveProject)
        {
            // Assessed again here, from the snapshot as it is now: a page that has been open a while cannot remove something that has changed.
            removing = RepoSafety.AssessAll(machine.Snapshot, now).FirstOrDefault(c => c.Entry.Path == action.RepoPath);
            if (RemoveRefusal(machine, action, removing) is { } noRemove)
            {
                return new QueueOutcome(null, noRemove);
            }
        }
        else if (action.Kind == AgentActionKind.CleanBuildDirs)
        {
            var known = RepoIdentity.Flatten(machine.Snapshot.Entries)
                .FirstOrDefault(e => e.Path == action.RepoPath && e.Kind == EntryKind.GitRepo)?.Size?.ReclaimableDirs.Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
            if (known is null || action.BuildDirs!.Any(d => !known.Contains(d)))
            {
                return new QueueOutcome(null, $"{machine.Machine.Hostname} does not report that build output in {action.RepoPath} any more.");
            }
        }

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
            RepoPath = action.RepoPath ?? "",
            FindingFingerprint = finding?.Fingerprint,
            FindingTitle = finding?.Title,
            QueuedBy = queuedBy.Trim(),
            Status = AgentActionStatus.Queued,
            QueuedAt = now,
            ExpiresAt = now.AddMinutes(Math.Max(1, Options.ExpiryMinutes)),
        };
        db.AgentActions.Add(entity);
        await db.SaveChangesAsync(ct);

        // The tombstone is written now, while the snapshot still knows the project's remotes, and
        // confirmed when the agent reports it done. A linked worktree gets none: it is not cloned back.
        if (removing is { Safety.IsLinkedWorktree: false, Entry.Git: { } git })
        {
            db.RemovedProjects.Add(new RemovedProjectEntity
            {
                MachineId = machine.MachineId,
                MachineName = machine.Machine.Hostname,
                Path = removing.Entry.Path,
                Name = removing.Entry.Name,
                RepoKey = removing.RepoKey,
                RemotesJson = JsonSerializer.Serialize(git.Remotes, SnapshotSchema.JsonOptions),
                DefaultBranch = git.DefaultBranch,
                Branch = git.Branch,
                HeadSha = git.HeadSha,
                SizeBytes = removing.Safety.FreedBytes,
                ActionId = entity.Id,
                RemovedBy = entity.QueuedBy,
                QueuedAt = now,
            });
            await db.SaveChangesAsync(ct);
        }

        // Refreshes are trimmed on their own, shorter, leash: clicking Refresh a lot must not push
        // the record of a deleted branch or an agent update out of the audit trail.
        var keep = isRefresh ? KeepRefreshes : Math.Max(50, Options.KeepLast);
        var cutoff = await db.AgentActions
            .Where(OfSort(isRefresh))
            .OrderByDescending(a => a.Id)
            .Select(a => (int?)a.Id)
            .Skip(keep)
            .FirstOrDefaultAsync(ct);
        if (cutoff is { } oldest)
        {
            await db.AgentActions.Where(a => a.Id <= oldest).Where(OfSort(isRefresh)).ExecuteDeleteAsync(ct);
        }

        logger.LogInformation("Action {Id} queued for {Machine} by {User}: {Command}", entity.Id, entity.MachineId, entity.QueuedBy, entity.CommandLine);
        notifier.Notify(DashboardChange.Actions);
        signals.Pulse(entity.MachineId); // wakes the agent's long poll, if it has one open
        return new QueueOutcome(entity, null);
    }

    /// <summary>
    /// A claim that waits: when nothing is queued for the machine, holds on until something is, or
    /// <paramref name="wait"/> has passed, or the hub is stopping, and then answers (with nothing, in
    /// the last two cases). Nothing is ever handed out to a request that has gone away.
    /// </summary>
    public async Task<IReadOnlyList<PendingAgentAction>> ClaimAsync(string machineId, TimeSpan wait, CancellationToken ct = default, CancellationToken stopping = default)
    {
        var deadline = time.GetUtcNow() + wait;
        while (true)
        {
            // Taken before looking, so an action queued between the look and the wait still wakes this.
            var queued = signals.Next(machineId);
            var claimed = await ClaimAsync(machineId, ct);
            var remaining = deadline - time.GetUtcNow();
            if (claimed.Count > 0 || remaining <= TimeSpan.Zero || stopping.IsCancellationRequested)
            {
                return claimed;
            }

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping);
            try
            {
                await queued.WaitAsync(remaining, time, cancel.Token);
            }
            catch (TimeoutException)
            {
                return [];
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return [];
            }
        }
    }

    /// <summary>
    /// Hands the machine's queued actions to its agent and marks them claimed. Anything returned here
    /// is never returned again, whatever happens to the agent afterwards.
    /// </summary>
    public async Task<IReadOnlyList<PendingAgentAction>> ClaimAsync(string machineId, CancellationToken ct = default)
    {
        await ExpireStaleAsync(ct);

        // An agent polls when it accepts git actions, update requests, or both. It is only ever handed
        // the kinds its last snapshot said it accepts: one that polls for updates alone gets no git
        // action, and one restarted with a switch turned off gets nothing of that kind (the rows expire).
        if (snapshots.Get(machineId) is not { } machine
            || (!Allows(machine, AgentActionKind.Fetch) && !Allows(machine, AgentActionKind.UpdateAgent) && !Allows(machine, AgentActionKind.Refresh)))
        {
            return [];
        }

        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var queued = (await db.AgentActions.AsNoTracking()
                .Where(a => a.MachineId == machine.MachineId && a.Status == AgentActionStatus.Queued)
                .OrderBy(a => a.Id)
                .ToListAsync(ct))
            .Where(a => Allows(machine, a.Kind))
            .Take(MaxPerClaim)
            .ToList();

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

        await SettleTombstonesAsync(db, id, result.Succeeded, now, ct);
        await T3.T3Settlement.SettleAsync(db, id, result.Succeeded, result.Thread, output, now, ct);
        logger.LogInformation("Action {Id} on {Machine} {Status} (exit {Exit})", id, owner, status, result.ExitCode);
        notifier.Notify(DashboardChange.Actions);
        return true;
    }

    /// <summary>Expires queued actions nobody claimed and fails claimed ones that never reported back. Returns how many changed.</summary>
    public async Task<int> ExpireStaleAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var failed = new List<int>();
        TimeSpan LeaseOf(AgentActionKind kind) => TimeSpan.FromMinutes(Math.Max(1, kind switch
        {
            AgentActionKind.UpdateAgent => UpdateOptions.LeaseMinutes,
            AgentActionKind.Clone => Math.Max(Options.CloneLeaseMinutes, Options.LeaseMinutes),
            _ => Options.LeaseMinutes,
        }));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var open = await db.AgentActions.AsNoTracking()
            .Where(a => a.Status == AgentActionStatus.Queued || a.Status == AgentActionStatus.Claimed)
            .ToListAsync(ct);

        var changed = 0;
        foreach (var row in open)
        {
            if (row.Status == AgentActionStatus.Queued && row.ExpiresAt <= now)
            {
                failed.Add(row.Id);
                changed += await db.AgentActions
                    .Where(a => a.Id == row.Id && a.Status == AgentActionStatus.Queued)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.Status, AgentActionStatus.Expired)
                        .SetProperty(a => a.CompletedAt, now)
                        .SetProperty(a => a.Output, "The agent did not pick this up in time, so it was not run."), ct);
            }
            else if (row.Status == AgentActionStatus.Claimed && row.ClaimedAt + LeaseOf(row.Kind) <= now)
            {
                failed.Add(row.Id);
                changed += await db.AgentActions
                    .Where(a => a.Id == row.Id && a.Status == AgentActionStatus.Claimed)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.Status, AgentActionStatus.Failed)
                        .SetProperty(a => a.CompletedAt, now)
                        .SetProperty(a => a.Output, "The agent took this action but never reported a result. It may or may not have run; it will not be retried."), ct);
            }
        }

        // A removal that expired, or whose result never came, leaves no tombstone: nothing says it happened.
        if (failed.Count > 0)
        {
            await db.RemovedProjects.Where(r => failed.Contains(r.ActionId) && r.RemovedAt == null).ExecuteDeleteAsync(ct);
            await T3.T3Settlement.FailAsync(db, failed, "The agent did not start the thread in time, or never said whether it had.", now, ct);
        }

        if (changed > 0)
        {
            notifier.Notify(DashboardChange.Actions);
        }

        return changed;
    }

    /// <summary>The audit trail, newest first: every action, refreshes included.</summary>
    public async Task<IReadOnlyList<AgentActionEntity>> RecentAsync(int take = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AgentActions.AsNoTracking().OrderByDescending(a => a.Id).Take(take).ToListAsync(ct);
    }

    /// <summary>The audit trail without refreshes (fixes and agent updates), or only the refreshes. Newest first.</summary>
    public async Task<IReadOnlyList<AgentActionEntity>> RecentAsync(bool refreshes, int take, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AgentActions.AsNoTracking().Where(OfSort(refreshes)).OrderByDescending(a => a.Id).Take(take).ToListAsync(ct);
    }

    private static System.Linq.Expressions.Expression<Func<AgentActionEntity, bool>> OfSort(bool refreshes) =>
        refreshes ? a => a.Kind == AgentActionKind.Refresh : a => a.Kind != AgentActionKind.Refresh;

    /// <summary>
    /// The newest refresh per (machine, entry path) that is still worth showing beside its button:
    /// on its way, or finished in the last few minutes.
    /// </summary>
    public async Task<IReadOnlyDictionary<(string MachineId, string Path), AgentActionEntity>> ShownRefreshesAsync(CancellationToken ct = default)
    {
        var cutoff = time.GetUtcNow() - RefreshShownFor;
        var result = new Dictionary<(string, string), AgentActionEntity>();
        foreach (var row in await RecentAsync(refreshes: true, KeepRefreshes, ct))
        {
            if (row.IsPending || row.CompletedAt >= cutoff)
            {
                result.TryAdd((row.MachineId.ToLowerInvariant(), row.RepoPath), row); // newest first, so the first one wins
            }
        }

        return result;
    }

    /// <summary>
    /// The latest action per (machine, action) that is still worth showing on a finding: anything
    /// pending, and anything that finished in the last few minutes.
    /// </summary>
    public async Task<IReadOnlyDictionary<(string MachineId, string ActionJson), AgentActionEntity>> ShownOnFindingsAsync(CancellationToken ct = default)
    {
        var cutoff = time.GetUtcNow() - ShownOnFindingFor;
        var result = new Dictionary<(string, string), AgentActionEntity>();
        foreach (var row in await RecentAsync(refreshes: false, 200, ct))
        {
            if (row.IsPending || row.CompletedAt >= cutoff)
            {
                result.TryAdd((row.MachineId.ToLowerInvariant(), row.ActionJson), row); // newest first, so the first one wins
            }
        }

        return result;
    }

    /// <summary>
    /// What a finished action means for the removed-projects list: a removal that worked confirms its
    /// tombstone, one that did not drops it, and a clone that worked ends the tombstone for that folder.
    /// </summary>
    private static async Task SettleTombstonesAsync(DevDashDbContext db, int actionId, bool succeeded, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.AgentActions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == actionId, ct) is not { } row)
        {
            return;
        }

        if (row.Kind == AgentActionKind.RemoveProject)
        {
            if (succeeded)
            {
                // An older tombstone for the same folder is replaced by this one.
                await db.RemovedProjects.Where(r => r.MachineId == row.MachineId && r.Path == row.RepoPath && r.ActionId != actionId).ExecuteDeleteAsync(ct);
                await db.RemovedProjects.Where(r => r.ActionId == actionId).ExecuteUpdateAsync(s => s.SetProperty(r => r.RemovedAt, now), ct);
            }
            else
            {
                await db.RemovedProjects.Where(r => r.ActionId == actionId).ExecuteDeleteAsync(ct);
            }
        }
        else if (row.Kind == AgentActionKind.Clone && succeeded)
        {
            await db.RemovedProjects.Where(r => r.MachineId == row.MachineId && r.Path == row.RepoPath).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>
    /// Why the hub will not ask for this removal, or null. The same rule the agent applies again after
    /// its own fetch and scan: safe, or safe with every caveat acknowledged, and unchanged since it was shown.
    /// </summary>
    private static string? RemoveRefusal(MachineView machine, AgentAction action, CloneSafety? assessed)
    {
        if (assessed is null)
        {
            return $"{machine.Machine.Hostname} no longer reports a repository at {action.RepoPath}.";
        }

        var safety = assessed.Safety;
        if (!safety.IsRemovable)
        {
            return $"{action.RepoPath} is not safe to remove: {string.Join("; ", safety.Reasons.Select(r => r.Text))}.";
        }

        if (safety.Fingerprint != action.ExpectedFingerprint)
        {
            return $"{action.RepoPath} has changed since this page was drawn. Look at it again before removing it.";
        }

        var acknowledged = (action.AcknowledgedCaveats ?? []).ToHashSet(StringComparer.Ordinal);
        return safety.Caveats.SelectMany(c => c.Names).Any(n => !acknowledged.Contains(n))
            ? $"{action.RepoPath} has ignored files that have not been acknowledged."
            : null;
    }

    /// <summary>
    /// Why the hub will not ask for this clone, or null. It must put back a project this hub saw removed
    /// from that folder on that machine, the folder must be free, and the URL must be one of that
    /// project's remotes: recorded with the removal, or reported now by a machine that still has the repo.
    /// </summary>
    private async Task<string?> CloneRefusalAsync(MachineView machine, AgentAction action, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tombstone = await db.RemovedProjects.AsNoTracking()
            .Where(r => r.MachineId == machine.MachineId && r.Path == action.RepoPath && r.RemovedAt != null)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (tombstone is null)
        {
            return $"Nothing was removed from {action.RepoPath} on {machine.Machine.Hostname}, so there is nothing to clone back.";
        }

        if (RepoIdentity.Flatten(machine.Snapshot.Entries).Any(e => e.Path == action.RepoPath))
        {
            return $"{machine.Machine.Hostname} reports something at {action.RepoPath} again.";
        }

        return TombstoneStore.AllowedUrls(tombstone, snapshots.Machines).Contains(action.Url!, StringComparer.Ordinal)
            ? null
            : "That URL is not a remote of the project that was removed.";
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

/// <summary>
/// Wakes an agent's long poll when something is queued for its machine. In memory only: a poll that
/// misses a pulse (it cannot, see <see cref="ActionQueue.ClaimAsync(string, TimeSpan, CancellationToken, CancellationToken)"/>)
/// would still find the action on its next attempt.
/// </summary>
public sealed class ActionSignals
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> _waiting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Completes the next time something is queued for the machine.</summary>
    public Task Next(string machineId) =>
        _waiting.GetOrAdd(machineId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void Pulse(string machineId)
    {
        if (_waiting.TryRemove(machineId, out var waiting))
        {
            waiting.TrySetResult();
        }
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
