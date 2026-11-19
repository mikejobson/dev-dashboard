using System.Globalization;
using DevDash.Contracts;
using DevDash.Core;
using DevDash.Hub.Data;
using Microsoft.AspNetCore.Components.Authorization;

namespace DevDash.Hub.Services;

/// <summary>Whether the dashboard may ask a machine to refresh an entry right now, and if not, what to tell the user.</summary>
public sealed record RefreshAvailability(bool Available, string? WhyNot);

/// <summary>
/// Which agents can be asked to rescan one entry now. Two things are needed, and both come from the
/// machine's own last snapshot: an agent new enough to know the Refresh action
/// (<see cref="AgentActions.RefreshMinAgentVersion"/>), and one that checks the hub for requests at
/// all, which an agent with updates off and actions off does not. <see cref="MachineInfo.AcceptsRefresh"/>
/// says both at once; the version only decides which of the two explanations to give.
/// A refresh is read-only, so unlike a fix it does not need <c>--enable-actions</c> and is not confirmed.
/// </summary>
public static class RefreshGate
{
    /// <summary>Why this agent cannot take a refresh whatever its state, or null when it can.</summary>
    public static string? WhyUnsupported(MachineInfo machine)
    {
        if (machine.AcceptsRefresh)
        {
            return null;
        }

        var minimum = SemVer.ParseOrNull(AgentActions.RefreshMinAgentVersion)!;
        var version = SemVer.ParseOrNull(machine.AgentVersion);
        if (version is null || version.CompareTo(minimum) < 0)
        {
            var on = string.IsNullOrWhiteSpace(machine.AgentVersion) ? "" : $" (it is on {machine.AgentVersion})";
            return $"Update the agent on {machine.Hostname} to {AgentActions.RefreshMinAgentVersion} or newer to refresh from here{on}.";
        }

        return $"The agent on {machine.Hostname} doesn't check for requests: updates are off and actions are off.";
    }

    public static RefreshAvailability For(MachineInfo machine, bool online)
    {
        if (WhyUnsupported(machine) is { } why)
        {
            return new RefreshAvailability(false, why);
        }

        return online
            ? new RefreshAvailability(true, null)
            : new RefreshAvailability(false, $"{machine.Hostname} is offline. It can be refreshed when its agent reports again.");
    }
}

public enum RefreshPhase
{
    /// <summary>Nothing recent to show.</summary>
    Idle,

    /// <summary>Queued, or the agent has it.</summary>
    Refreshing,
    Done,
    Failed,
}

/// <summary>What to show beside a Refresh button, worked out from the newest refresh action for that entry.</summary>
/// <param name="Text">Short, for the page: "Refreshing on work-mac…", "Refreshed 3 s ago".</param>
/// <param name="Detail">Longer, for a tooltip: what the agent reported.</param>
/// <param name="At">When it finished.</param>
public sealed record RefreshStatus(RefreshPhase Phase, string Text, string? Detail = null, DateTimeOffset? At = null, string? Machine = null)
{
    public static readonly RefreshStatus Idle = new(RefreshPhase.Idle, "");

    public bool InFlight => Phase == RefreshPhase.Refreshing;

    /// <summary>
    /// The status of one refresh action. The agent pushes the snapshot before it reports, so by the
    /// time an action reads Succeeded the page already shows the new state.
    /// </summary>
    public static RefreshStatus Of(AgentActionEntity? action, DateTimeOffset now)
    {
        if (action is null)
        {
            return Idle;
        }

        var machine = string.IsNullOrEmpty(action.MachineName) ? action.MachineId : action.MachineName;
        return action.Status switch
        {
            AgentActionStatus.Queued => new RefreshStatus(RefreshPhase.Refreshing, $"Refreshing on {machine}…", $"Asked {Ago(action.QueuedAt, now)}; waiting for the agent on {machine} to pick it up.", null, machine),
            AgentActionStatus.Claimed => new RefreshStatus(RefreshPhase.Refreshing, $"Refreshing on {machine}…", $"The agent on {machine} is rescanning.", null, machine),
            AgentActionStatus.Succeeded => new RefreshStatus(RefreshPhase.Done, $"Refreshed {Ago(action.CompletedAt, now)}", action.Output, action.CompletedAt, machine),
            AgentActionStatus.Expired => new RefreshStatus(RefreshPhase.Failed, $"No answer from {machine}", action.Output, action.CompletedAt, machine),
            _ => new RefreshStatus(RefreshPhase.Failed, $"Refresh failed on {machine}", action.Output, action.CompletedAt, machine),
        };
    }

