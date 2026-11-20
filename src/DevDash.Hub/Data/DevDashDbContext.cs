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
    public DbSet<RemovedProjectEntity> RemovedProjects => Set<RemovedProjectEntity>();
    public DbSet<AlertSettingEntity> AlertSettings => Set<AlertSettingEntity>();
    public DbSet<AlertStateEntity> AlertStates => Set<AlertStateEntity>();
    public DbSet<AlertDeliveryEntity> AlertDeliveries => Set<AlertDeliveryEntity>();

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

        modelBuilder.Entity<RemovedProjectEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.MachineId).HasMaxLength(128);
            e.HasIndex(r => new { r.MachineId, r.Path });
            e.HasIndex(r => r.ActionId);
        });

        modelBuilder.Entity<AlertSettingEntity>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(128);
        });

        modelBuilder.Entity<AlertStateEntity>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(64);
            e.Property(s => s.Trigger).HasMaxLength(32);
        });

        modelBuilder.Entity<AlertDeliveryEntity>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Channel).HasMaxLength(32);
            e.Property(d => d.Trigger).HasMaxLength(32);
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

/// <summary>
/// A tombstone: a project the dashboard had an agent move to the trash, and what it takes to clone it
/// back into the same folder. Written when the removal is queued (the snapshot still has the facts
/// then) and confirmed, by <see cref="RemovedAt"/>, when the agent reports that it is done.
/// </summary>
public sealed class RemovedProjectEntity
{
    public int Id { get; set; }
    public required string MachineId { get; set; }
    public string MachineName { get; set; } = "";

    /// <summary>The folder it was in, as the agent reported it. A re-clone goes back here.</summary>
    public required string Path { get; set; }

    /// <summary>The folder's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The repo's key in the dashboard (<c>host/owner/repo</c>), so other machines' remotes for it can be found.</summary>
    public string RepoKey { get; set; } = "";

    /// <summary>Its remotes as JSON: a list of <see cref="RemoteInfo"/>. The only URLs a re-clone may use, besides another machine's remote for the same repo.</summary>
    public string RemotesJson { get; set; } = "[]";
    public string? DefaultBranch { get; set; }

    /// <summary>The branch that was checked out, and the commit it was at.</summary>
    public string? Branch { get; set; }
    public string? HeadSha { get; set; }

    /// <summary>The folder's size when it was removed. Null when it had not been measured.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>The removal action this belongs to.</summary>
    public int ActionId { get; set; }
    public string RemovedBy { get; set; } = "";
    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>When the agent reported the folder moved to the trash. Null while the removal is still on its way.</summary>
    public DateTimeOffset? RemovedAt { get; set; }
}

/// <summary>
/// One alert setting changed on the Alerts page, over the value from configuration. The key is the
/// setting's path under <c>Hub:Alerts</c> (<c>Triggers:AtRisk:Days</c>).
/// </summary>
public sealed class AlertSettingEntity
{
    public required string Key { get; set; }

    /// <summary>The value as configuration would spell it. A secret is stored protected (see <see cref="IsSecret"/>).</summary>
    public string Value { get; set; } = "";

    /// <summary>A token, topic or webhook address: encrypted with the hub's data-protection key, and never shown again.</summary>
    public bool IsSecret { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
}

/// <summary>
/// An alert that is open: its condition is true now. The row is what makes an alert go out once. It is
/// deleted when the condition clears, so the same condition coming back alerts again.
/// </summary>
public sealed class AlertStateEntity
{
    /// <summary>The trigger and a hash of what the alert is about.</summary>
    public required string Key { get; set; }
    public string Trigger { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>The hub page about it, relative to the hub.</summary>
    public string Path { get; set; } = "";

    /// <summary>Something that happened (a failed action), kept for a while so it is not sent twice.</summary>
    public bool IsEvent { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>When it last reached at least one channel. Null while it is still waiting: quiet hours, no channel, or every channel failed.</summary>
    public DateTimeOffset? LastNotifiedAt { get; set; }

    /// <summary>Failed attempts since it last got through, and when the last one was: the next waits longer each time.</summary>
    public int Attempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
}

/// <summary>One attempt to send one alert to one channel, for the delivery log on the Alerts page.</summary>
public sealed class AlertDeliveryEntity
{
    public int Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Channel { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Ok { get; set; }

    /// <summary>What went wrong, with anything secret taken out. Null when it went through.</summary>
    public string? Error { get; set; }

    /// <summary>Sent with a "Send test" button.</summary>
    public bool IsTest { get; set; }
}
