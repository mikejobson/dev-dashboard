using Microsoft.EntityFrameworkCore;

namespace DevDash.Hub.History;

/// <summary><c>Hub:History</c>: the per-day rollups behind the History page and "since yesterday" (§22).</summary>
public sealed class HistoryOptions
{
    /// <summary>Record a rollup on every snapshot. Off: nothing is written, and the pages say there is no history.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Days of rollups to keep. Older rows are deleted once a day.</summary>
    public int RetentionDays { get; set; } = 180;
}

/// <summary>
/// One machine on one day: the totals of its latest snapshot that day, and the day's highest for the
/// counts where a peak matters. Days are the hub's local calendar days.
/// </summary>
public sealed class MachineDayEntity
{
    public required string MachineId { get; set; }
    public DateOnly Day { get; set; }
    public string Hostname { get; set; } = "";

    /// <summary>Working trees that are not linked worktrees.</summary>
    public int RepoCount { get; set; }

    /// <summary>Working trees with uncommitted changes.</summary>
    public int DirtyCount { get; set; }

    /// <summary>Commits on no remote, across every repository.</summary>
    public int UnpushedCommits { get; set; }
    public int MaxUnpushedCommits { get; set; }
    public int AtRiskHigh { get; set; }
    public int AtRiskMedium { get; set; }
    public int AtRiskLow { get; set; }

    /// <summary>The most working trees at risk (any level) seen in one snapshot that day.</summary>
    public int MaxAtRisk { get; set; }

    /// <summary>Disk used by the working trees (measured ones only).</summary>
    public long RepoBytes { get; set; }

    /// <summary>The part of <see cref="RepoBytes"/> that is build output.</summary>
    public long BuildBytes { get; set; }

    /// <summary>What removing every working tree that is safe to remove would free.</summary>
    public long SafeToRemoveBytes { get; set; }

    /// <summary>Snapshots folded into this row.</summary>
    public int Samples { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public int AtRisk => AtRiskHigh + AtRiskMedium + AtRiskLow;

    internal static void Configure(ModelBuilder model) => model.Entity<MachineDayEntity>(e =>
    {
        e.ToTable("HistoryMachineDays");
        e.HasKey(d => new { d.MachineId, d.Day });
        e.Property(d => d.MachineId).HasMaxLength(128);
        e.HasIndex(d => d.Day);
        e.Ignore(d => d.AtRisk);
    });
}

/// <summary>One working tree on one machine on one day: its latest state that day, plus the day's worst.</summary>
public sealed class CloneDayEntity
{
    public required string MachineId { get; set; }
    public required string Path { get; set; }
    public DateOnly Day { get; set; }

    /// <summary>The repo's key in the dashboard (<c>host/owner/repo</c>), as the repo page uses it.</summary>
    public string RepoKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Branch { get; set; }
    public bool IsLinkedWorktree { get; set; }
    public long? SizeBytes { get; set; }
    public long? BuildBytes { get; set; }
    public int Dirty { get; set; }
    public int Unpushed { get; set; }
    public int MaxUnpushed { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }

    /// <summary><see cref="Core.Safety.RiskLevel"/> as a number: 0 none, 3 commits on no remote.</summary>
    public int RiskLevel { get; set; }
    public int MaxRiskLevel { get; set; }

    /// <summary><see cref="Core.Safety.SafetyVerdict"/>, by name.</summary>
    public string Verdict { get; set; } = "";

    /// <summary>Branches with commits on no remote, one per line, at most <see cref="HistoryRollup.MaxBranchNames"/>.</summary>
    public string UnpushedBranches { get; set; } = "";

    internal static void Configure(ModelBuilder model) => model.Entity<CloneDayEntity>(e =>
    {
        e.ToTable("HistoryCloneDays");
        e.HasKey(d => new { d.MachineId, d.Path, d.Day });
        e.Property(d => d.MachineId).HasMaxLength(128);
        e.Property(d => d.Verdict).HasMaxLength(16);
        e.HasIndex(d => d.Day);
        e.HasIndex(d => d.RepoKey);
    });
}

/// <summary>
/// A working tree that is at risk now, and since when the hub has seen it so without a break: the
/// "risk since" that makes "unpushed for 7 days" survive a hub restart. Deleted when the clone stops
/// being at risk or disappears, so the next time is a new episode.
/// </summary>
public sealed class RiskEpisodeEntity
{
    public required string MachineId { get; set; }
    public required string Path { get; set; }

    /// <summary>At risk at any level since.</summary>
    public DateTimeOffset Since { get; set; }

    /// <summary>At <see cref="Core.Safety.RiskLevel.Medium"/> or worse since. Null while it is less.</summary>
    public DateTimeOffset? MediumSince { get; set; }

    /// <summary>With commits on no remote since. Null while it has none.</summary>
    public DateTimeOffset? HighSince { get; set; }

    /// <summary>The level in the latest snapshot.</summary>
    public int Level { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Since when it has been at <paramref name="level"/> or worse. Null when it is not now.</summary>
    public DateTimeOffset? SinceFor(Core.Safety.RiskLevel level) => level switch
    {
        Core.Safety.RiskLevel.High => HighSince,
        Core.Safety.RiskLevel.Medium => MediumSince,
        _ => Since,
    };

    internal static void Configure(ModelBuilder model) => model.Entity<RiskEpisodeEntity>(e =>
    {
        e.ToTable("RiskEpisodes");
        e.HasKey(r => new { r.MachineId, r.Path });
        e.Property(r => r.MachineId).HasMaxLength(128);
    });
}
