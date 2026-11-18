using System.Collections.Concurrent;
using System.Text.Json;
using DevDash.Contracts;
using DevDash.Hub.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Services;

public sealed record MachineView(MachineSnapshot Snapshot, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt)
{
    public MachineInfo Machine => Snapshot.Machine;
    public string MachineId => Snapshot.Machine.MachineId;
}

/// <summary>The latest snapshot per machine, kept in memory and persisted to SQLite.</summary>
public sealed class SnapshotStore(
    IDbContextFactory<DevDashDbContext> dbFactory,
    DashboardNotifier notifier,
    IOptionsMonitor<HubOptions> options,
    TimeProvider time)
{
    private readonly ConcurrentDictionary<string, MachineView> _machines = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MachineView> Machines =>
        _machines.Values.OrderBy(m => m.Machine.Hostname, StringComparer.OrdinalIgnoreCase).ToList();

    public MachineView? Get(string machineId) => _machines.GetValueOrDefault(machineId);

    public bool IsOnline(MachineView machine) =>
        time.GetUtcNow() - machine.LastSeenAt <= TimeSpan.FromMinutes(options.CurrentValue.OnlineWindowMinutes);

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var entity in await db.Machines.AsNoTracking().ToListAsync(ct))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<MachineSnapshot>(entity.SnapshotJson, SnapshotSchema.JsonOptions);
                if (snapshot is not null)
                {
                    _machines[entity.MachineId] = new MachineView(snapshot, entity.FirstSeenAt, entity.LastSeenAt);
                }
            }
            catch (JsonException)
            {
                // A snapshot written by an incompatible version; the next push replaces it.
            }
        }
    }

    public async Task<MachineView> IngestAsync(MachineSnapshot snapshot, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var id = snapshot.Machine.MachineId;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Machines.FirstOrDefaultAsync(m => m.MachineId == id, ct);
        if (entity is null)
        {
            entity = new MachineEntity { MachineId = id, FirstSeenAt = now };
            db.Machines.Add(entity);
        }

        entity.Hostname = snapshot.Machine.Hostname;
        entity.Os = snapshot.Machine.Os;
        entity.AgentVersion = snapshot.Machine.AgentVersion;
        entity.LastSeenAt = now;
        entity.LastScanDurationMs = snapshot.ScanDurationMs;
        entity.EntryCount = snapshot.Entries.Count;
        entity.SnapshotJson = JsonSerializer.Serialize(snapshot, SnapshotSchema.JsonOptions);
        await db.SaveChangesAsync(ct);

        var view = new MachineView(snapshot, entity.FirstSeenAt, now);
        _machines[id] = view;
        notifier.Notify(DashboardChange.Snapshot);
        return view;
    }

    public async Task ForgetAsync(string machineId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Machines.Where(m => m.MachineId == machineId).ExecuteDeleteAsync(ct);
        _machines.TryRemove(machineId, out _);
        notifier.Notify(DashboardChange.Machines);
    }
}
