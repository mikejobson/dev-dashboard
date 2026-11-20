using System.Text.Json;
using DevDash.Contracts;
using DevDash.Core;
using DevDash.Hub.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DevDash.Hub.Services;

/// <summary>A removed project as the pages show it: the tombstone, its machine, and the clone that would put it back.</summary>
public sealed record RemovedProjectView(RemovedProjectEntity Tombstone, MachineView? Machine, IReadOnlyList<RemoteInfo> Remotes)
{
    /// <summary>The URL a re-clone uses: <c>origin</c>'s, else the first remote's. Null when the project had none recorded.</summary>
    public RemoteInfo? Remote => Remotes.FirstOrDefault(r => r.Name == "origin") ?? Remotes.FirstOrDefault();

    /// <summary>The typed action that clones it back into the folder it was removed from. Null when there is no URL to clone.</summary>
    public AgentAction? CloneAction => Remote is { } remote ? AgentAction.CloneInto(Tombstone.Path, remote.Url, remote.Name) : null;

    /// <summary>True when it was archived before it went to the trash.</summary>
    public bool IsArchived => Tombstone.IsArchived;

    /// <summary>The manifest the agent wrote beside the archive. Null for a plain removal, or before the agent reported it.</summary>
    public ArchiveManifest? Manifest => TombstoneStore.ManifestOf(Tombstone);

    /// <summary>The typed action that unpacks the archive into <paramref name="target"/>. Null when there is no archive to restore.</summary>
    public AgentAction? RestoreAction(string target) =>
        Tombstone is { ArchivePath: { } archive, ArchiveSha256: { } sha } ? AgentAction.RestoreArchive(archive, sha, target) : null;
}

/// <summary>
/// The removed-projects list: every project the dashboard had an agent move to the trash and that is
/// not back yet. The rows are written and settled by <see cref="ActionQueue"/>, as part of queueing
/// and completing the removal; this reads them.
/// </summary>
public sealed class TombstoneStore(IDbContextFactory<DevDashDbContext> dbFactory, SnapshotStore snapshots, DashboardNotifier notifier)
{
    /// <summary>
    /// Confirmed removals, newest first. One whose machine reports something at that path again (cloned
    /// back by hand, or the folder dragged out of the trash) is left out: it is not removed any more.
    /// </summary>
    public async Task<IReadOnlyList<RemovedProjectView>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.RemovedProjects.AsNoTracking().Where(r => r.RemovedAt != null).OrderByDescending(r => r.Id).ToListAsync(ct);
        var result = new List<RemovedProjectView>(rows.Count);
        foreach (var row in rows)
        {
            var machine = snapshots.Get(row.MachineId);
            // An archived project stays listed whatever is at its old path: the archive is still there to restore elsewhere.
            if (machine is not null && !row.IsArchived && RepoIdentity.Flatten(machine.Snapshot.Entries).Any(e => e.Path == row.Path))
            {
                continue;
            }

            result.Add(new RemovedProjectView(row, machine, RemotesOf(row)));
        }