    /// <summary>
    /// One status for a button that refreshes several entries (a repo on every machine): in flight
    /// while any is, then the first failure, then done. "Refreshed 3 s ago" on its own means every
    /// entry was; otherwise it names the machines that were.
    /// </summary>
    public static RefreshStatus Combine(IReadOnlyList<RefreshStatus> statuses)
    {
        var shown = statuses.Where(s => s.Phase != RefreshPhase.Idle).ToList();
        if (shown.Count == 0)
        {
            return Idle;
        }

        static string Names(IEnumerable<RefreshStatus> list) => string.Join(", ", list.Select(s => s.Machine).Where(m => m is not null).Distinct(StringComparer.OrdinalIgnoreCase));

        var running = shown.Where(s => s.InFlight).ToList();
        if (running.Count > 0)
        {
            return new RefreshStatus(RefreshPhase.Refreshing, $"Refreshing on {Names(running)}…", string.Join("\n", running.Select(s => s.Detail)));
        }

        var failed = shown.Where(s => s.Phase == RefreshPhase.Failed).ToList();
        if (failed.Count > 0)
        {
            return failed.Count == 1 ? failed[0] : failed[0] with { Text = $"Refresh failed on {Names(failed)}", Detail = string.Join("\n\n", failed.Select(s => $"{s.Machine}: {s.Detail}")) };
        }

        // The least recent one: "refreshed 3 s ago" is only true of all of them from then.
        var oldest = shown.MinBy(s => s.At)!;
        return shown.Count == statuses.Count
            ? oldest with { Detail = string.Join("\n", shown.Select(s => s.Detail)) }
            : oldest with { Text = $"Refreshed on {Names(shown)}{oldest.Text["Refreshed".Length..]}", Detail = string.Join("\n", shown.Select(s => s.Detail)) };
    }

    /// <summary>Finer than the rest of the dashboard's "just now": a refresh takes seconds, and that is the point.</summary>
    public static string Ago(DateTimeOffset? when, DateTimeOffset now)
    {
        var seconds = when is { } value ? Math.Max(0, (long)(now - value).TotalSeconds) : 0;
        return seconds < 1 ? "just now"
            : seconds < 60 ? string.Create(CultureInfo.InvariantCulture, $"{seconds} s ago")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} min ago");
    }
}

/// <summary>One thing to ask one agent: rescan the top-level entry at <paramref name="Path"/>.</summary>
public sealed record RefreshTarget(MachineView Machine, string Path);

/// <summary>Works out what a Refresh button refreshes. An agent rescans whole top-level entries, so that is what is asked for.</summary>
public static class RefreshTargets
{
    /// <summary>The path of the top-level entry that is, or holds, the entry at <paramref name="path"/>. Null when the snapshot has nothing there.</summary>
    public static string? TopLevel(MachineSnapshot snapshot, string path)
    {
        foreach (var entry in snapshot.Entries)
        {
            if (entry.Path == path || RepoIdentity.Flatten(entry.Children).Any(child => child.Path == path))
            {
                return entry.Path;
            }
        }

        return null;
    }

    /// <summary>The entry to refresh for one working tree or folder.</summary>
    public static RefreshTarget? For(CloneView clone) =>
        TopLevel(clone.Machine.Snapshot, clone.Entry.Path) is { } path ? new RefreshTarget(clone.Machine, path) : null;

