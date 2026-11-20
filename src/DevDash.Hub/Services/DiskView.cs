using DevDash.Contracts;
using DevDash.Core;
using DevDash.Core.Toolchains;

namespace DevDash.Hub.Services;

/// <summary>One cache on the Disk page.</summary>
/// <param name="Info">What the id means; null for an id from a newer agent.</param>
/// <param name="Reclaimable">What cleaning would free, as well as it is known: the tool's figure, the whole cache for one that is cleared, null for one that is pruned.</param>
public sealed record DiskRow(DiskItem Item, DiskCaches.Info? Info, long? Reclaimable)
{
    public string Title => Info?.Title ?? Item.Id;

    /// <summary>Whether a Clean button belongs here: this build knows a recipe, and the agent said it can run it.</summary>
    public bool CanClean => Info?.Recipe is not null && Item.Cleanable;

    public AgentAction? Action => CanClean ? AgentAction.CleanCache(Info!.Id) : null;
}

/// <summary>A repository's build output, for context beside the caches.</summary>
public sealed record BuildOutputRow(DevEntry Entry, string RepoKey, long Bytes);

/// <summary>An installed .NET SDK, and whether anything on the machine would use it.</summary>
/// <param name="UsedBy">The working trees whose <c>global.json</c> resolves to it, or "unpinned" for the newest.</param>
public sealed record SdkRow(string Version, long Bytes, IReadOnlyList<string> UsedBy)
{
    public bool Unused => UsedBy.Count == 0;
}

/// <summary>What the Disk page shows for one machine.</summary>
/// <param name="Reported">False when the agent sent no disk report: too old, or switched off.</param>
public sealed record MachineDisk(
    MachineView Machine,
    bool Reported,
    IReadOnlyList<DiskRow> Rows,
    IReadOnlyList<SdkRow> Sdks,
    IReadOnlyList<BuildOutputRow> BuildOutput,
    long BuildOutputTotal,
    int BuildOutputRepos)
{
    /// <summary>What the Clean buttons together would free, as far as it is known.</summary>
    public long Reclaimable => Rows.Where(r => r.CanClean).Sum(r => r.Reclaimable ?? 0);

    public long Total => Rows.Sum(r => r.Item.Bytes ?? 0);
}

/// <summary>Turns each machine's disk report into the Disk page. Pure over the snapshots.</summary>
public static class DiskView
{
    public const int BuildOutputShown = 10;

    /// <summary>The caches that a clean empties (so all of it is freed). The rest are pruned, and what that frees is unknown unless the tool says.</summary>
    private static readonly HashSet<DiskCacheId> Cleared =
    [
        DiskCacheId.NuGetGlobalPackages, DiskCacheId.NuGetHttpCache, DiskCacheId.NpmCache, DiskCacheId.XcodeDerivedData, DiskCacheId.HomebrewCache,
    ];

    public static IReadOnlyList<MachineDisk> For(IEnumerable<MachineView> machines) => machines.Select(For).ToList();

    public static MachineDisk For(MachineView machine)
    {
        var report = machine.Machine.Disk;
        var rows = (report?.Items ?? [])
            .Select(item =>
            {
                var info = DiskCaches.Find(item.Id);
                var reclaimable = item.ReclaimableBytes ?? (info is not null && Cleared.Contains(info.Id) ? item.Bytes : null);
                return new DiskRow(item, info, reclaimable);
            })
            .OrderByDescending(r => r.CanClean)
            .ThenByDescending(r => r.Item.Bytes ?? 0)
            .ToList();

        var entries = RepoIdentity.Flatten(machine.Snapshot.Entries).ToList();
        var build = entries
            .Where(e => e.Size is { ReclaimableBytes: > 0 })
            .Select(e => new BuildOutputRow(e, RepoKey(machine, e), e.Size!.ReclaimableBytes))
            .OrderByDescending(r => r.Bytes)
            .ToList();

        return new MachineDisk(machine, report is not null, rows, Sdks(machine, report, entries), build.Take(BuildOutputShown).ToList(), build.Sum(r => r.Bytes), build.Count);
    }

    /// <summary>
    /// Each installed SDK, with the working trees that would use it: the one each <c>global.json</c> resolves to by the
    /// .NET host's own rules (§17), and the newest for everything unpinned. An SDK nothing resolves to is "unused".
    /// </summary>
    public static IReadOnlyList<SdkRow> Sdks(MachineView machine, DiskReport? report, IReadOnlyList<DevEntry> entries)
    {
        var item = report?.Items.FirstOrDefault(i => i.Id == nameof(DiskCacheId.DotnetSdks));
        if (item is null || item.Parts.Count == 0)
        {
            return [];
        }

        var installed = item.Parts.Select(p => p.Name).ToList();
        var used = installed.ToDictionary(v => v, _ => new List<string>(), StringComparer.Ordinal);
        if (installed.Select(ToolVersion.Parse).Where(v => v is not null).Max() is { } newest && used.TryGetValue(newest.ToString(), out var unpinned))
        {
            unpinned.Add("anything unpinned");
        }

        foreach (var entry in entries.Where(e => e.Kind == EntryKind.GitRepo))
        {
            foreach (var pin in entry.Pins ?? [])
            {
                if (pin.Tool != ToolNames.Dotnet)
                {
                    continue;
                }

                var resolved = DotnetSdkResolver.Resolve(pin.Version, pin.RollForward, pin.AllowPrerelease, installed);
                if (resolved.Sdk?.ToString() is { } version && used.TryGetValue(version, out var list) && !list.Contains(entry.Name))
                {
                    list.Add(entry.Name);
                }
            }
        }

        return item.Parts.Select(p => new SdkRow(p.Name, p.Bytes, used.GetValueOrDefault(p.Name) ?? [])).ToList();
    }

    /// <summary>Why a machine has no disk report, for the page.</summary>
    public static string WhyNotReported(MachineInfo machine)
    {
        var minimum = SemVer.ParseOrNull(AgentActions.DiskMinAgentVersion)!;
        return SemVer.ParseOrNull(machine.AgentVersion) is { } version && version.CompareTo(minimum) >= 0
            ? $"The disk report is switched off on {machine.Hostname} (Agent:Disk:Enabled), or its first measurement has not finished yet."
            : $"Update the agent on {machine.Hostname} to {AgentActions.DiskMinAgentVersion} or newer to see its caches.";
    }

    /// <summary>"3 days", "5 months": how long since the cache was last written to.</summary>
    public static string Age(DateTimeOffset? lastModified, DateTimeOffset now) => lastModified is null ? "" : Fmt.Ago(lastModified, now);

    private static string RepoKey(MachineView machine, DevEntry entry)
    {
        var mains = RepoIdentity.MainWorkingTrees(machine.Snapshot.Entries);
        return RepoIdentity.KeyFor(machine.MachineId, RepoIdentity.MainOf(entry, mains) ?? entry);
    }
}