        return result;
    }

    /// <summary>Drops a tombstone: the project stays wherever it is, the dashboard just stops listing it.</summary>
    public async Task ForgetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.RemovedProjects.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
        notifier.Notify(DashboardChange.Actions);
    }

    public static ArchiveManifest? ManifestOf(RemovedProjectEntity row)
    {
        try
        {
            return row.ManifestJson is { } json ? JsonSerializer.Deserialize<ArchiveManifest>(json, SnapshotSchema.JsonOptions) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<RemoteInfo> RemotesOf(RemovedProjectEntity row)
    {
        try
        {
            return JsonSerializer.Deserialize<List<RemoteInfo>>(row.RemotesJson, SnapshotSchema.JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Every URL the hub will ask an agent to clone a removed project from: the remotes recorded with the
    /// removal, and the remotes any machine reports now for the same hosted repo. Nothing else, so a
    /// tombstone can never be turned into "clone this URL I just typed".
    /// </summary>
    public static IReadOnlyList<string> AllowedUrls(RemovedProjectEntity row, IEnumerable<MachineView> machines)
    {
        var urls = RemotesOf(row).Select(r => r.Url).ToList();
        if (row.RepoKey.Length > 0 && !RepoIdentity.IsLocalOnly(row.RepoKey))
        {
            urls.AddRange(machines
                .SelectMany(m => RepoIdentity.Flatten(m.Snapshot.Entries))
                .Where(e => e.Git is not null && string.Equals(RepoIdentity.PrimaryRemote(e.Git)?.Normalized, row.RepoKey, StringComparison.OrdinalIgnoreCase))
                .SelectMany(e => e.Git!.Remotes)
                .Where(r => string.Equals(r.Normalized, row.RepoKey, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Url));
        }

        return urls.Distinct(StringComparer.Ordinal).ToList();
    }
}

/// <summary>What to show beside a button that queues an action: nothing, on its way, done, or why it failed.</summary>
public sealed record ActionStatus(AgentActionEntity? Row)
{
    public static readonly ActionStatus None = new((AgentActionEntity?)null);

    public bool InFlight => Row is { IsPending: true };

    public bool Failed => Row is { Status: AgentActionStatus.Failed or AgentActionStatus.Expired };

    public string Class => Row?.Status switch
    {
        null => "",
        AgentActionStatus.Succeeded => "ok",
        AgentActionStatus.Failed => "danger",
        AgentActionStatus.Expired => "muted",
        _ => "info",
    };

    /// <summary>The same words the cleanup findings use.</summary>
    public string Text => Row?.Status switch
    {
        null => "",
        AgentActionStatus.Queued => $"queued for {Row.MachineName}",
        AgentActionStatus.Claimed => $"running on {Row.MachineName}",
        AgentActionStatus.Succeeded => $"done on {Row.MachineName}",
        AgentActionStatus.Failed => $"failed on {Row.MachineName}",
        _ => "expired, not run",
    };
}

/// <summary>
/// What a page needs for buttons that queue agent actions outside a cleanup finding (remove, re-clone,
/// clean, push, pull): who is asking, whether a machine can be asked, the recent actions, and queueing.
/// One per circuit; the page reloads it whenever the dashboard changes.
/// </summary>
public sealed class ActionBoard(ActionQueue queue, AuthenticationStateProvider auth)
{
    private IReadOnlyDictionary<(string MachineId, string ActionJson), AgentActionEntity> _shown = new Dictionary<(string, string), AgentActionEntity>();

    public async Task LoadAsync() => _shown = await queue.ShownOnFindingsAsync();

    public ActionAvailability For(MachineView machine, AgentActionKind kind) => queue.Availability(machine, kind);

    /// <summary>The newest queued, running or just-finished run of exactly this action on this machine.</summary>
    public ActionStatus StatusOf(string machineId, AgentAction? action) =>
        action is not null && _shown.TryGetValue((machineId.ToLowerInvariant(), AgentActions.Key(action)), out var row) ? new ActionStatus(row) : ActionStatus.None;

    /// <summary>The newest shown action of a kind for a path on a machine, whatever its other fields: a removal queued with different caveats ticked is still that folder's removal.</summary>
    public ActionStatus StatusOf(string machineId, AgentActionKind kind, string path) =>
        _shown.Values.Where(r => r.Kind == kind && r.RepoPath == path && string.Equals(r.MachineId, machineId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Id).FirstOrDefault() is { } row ? new ActionStatus(row) : ActionStatus.None;

    /// <summary>The newest shown restore of one archive on a machine, wherever it was restored to.</summary>
    public ActionStatus StatusOfRestore(string machineId, string archivePath) =>
        _shown.Values
            .Where(r => r.Kind == AgentActionKind.RestoreArchive && string.Equals(r.MachineId, machineId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Id)
            .FirstOrDefault(r => JsonSerializer.Deserialize<AgentAction>(r.ActionJson, SnapshotSchema.JsonOptions)?.ArchivePath == archivePath) is { } row
            ? new ActionStatus(row)
            : ActionStatus.None;

    public async Task<string> LoginAsync() =>
        (await auth.GetAuthenticationStateAsync()).User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";

    /// <summary>Queues one confirmed action and reloads. Returns why it was refused, or null.</summary>
    public async Task<string?> QueueAsync(string machineId, AgentAction action)
    {
        var outcome = await queue.QueueAsync(machineId, action, await LoginAsync());
        await LoadAsync();
        return outcome.Error;
    }
}
