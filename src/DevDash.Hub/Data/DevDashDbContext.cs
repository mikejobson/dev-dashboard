using DevDash.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DevDash.Hub.Data;

public sealed class DevDashDbContext(DbContextOptions<DevDashDbContext> options) : DbContext(options)
{
    public DbSet<MachineEntity> Machines => Set<MachineEntity>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();
    public DbSet<FindingStateEntity> FindingStates => Set<FindingStateEntity>();
    public DbSet<CacheEntity> Cache => Set<CacheEntity>();
    public DbSet<AgentActionEntity> AgentActions => Set<AgentActionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MachineEntity>(e =>
        {
            e.HasKey(m => m.MachineId);
            e.Property(m => m.MachineId).HasMaxLength(128);
        });

        modelBuilder.Entity<ApiKeyEntity>(e =>
        {
            e.HasKey(k => k.Id);
            e.HasIndex(k => k.KeyHash).IsUnique();
            e.Property(k => k.KeyHash).HasMaxLength(64);
        });

        modelBuilder.Entity<FindingStateEntity>(e =>
        {
            e.HasKey(f => f.Fingerprint);
            e.Property(f => f.Fingerprint).HasMaxLength(64);
            e.Property(f => f.Status).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<CacheEntity>(e =>
        {
            e.HasKey(c => c.Key);
            e.Property(c => c.Key).HasMaxLength(64);
        });

        modelBuilder.Entity<AgentActionEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.MachineId).HasMaxLength(128);
            e.Property(a => a.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(a => new { a.MachineId, a.Status });
        });
    }
}

/// <summary>A machine and its latest snapshot (stored as JSON; only the newest is kept).</summary>
public sealed class MachineEntity
{
    public required string MachineId { get; set; }
    public string Hostname { get; set; } = "";
    public string Os { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public long LastScanDurationMs { get; set; }
    public int EntryCount { get; set; }
    public string SnapshotJson { get; set; } = "";
}

/// <summary>An agent API key. Only the SHA-256 hash is stored.</summary>
public sealed class ApiKeyEntity
{
    public int Id { get; set; }
    public required string KeyHash { get; set; }

    /// <summary>First characters of the key, to tell keys apart in the UI.</summary>
    public string Prefix { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>The machine this key is scoped to. Null until first use, when it binds to the sender.</summary>
    public string? MachineId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public enum FindingStatus
{
    Snoozed,
    Dismissed,
}

/// <summary>A snooze or dismiss decision, keyed by finding fingerprint.</summary>
public sealed class FindingStateEntity
{
    public required string Fingerprint { get; set; }
    public FindingStatus Status { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
    public string RuleId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Small JSON blobs the hub wants to survive a restart (e.g. the last GitHub poll).</summary>
public sealed class CacheEntity
{
    public required string Key { get; set; }
    public string Json { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum AgentActionStatus
{
    /// <summary>Waiting for the machine's agent to pick it up.</summary>
    Queued,

    /// <summary>Handed to the agent. It is never handed out a second time.</summary>
    Claimed,
    Succeeded,

    /// <summary>The agent refused it, git failed, or the agent never reported back.</summary>
    Failed,

    /// <summary>The agent did not pick it up in time, so it will never run.</summary>
    Expired,
}

/// <summary>
/// One action queued for one machine's agent. The row is also the audit trail: who queued what,
/// when each step happened, and what git printed.
/// </summary>
public sealed class AgentActionEntity
{
    /// <summary>Also the order the actions were queued in.</summary>
    public int Id { get; set; }
    public required string MachineId { get; set; }
    public string MachineName { get; set; } = "";
    public AgentActionKind Kind { get; set; }

    /// <summary>The typed action exactly as it is sent to the agent.</summary>
    public string ActionJson { get; set; } = "";

    /// <summary>The equivalent command the user confirmed.</summary>
    public string CommandLine { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public string? FindingFingerprint { get; set; }
    public string? FindingTitle { get; set; }

    /// <summary>Login of the browser user who confirmed it.</summary>
    public string QueuedBy { get; set; } = "";
    public AgentActionStatus Status { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ExitCode { get; set; }

    /// <summary>Git's output or the reason it did not run, truncated.</summary>
    public string? Output { get; set; }

    public bool IsPending => Status is AgentActionStatus.Queued or AgentActionStatus.Claimed;
}
