using Microsoft.EntityFrameworkCore;

namespace DevDash.Hub.Data;

public enum T3StartStatus
{
    /// <summary>The StartT3Thread action is queued or with the agent.</summary>
    Queued,

    /// <summary>The agent reported the thread started.</summary>
    Started,

    /// <summary>Refused, failed, expired, or no result came back.</summary>
    Failed,
}

/// <summary>
/// A T3 Code thread the dashboard asked an agent to start: from a repository's "Ask Claude in T3", a "Fix in
/// T3" button, or a GitHub issue. Written when the action is queued and settled when its result arrives,
/// with the thread's id and link if T3 Code said them. The alerts about threads DevDash started read this.
/// </summary>
public sealed class T3ThreadEntity
{
    public int Id { get; set; }

    /// <summary>The StartT3Thread action. One row per action.</summary>
    public int ActionId { get; set; }
    public required string MachineId { get; set; }
    public string MachineName { get; set; } = "";

    /// <summary>The repo's key in the dashboard (<c>github.com/owner/repo</c>, or a local key).</summary>
    public string RepoKey { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Branch { get; set; } = "";
    public string WorktreeBase { get; set; } = "";

    /// <summary>Where it was started from: <c>repo</c>, <c>issue</c>, <c>ci</c>, <c>deps</c>, <c>toolchain</c>.</summary>
    public string Origin { get; set; } = "repo";

    /// <summary>For <c>issue</c>: the issue number.</summary>
    public int? IssueNumber { get; set; }

    public string? ThreadId { get; set; }
    public string? Link { get; set; }
    public T3StartStatus Status { get; set; }

    /// <summary>Why it failed, truncated.</summary>
    public string? Error { get; set; }

    /// <summary>The browser login that confirmed it, or <c>auto</c> for a label-triggered start.</summary>
    public string QueuedBy { get; set; } = "";
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    internal static void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<T3ThreadEntity>(e =>
    {
        e.HasKey(t => t.Id);
        e.Property(t => t.MachineId).HasMaxLength(128);
        e.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        e.HasIndex(t => t.ActionId);
        e.HasIndex(t => t.ThreadId);
    });
}

public enum IssueThreadStatus
{
    /// <summary>Should start (label rules met) but no machine can take it yet.</summary>
    Pending,

    /// <summary>Has the label, but a rule says a person must start it: who added the label, or who wrote the issue.</summary>
    NeedsManualStart,
    Queued,
    Started,
    Failed,
}

/// <summary>
/// A GitHub issue's link to a T3 thread: started by hand from the Issues page, or by the label trigger; and
/// for the label trigger, the decisions that did not start one (pending, or needs a manual start, with why).
/// A row with an <see cref="ActionId"/> is a thread start, and no second one is ever started for the same
/// issue automatically; only "Start again" on the page does that.
/// </summary>
public sealed class IssueThreadEntity
{
    public int Id { get; set; }

    /// <summary>Lower-cased <c>github.com/owner/repo</c>.</summary>
    public required string Repo { get; set; }
    public int Number { get; set; }
    public string? MachineId { get; set; }
    public int? ActionId { get; set; }
    public string? ThreadId { get; set; }
    public string? Link { get; set; }
    public IssueThreadStatus Status { get; set; }

    /// <summary>Why it is pending, needs a manual start, or failed.</summary>
    public string? Reason { get; set; }

    /// <summary><c>auto</c> or <c>manual</c>.</summary>
    public string StartedBy { get; set; } = "manual";

    /// <summary>The login that started it, or what added the label for an automatic start.</summary>
    public string By { get; set; } = "";

    /// <summary>What the optional write-back to GitHub did: "commented", "label swapped", or why it could not.</summary>
    public string? WriteBack { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public const string Auto = "auto";
    public const string Manual = "manual";

    internal static void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<IssueThreadEntity>(e =>
    {
        e.HasKey(t => t.Id);
        e.Property(t => t.Repo).HasMaxLength(256);
        e.Property(t => t.Status).HasConversion<string>().HasMaxLength(24);
        e.Property(t => t.StartedBy).HasMaxLength(16);
        e.HasIndex(t => new { t.Repo, t.Number });
        e.HasIndex(t => t.ActionId);
    });
}
