using DevDash.Contracts;
using DevDash.Core.Cleanup;
using DevDash.Hub.Data;
using DevDash.Hub.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Services;

public sealed record FindingView(Finding Finding, FindingStatus? Status, DateTimeOffset? SnoozedUntil)
{
    private static readonly IReadOnlySet<string> NoMachines = new HashSet<string>();
    private static readonly IReadOnlyDictionary<(string, string), AgentActionEntity> NoActions = new Dictionary<(string, string), AgentActionEntity>();

    /// <summary>Not dismissed and not currently snoozed.</summary>
    public bool IsActive => Status is null;

    /// <summary>Machines whose agent has actions enabled and is online.</summary>
    public IReadOnlySet<string> RunnableMachines { get; init; } = NoMachines;

    /// <summary>Pending and just-finished agent actions, by machine and action.</summary>
    public IReadOnlyDictionary<(string MachineId, string ActionJson), AgentActionEntity> RecentActions { get; init; } = NoActions;

    /// <summary>True when the command can be queued for its machine's agent: a Run button is only shown then.</summary>
    public bool CanRun(FindingCommand command) =>
        IsActive
        && command.Action is not null
        && Finding.MachineIdFor(command) is { } machine
        && RunnableMachines.Contains(machine)
        && Supports(machine, command.Action.Kind);

    /// <summary>Whether a machine's agent knows a kind of action: the kinds from 0.6.0 are not offered to an older agent.</summary>
    public Func<string, AgentActionKind, bool> Supports { get; init; } = (_, _) => true;

    /// <summary>The latest queued, running or just-finished action for this command, if any.</summary>
    public AgentActionEntity? ActionFor(FindingCommand command) =>
        command.Action is { } action
        && Finding.MachineIdFor(command) is { } machine
        && RecentActions.TryGetValue((machine.ToLowerInvariant(), AgentActions.Key(action)), out var row)
            ? row
            : null;
}

/// <summary>
/// Runs the cleanup rules over the current state and applies snooze/dismiss decisions. It only ever
/// reports: suggested commands are shown for copying. Running one is a separate, confirmed step
/// through <see cref="ActionQueue"/>, and only for the typed actions a rule attached.
/// </summary>
public sealed class FindingsService(
    SnapshotStore snapshots,
    GitHubStore github,
    CleanupEngine engine,
    IDbContextFactory<DevDashDbContext> dbFactory,
    DashboardNotifier notifier,
    ActionQueue actions,
    IOptionsMonitor<HubOptions> options,
    TimeProvider time)
{
    public IReadOnlyList<ICleanupRule> Rules => engine.Rules;

    /// <summary>The rule's heading with the configured thresholds in it ("older than 14 days").</summary>
    public string TitleOf(ICleanupRule rule) => rule.TitleFor(options.CurrentValue.Cleanup);

    public async Task<IReadOnlyList<FindingView>> GetAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var gh = github.Current;
        var findings = engine.Evaluate(new CleanupContext
        {
            Machines = snapshots.Machines.Select(m => m.Snapshot).ToList(),
            RemoteRepos = gh.LocalRemotes,
            PullRequests = gh.PullRequests,
            Now = now,
            Options = options.CurrentValue.Cleanup,
        });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var states = (await db.FindingStates.AsNoTracking().ToListAsync(ct)).ToDictionary(s => s.Fingerprint, StringComparer.Ordinal);

        var runnable = actions.RunnableMachines();
        var recent = await actions.ShownOnFindingsAsync(ct);

        return findings
            .Select(f =>
            {
                if (!states.TryGetValue(f.Fingerprint, out var state))
                {
                    return new FindingView(f, null, null);
                }

                // An expired snooze is the same as no decision.
                if (state.Status == FindingStatus.Snoozed && (state.SnoozedUntil is null || state.SnoozedUntil <= now))
                {
                    return new FindingView(f, null, null);
                }

                return new FindingView(f, state.Status, state.SnoozedUntil);
            })
            .Select(v => v with { RunnableMachines = runnable, RecentActions = recent, Supports = actions.CanRun })
            .OrderByDescending(v => v.Finding.Severity)
            .ThenBy(v => v.Finding.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task SnoozeAsync(Finding finding, TimeSpan duration, CancellationToken ct = default) =>
        SetAsync(finding.Fingerprint, finding.RuleId, finding.Title, FindingStatus.Snoozed, time.GetUtcNow() + duration, ct);

    public Task DismissAsync(Finding finding, CancellationToken ct = default) =>
        SetAsync(finding.Fingerprint, finding.RuleId, finding.Title, FindingStatus.Dismissed, null, ct);

    /// <summary>Snoozes a whole group at once: one save, one notification.</summary>
    public Task<int> SnoozeManyAsync(IEnumerable<Finding> findings, TimeSpan duration, CancellationToken ct = default) =>
        SetManyAsync(findings, FindingStatus.Snoozed, time.GetUtcNow() + duration, ct);

    /// <summary>Dismisses a whole group at once: one save, one notification.</summary>
    public Task<int> DismissManyAsync(IEnumerable<Finding> findings, CancellationToken ct = default) =>
        SetManyAsync(findings, FindingStatus.Dismissed, null, ct);

    private async Task<int> SetManyAsync(IEnumerable<Finding> findings, FindingStatus status, DateTimeOffset? until, CancellationToken ct)
    {
        var wanted = findings.DistinctBy(f => f.Fingerprint, StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var fingerprints = wanted.Select(f => f.Fingerprint).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.FindingStates.Where(s => fingerprints.Contains(s.Fingerprint)).ToDictionaryAsync(s => s.Fingerprint, StringComparer.Ordinal, ct);
        foreach (var finding in wanted)
        {
            if (!existing.TryGetValue(finding.Fingerprint, out var state))
            {
                state = new FindingStateEntity { Fingerprint = finding.Fingerprint };
                db.FindingStates.Add(state);
            }

            state.Status = status;
            state.SnoozedUntil = until;
            state.RuleId = finding.RuleId;
            state.Title = finding.Title;
            state.UpdatedAt = now;
        }

        // One SaveChanges is one transaction: the whole group changes or none of it does.
        await db.SaveChangesAsync(ct);
        notifier.Notify(DashboardChange.Findings);
        return wanted.Count;
    }

    public async Task<bool> SetByFingerprintAsync(string fingerprint, FindingStatus status, TimeSpan? snooze, CancellationToken ct = default)
    {
        var finding = (await GetAsync(ct)).FirstOrDefault(v => v.Finding.Fingerprint == fingerprint)?.Finding;
        if (finding is null)
        {
            return false;
        }

        await SetAsync(fingerprint, finding.RuleId, finding.Title, status,
            status == FindingStatus.Snoozed ? time.GetUtcNow() + (snooze ?? TimeSpan.FromDays(7)) : null, ct);
        return true;
    }

    public async Task RestoreAsync(string fingerprint, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.FindingStates.Where(s => s.Fingerprint == fingerprint).ExecuteDeleteAsync(ct);
        notifier.Notify(DashboardChange.Findings);
    }

    private async Task SetAsync(string fingerprint, string ruleId, string title, FindingStatus status, DateTimeOffset? until, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = await db.FindingStates.FirstOrDefaultAsync(s => s.Fingerprint == fingerprint, ct);
        if (state is null)
        {
            state = new FindingStateEntity { Fingerprint = fingerprint };
            db.FindingStates.Add(state);
        }

        state.Status = status;
        state.SnoozedUntil = until;
        state.RuleId = ruleId;
        state.Title = title;
        state.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        notifier.Notify(DashboardChange.Findings);
    }
}
