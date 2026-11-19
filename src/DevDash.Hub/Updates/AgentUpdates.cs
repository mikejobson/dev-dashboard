using DevDash.Contracts;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Updates;

public enum AgentUpdateState
{
    /// <summary>No release is known, or the agent's version is not a release version.</summary>
    Unknown,
    UpToDate,

    /// <summary>A newer release exists.</summary>
    Behind,
}

/// <summary>Where one machine's agent stands against the latest release.</summary>
/// <param name="CanUpdate">True when the dashboard may queue an update for it right now.</param>
/// <param name="WhyNot">When behind but <paramref name="CanUpdate"/> is false: what to do about it.</param>
public sealed record MachineUpdateView(AgentUpdateState State, string? Latest, bool CanUpdate, string? WhyNot)
{
    public bool Behind => State == AgentUpdateState.Behind;
}

/// <summary>
/// Compares each agent with the latest release and queues updates. The hub's whole part in an update
/// is to say "version X, please": it names a version it saw on GitHub and nothing else. The agent
/// decides whether to accept (its own <c>Agent:Updates:Mode</c>), downloads the release from GitHub
/// itself and verifies it against the key it was built with.
/// </summary>
public sealed class AgentUpdates(ReleaseStore releases, SnapshotStore snapshots, ActionQueue queue, IOptionsMonitor<HubOptions> options)
{
    public bool Enabled => options.CurrentValue.Updates.Enabled;

    public LatestRelease? Latest => releases.Current;

    public MachineUpdateView For(MachineView machine)
    {
        if (releases.Current is not { } latest || !SemVer.TryParse(latest.Version, out var latestVersion))
        {
            return new MachineUpdateView(AgentUpdateState.Unknown, null, false, null);
        }

        if (!SemVer.TryParse(machine.Machine.AgentVersion, out var agentVersion))
        {
            return new MachineUpdateView(AgentUpdateState.Unknown, latest.Version, false, null);
        }

        if (agentVersion >= latestVersion)
        {
            return new MachineUpdateView(AgentUpdateState.UpToDate, latest.Version, false, null);
        }

        var whyNot = WhyNot(machine, latest);
        return new MachineUpdateView(AgentUpdateState.Behind, latest.Version, whyNot is null, whyNot);
    }

    /// <summary>Why the dashboard cannot update this (behind) machine, or null when it can.</summary>
    private string? WhyNot(MachineView machine, LatestRelease latest)
    {
        if (!Enabled)
        {
            return "Updates from the dashboard are switched off on this hub (Hub:Updates:Enabled).";
        }

        if (!latest.Signed)
        {
            return $"Release {latest.Version} is not signed, so agents will not install it.";
        }

        return AgentUpdateModes.Normalize(machine.Machine.UpdateMode) switch
        {
            // An agent from before self-update says nothing about it. It cannot update itself: reinstall it once.
            null => "This agent predates self-update. Run the install one-liner on that machine once; after that it can be updated from here.",
            AgentUpdateModes.Off => "Updates are off on this machine (Agent:Updates:Mode). Update it there with the install one-liner, or set the mode to notify.",
            _ => snapshots.IsOnline(machine) ? null : "The machine is offline. It can be updated when its agent reports again.",
        };
    }

    /// <summary>Machines that are behind and can be updated from the dashboard now.</summary>
    public IReadOnlyList<MachineView> Updatable() => snapshots.Machines.Where(m => For(m).CanUpdate).ToList();

    public int BehindCount() => snapshots.Machines.Count(m => For(m).Behind);

    /// <summary>Queues an update to the latest release for one machine, on behalf of the browser user who confirmed it.</summary>
    public Task<QueueOutcome> QueueAsync(string machineId, string queuedBy, CancellationToken ct = default) =>
        releases.Current is { } latest
            ? queue.QueueAsync(machineId, AgentAction.UpdateAgent(latest.Version), queuedBy, ct: ct)
            : Task.FromResult(new QueueOutcome(null, "The hub does not know of a release yet."));

    /// <summary>The newest update action per machine that is still on its way.</summary>
    public async Task<IReadOnlyDictionary<string, AgentActionEntity>> PendingAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, AgentActionEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in await queue.RecentAsync(refreshes: false, 200, ct))
        {
            if (row.Kind == AgentActionKind.UpdateAgent && row.IsPending)
            {
                result.TryAdd(row.MachineId, row);
            }
        }

        return result;
    }
}
