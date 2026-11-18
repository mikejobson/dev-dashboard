using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevDash.Contracts;

/// <summary>Version of the snapshot wire format. Bump on breaking changes.</summary>
public static class SnapshotSchema
{
    public const int Version = 1;

    /// <summary>Serializer options shared by agent and hub.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = Create(indented: false);

    public static JsonSerializerOptions IndentedJsonOptions { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>Everything one agent knows about one machine at one point in time.</summary>
public sealed record MachineSnapshot
{
    public int SchemaVersion { get; init; } = SnapshotSchema.Version;
    public required MachineInfo Machine { get; init; }
    public DateTimeOffset ScannedAt { get; init; }
    public long ScanDurationMs { get; init; }
    public IReadOnlyList<string> Roots { get; init; } = [];
    public IReadOnlyList<DevEntry> Entries { get; init; } = [];
}

public sealed record MachineInfo
{
    /// <summary>Stable id for the machine (defaults to the lower-cased host name).</summary>
    public required string MachineId { get; init; }
    public required string Hostname { get; init; }
    public string Os { get; init; } = "";
    public string? OsDescription { get; init; }
    public string? TailscaleName { get; init; }
    public string? TailscaleIp { get; init; }
    public string AgentVersion { get; init; } = "";

    /// <summary>
    /// True when this agent is configured to run allowlisted actions queued from the dashboard
    /// (<c>Agent:Actions:Enabled</c>). Off unless the machine's owner turned it on.
    /// </summary>
    public bool ActionsEnabled { get; init; }
}

public enum EntryKind
{
    GitRepo,
    WorktreeContainer,
    NonGitFolder,
    LooseFile,
}

/// <summary>One top-level entry under a scan root (or a child of a worktree container).</summary>
public sealed record DevEntry
{
    public required string Root { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public EntryKind Kind { get; init; }
    public GitState? Git { get; init; }
    public DescriptionInputs? Description { get; init; }
    public SizeInfo? Size { get; init; }
    public FolderSummary? Folder { get; init; }

    /// <summary>Newest file mtime, ignoring .git and build directories.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>Entries inside a worktree container.</summary>
    public IReadOnlyList<DevEntry> Children { get; init; } = [];

    /// <summary>Set when the entry could not be scanned completely.</summary>
    public string? Error { get; init; }
}

public sealed record GitState
{
    public string? Branch { get; init; }
    public bool Detached { get; init; }
    public string? HeadSha { get; init; }
    public string? Upstream { get; init; }
    public bool UpstreamGone { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public int Staged { get; init; }
    public int Unstaged { get; init; }
    public int Untracked { get; init; }
    public int Conflicted { get; init; }

    /// <summary>Newest mtime among tracked files with uncommitted changes.</summary>
    public DateTimeOffset? NewestTrackedChangeAt { get; init; }

    /// <summary>Newest mtime among untracked files and folders.</summary>
    public DateTimeOffset? NewestUntrackedAt { get; init; }

    public CommitInfo? LastCommit { get; init; }
    public string? DefaultBranch { get; init; }

    /// <summary>True when this working tree is a linked worktree of another repository.</summary>
    public bool IsLinkedWorktree { get; init; }

    /// <summary>Absolute path of the shared .git directory. Linked worktrees share it with their main repo.</summary>
    public string? CommonDir { get; init; }

    /// <summary>When remote-tracking refs were last updated (mtime of FETCH_HEAD).</summary>
    public DateTimeOffset? LastFetchAt { get; init; }

    public IReadOnlyList<RemoteInfo> Remotes { get; init; } = [];
    public IReadOnlyList<BranchInfo> Branches { get; init; } = [];
    public IReadOnlyList<WorktreeInfo> Worktrees { get; init; } = [];
    public IReadOnlyList<StashInfo> Stashes { get; init; } = [];

    [JsonIgnore]
    public int DirtyCount => Staged + Unstaged + Conflicted;

    [JsonIgnore]
    public bool IsDirty => DirtyCount > 0;
}

public sealed record CommitInfo
{
    public required string Sha { get; init; }
    public DateTimeOffset Date { get; init; }
    public string Subject { get; init; } = "";
    public string Author { get; init; } = "";
}

public sealed record RemoteInfo
{
    public required string Name { get; init; }

    /// <summary>Fetch URL with any embedded credentials removed.</summary>
    public required string Url { get; init; }

    /// <summary>Lower-cased <c>host/owner/repo</c>, or null when the URL is not a hosted remote.</summary>
    public string? Normalized { get; init; }
}

public sealed record BranchInfo
{
    public required string Name { get; init; }
    public string Sha { get; init; } = "";
    public string? Upstream { get; init; }
    public bool UpstreamGone { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public bool IsCurrent { get; init; }

    /// <summary>Null when the default branch could not be determined.</summary>
    public bool? MergedIntoDefault { get; init; }
    public DateTimeOffset? LastCommitDate { get; init; }
    public string? Subject { get; init; }
}

public sealed record WorktreeInfo
{
    public required string Path { get; init; }
    public string? Head { get; init; }
    public string? Branch { get; init; }
    public bool Detached { get; init; }
    public bool Bare { get; init; }
    public bool Locked { get; init; }
    public bool Prunable { get; init; }
    public string? PrunableReason { get; init; }
}

public sealed record StashInfo
{
    public int Index { get; init; }

    /// <summary>The stash commit. Identifies the stash even when indexes shift. Null from older agents.</summary>
    public string? Sha { get; init; }
    public DateTimeOffset Date { get; init; }
    public string Message { get; init; } = "";
    public string? Branch { get; init; }
}

public sealed record DescriptionInputs
{
    public string? ReadmeExcerpt { get; init; }
    public IReadOnlyList<ManifestInfo> Manifests { get; init; } = [];
}

public sealed record ManifestInfo
{
    /// <summary>File name relative to the repo, e.g. <c>package.json</c>.</summary>
    public required string File { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
}

public sealed record SizeInfo
{
    public long TotalBytes { get; init; }
    public long ReclaimableBytes { get; init; }
    public IReadOnlyList<ReclaimableDir> ReclaimableDirs { get; init; } = [];
    public DateTimeOffset MeasuredAt { get; init; }
}

public sealed record ReclaimableDir
{
    public required string Path { get; init; }
    public long Bytes { get; init; }
}

/// <summary>Shallow summary of a folder that is not a git repo.</summary>
public sealed record FolderSummary
{
    public int FileCount { get; init; }
    public int DirectoryCount { get; init; }
    public IReadOnlyList<string> SampleNames { get; init; } = [];
}

/// <summary>Response to a snapshot push.</summary>
public sealed record IngestResponse
{
    public required string MachineId { get; init; }
    public int Entries { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
}
