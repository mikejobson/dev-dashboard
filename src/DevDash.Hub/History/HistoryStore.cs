using DevDash.Contracts;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.History;

/// <summary>"What changed since yesterday": the changes, and which day each machine is compared with.</summary>
public sealed record SinceYesterdayResult(IReadOnlyList<HistoryChange> Changes, IReadOnlyDictionary<string, DateOnly> Baselines)
{
    public static SinceYesterdayResult None { get; } = new([], new Dictionary<string, DateOnly>());

    /// <summary>False until some machine has a day of history before today.</summary>
    public bool HasBaseline => Baselines.Count > 0;
}

/// <summary>One working tree's size over a window, for "top growing".</summary>
public sealed record CloneGrowth(string MachineId, string Hostname, string Path, string RepoKey, string Name, long From, long To, IReadOnlyList<(DateOnly Day, double Value)> Points)
{
    public long Growth => To - From;
}

/// <summary>
/// Writes the per-day rollups on every snapshot and reads them back for the History page, the repo
/// page's sparklines, "since yesterday", the at-risk alert's durations and the MCP <c>history</c> tool.
/// Only compact numbers per machine and per working tree per day are kept, never whole snapshots (§22).
/// </summary>
public sealed class HistoryStore(
    IDbContextFactory<DevDashDbContext> dbFactory,
    IOptionsMonitor<HubOptions> options,
    TimeProvider time,
    ILogger<HistoryStore> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateOnly _prunedOn;
    private (DateOnly Today, IReadOnlyList<CloneFacts> Rows, IReadOnlyDictionary<string, DateOnly> Days)? _baseline;

    public HistoryOptions Options => options.CurrentValue.History;

    public int RetentionDays => Math.Clamp(Options.RetentionDays, 2, 3650);

    public DateOnly Today => HistoryRollup.DayOf(time.GetUtcNow(), time.LocalTimeZone);

    /// <summary>
    /// Folds one snapshot into today's rows and moves each working tree's risk episode on. Never throws
    /// for a database problem: the history is a nicety, and must not cost the agent its push.
    /// </summary>
    public async Task RecordAsync(MachineSnapshot snapshot, DateTimeOffset now, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var day = HistoryRollup.DayOf(now, time.LocalTimeZone);
            var id = snapshot.Machine.MachineId;
            var facts = HistoryRollup.Clones(snapshot, now);
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            // The risk episodes are kept even with the history switched off: the at-risk alert reads them.
            var episodes = await db.RiskEpisodes.Where(e => e.MachineId == id).ToDictionaryAsync(e => e.Path, StringComparer.Ordinal, ct);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var clone in facts)
            {
                seen.Add(clone.Path);
                var existing = episodes.GetValueOrDefault(clone.Path);
                var next = HistoryRollup.Advance(existing, id, clone.Path, clone.IsAtRisk, clone.Level, now);
                if (next is null && existing is not null)
                {
                    db.RiskEpisodes.Remove(existing);
                }
                else if (next is not null && existing is null)
                {
                    db.RiskEpisodes.Add(next);
                }
            }

            db.RiskEpisodes.RemoveRange(episodes.Values.Where(e => !seen.Contains(e.Path)));

            if (Options.Enabled)
            {
                var machine = await db.HistoryMachineDays.FirstOrDefaultAsync(d => d.MachineId == id && d.Day == day, ct);
                if (machine is null)
                {
                    machine = new MachineDayEntity { MachineId = id, Day = day };
                    db.HistoryMachineDays.Add(machine);
                }

                HistoryRollup.Apply(machine, snapshot.Machine.Hostname, HistoryRollup.Totals(facts), now);

                var rows = await db.HistoryCloneDays.Where(d => d.MachineId == id && d.Day == day).ToDictionaryAsync(d => d.Path, StringComparer.Ordinal, ct);
                foreach (var clone in facts)
                {
                    var isNew = !rows.TryGetValue(clone.Path, out var row);
                    if (row is null)
                    {
                        row = new CloneDayEntity { MachineId = id, Path = clone.Path, Day = day };
                        db.HistoryCloneDays.Add(row);
                        rows[clone.Path] = row;
                    }

                    HistoryRollup.Apply(row, clone, isNew);
                }
            }

            await db.SaveChangesAsync(ct);

            if (_prunedOn != day)
            {
                await PruneAsync(db, day, ct);
                _prunedOn = day;
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning(ex, "Could not record history for {Machine}", snapshot.Machine.MachineId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes the rows older than the retention. Returns how many went.</summary>
    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await PruneAsync(db, Today, ct);
    }

    private async Task<int> PruneAsync(DevDashDbContext db, DateOnly today, CancellationToken ct)
    {
        var cutoff = today.AddDays(-(RetentionDays - 1));
        var deleted = await db.HistoryCloneDays.Where(d => d.Day < cutoff).ExecuteDeleteAsync(ct)
            + await db.HistoryMachineDays.Where(d => d.Day < cutoff).ExecuteDeleteAsync(ct);
        if (deleted > 0)
        {
            logger.LogInformation("History: deleted {Rows} rows from before {Cutoff}", deleted, cutoff);
            _baseline = null;
        }

        return deleted;
    }

    /// <summary>A machine the dashboard forgets takes its history and risk episodes with it.</summary>
    public async Task ForgetAsync(string machineId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.HistoryCloneDays.Where(d => d.MachineId == machineId).ExecuteDeleteAsync(ct);
        await db.HistoryMachineDays.Where(d => d.MachineId == machineId).ExecuteDeleteAsync(ct);
        await db.RiskEpisodes.Where(e => e.MachineId == machineId).ExecuteDeleteAsync(ct);
        _baseline = null;
    }

    /// <summary>Every open risk episode, by machine and path.</summary>
    public async Task<IReadOnlyDictionary<(string MachineId, string Path), RiskEpisodeEntity>> RiskEpisodesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.RiskEpisodes.AsNoTracking().ToListAsync(ct)).ToDictionary(e => (e.MachineId, e.Path));
    }

    /// <summary>Each machine's working trees now against its last day of history before today.</summary>
    public async Task<SinceYesterdayResult> SinceYesterdayAsync(IEnumerable<MachineView> machines, CancellationToken ct = default)
    {
        if (!Options.Enabled)
        {
            return SinceYesterdayResult.None;
        }

        var now = time.GetUtcNow();
        var (rows, days) = await BaselineAsync(Today, ct);
        var current = machines.SelectMany(m => HistoryRollup.Clones(m.Snapshot, now)).ToList();
        var present = current.Select(c => c.MachineId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compared = days.Keys.Where(present.Contains).ToHashSet(StringComparer.Ordinal);
        return new SinceYesterdayResult(
            SinceYesterday.Compare(rows, current, compared),
            days.Where(d => compared.Contains(d.Key)).ToDictionary(d => d.Key, d => d.Value));
    }

    /// <summary>For each machine, the rows of its newest day before <paramref name="today"/>. Cached for the day.</summary>
    private async Task<(IReadOnlyList<CloneFacts> Rows, IReadOnlyDictionary<string, DateOnly> Days)> BaselineAsync(DateOnly today, CancellationToken ct)
    {
        if (_baseline is { } cached && cached.Today == today)
        {
            return (cached.Rows, cached.Days);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var from = today.AddDays(-RetentionDays);
        var machineDays = await db.HistoryMachineDays.AsNoTracking().Where(d => d.Day < today && d.Day >= from).ToListAsync(ct);
        var latest = machineDays.GroupBy(d => d.MachineId, StringComparer.Ordinal).Select(g => g.MaxBy(d => d.Day)!).ToList();

        var rows = new List<CloneFacts>();
        foreach (var day in latest)
        {
            var clones = await db.HistoryCloneDays.AsNoTracking().Where(d => d.MachineId == day.MachineId && d.Day == day.Day).ToListAsync(ct);
            rows.AddRange(clones.Select(c => HistoryRollup.FromRow(c, day.Hostname)));
        }

        var days = latest.ToDictionary(d => d.MachineId, d => d.Day, StringComparer.Ordinal);
        _baseline = (today, rows, days);
        return (rows, days);
    }

    /// <summary>Every machine's rows for the last <paramref name="days"/> days (today included), oldest first.</summary>
    public async Task<IReadOnlyList<MachineDayEntity>> MachineDaysAsync(int days, CancellationToken ct = default)
    {
        var from = Today.AddDays(-(Math.Max(1, days) - 1));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.HistoryMachineDays.AsNoTracking().Where(d => d.Day >= from).ToListAsync(ct))
            .OrderBy(d => d.Day).ThenBy(d => d.Hostname, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The rows of every working tree of one repo for the last <paramref name="days"/> days, oldest first.</summary>
    public async Task<IReadOnlyList<CloneDayEntity>> RepoDaysAsync(string repoKey, int days, CancellationToken ct = default)
    {
        var from = Today.AddDays(-(Math.Max(1, days) - 1));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.HistoryCloneDays.AsNoTracking().Where(d => d.RepoKey == repoKey && d.Day >= from).ToListAsync(ct))
            .OrderBy(d => d.Day).ThenBy(d => d.MachineId, StringComparer.Ordinal).ThenBy(d => d.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>The working trees that grew most over the last <paramref name="days"/> days: last measured size against the first.</summary>
    public async Task<IReadOnlyList<CloneGrowth>> TopGrowingAsync(int days = 30, int take = 10, CancellationToken ct = default)
    {
        var from = Today.AddDays(-(Math.Max(2, days) - 1));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.HistoryCloneDays.AsNoTracking().Where(d => d.Day >= from && d.SizeBytes != null).ToListAsync(ct);
        var hosts = (await db.HistoryMachineDays.AsNoTracking().Where(d => d.Day >= from).ToListAsync(ct))
            .GroupBy(d => d.MachineId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.MaxBy(d => d.Day)!.Hostname, StringComparer.Ordinal);
        return TopGrowing(rows, hosts, take);
    }

    /// <summary>Pure: the growth of each working tree across the rows given, the largest first, growing ones only.</summary>
    public static IReadOnlyList<CloneGrowth> TopGrowing(IEnumerable<CloneDayEntity> rows, IReadOnlyDictionary<string, string> hosts, int take) => rows
        .Where(r => r.SizeBytes is not null)
        .GroupBy(r => (r.MachineId, r.Path))
        .Select(g =>
        {
            var ordered = g.OrderBy(r => r.Day).ToList();
            var last = ordered[^1];
            return new CloneGrowth(last.MachineId, hosts.GetValueOrDefault(last.MachineId, last.MachineId), last.Path, last.RepoKey, last.Name,
                ordered[0].SizeBytes!.Value, last.SizeBytes!.Value, ordered.Select(r => (r.Day, (double)r.SizeBytes!.Value)).ToList());
        })
        .Where(g => g.Growth > 0 && g.Points.Count > 1)
        .OrderByDescending(g => g.Growth)
        .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
        .Take(take)
        .ToList();
}