    /// <summary>
    /// Everything to refresh for a repo: each clone's entry on each machine, and for a linked worktree
    /// the entry of its main working tree too (they share branches and one fetch), each entry once.
    /// </summary>
    public static IReadOnlyList<RefreshTarget> For(IEnumerable<CloneView> clones)
    {
        var targets = new List<RefreshTarget>();
        var seen = new HashSet<(string, string)>();
        void Add(MachineView machine, string? path)
        {
            if (path is not null && seen.Add((machine.MachineId.ToLowerInvariant(), path)))
            {
                targets.Add(new RefreshTarget(machine, path));
            }
        }

        foreach (var clone in clones)
        {
            Add(clone.Machine, TopLevel(clone.Machine.Snapshot, clone.Entry.Path));
            if (clone.Git is { IsLinkedWorktree: true, Worktrees: [var main, ..] })
            {
                Add(clone.Machine, TopLevel(clone.Machine.Snapshot, main.Path));
            }
        }

        return targets;
    }
}

/// <summary>Queues refreshes and says how the recent ones went.</summary>
public sealed class RefreshService(ActionQueue queue, SnapshotStore snapshots)
{
    public RefreshAvailability For(MachineView machine) => RefreshGate.For(machine.Machine, snapshots.IsOnline(machine));

    public Task<IReadOnlyDictionary<(string MachineId, string Path), AgentActionEntity>> ShownAsync(CancellationToken ct = default) => queue.ShownRefreshesAsync(ct);

    /// <summary>Asks the machine's agent to rescan one entry. Asking twice while one is on its way is one request.</summary>
    public Task<QueueOutcome> QueueAsync(RefreshTarget target, string queuedBy, CancellationToken ct = default) =>
        queue.QueueAsync(target.Machine.MachineId, AgentAction.Refresh(target.Path), queuedBy, ct: ct);
}

/// <summary>
/// What a page needs to show Refresh buttons: who is asking, the recent refreshes, and queueing.
/// One per circuit; the page reloads it whenever the dashboard changes.
/// </summary>
public sealed class RefreshBoard(RefreshService refreshes, AuthenticationStateProvider auth, TimeProvider time)
{
    private IReadOnlyDictionary<(string MachineId, string Path), AgentActionEntity> _shown = new Dictionary<(string, string), AgentActionEntity>();

    /// <summary>The last thing that could not be queued, for the page to show.</summary>
    public string? Error { get; private set; }

    public async Task LoadAsync() => _shown = await refreshes.ShownAsync();

    public RefreshAvailability For(MachineView machine) => refreshes.For(machine);

    public RefreshStatus StatusOf(RefreshTarget? target) =>
        target is not null && _shown.TryGetValue((target.Machine.MachineId.ToLowerInvariant(), target.Path), out var action)
            ? RefreshStatus.Of(action, time.GetUtcNow())
            : RefreshStatus.Idle;

    /// <summary>True while a status on the page changes by the second: something is in flight, or finished under a minute ago.</summary>
    public bool Ticking
    {
        get
        {
            var recent = time.GetUtcNow().AddMinutes(-1);
            return _shown.Values.Any(a => a.IsPending || a.CompletedAt >= recent);
        }
    }

    /// <summary>Queues a refresh for each target whose machine can take one, and reloads. Not confirmed first: a refresh changes nothing.</summary>
    public async Task QueueAsync(IEnumerable<RefreshTarget> targets)
    {
        var user = (await auth.GetAuthenticationStateAsync()).User;
        var login = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";
        var errors = new List<string>();
        foreach (var target in targets.Where(t => For(t.Machine).Available && !StatusOf(t).InFlight))
        {
            var outcome = await refreshes.QueueAsync(target, login);
            if (!outcome.Ok)
            {
                errors.Add(outcome.Error!);
            }
        }

        Error = errors.Count == 0 ? null : string.Join(" ", errors);
        await LoadAsync();
    }
}
