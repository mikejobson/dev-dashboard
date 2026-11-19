using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevDash.Contracts;

/// <summary>
/// Version of the snapshot wire format. Bump <see cref="Version"/> only for a breaking change, and
/// raise <see cref="MinSupportedVersion"/> one release later, so a hub always accepts the agents of
/// the release before it: agents update one at a time, and one that has not updated yet must not be
/// locked out. Adding an optional field is not a breaking change: both sides ignore fields they do
/// not know and treat a missing one as "unknown" (see <see cref="MachineInfo.UpdateMode"/>).
/// </summary>
public static class SnapshotSchema
{
    /// <summary>What this build writes.</summary>
    public const int Version = 1;

    /// <summary>The oldest format a hub of this build still accepts.</summary>
    public const int MinSupportedVersion = 1;

    public static bool IsSupported(int version) => version is >= MinSupportedVersion and <= Version;

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

    /// <summary>
    /// The agent's <c>Agent:Updates:Mode</c>: one of <see cref="AgentUpdateModes"/>. Null from an agent
    /// that predates self-update (0.1.x): the hub then offers no Update button, and that machine needs
    /// one reinstall.
    /// </summary>
    public string? UpdateMode { get; init; }

    /// <summary>
    /// True when this agent checks the hub for requests and knows the Refresh action, so the dashboard
    /// can ask it to rescan one entry now. False from agents before 0.5.0, and from an agent with both
    /// actions and updates switched off, which only reports.
    /// </summary>
    public bool AcceptsRefresh { get; init; }

    /// <summary>How the agent's most recent self-update went, including a roll back. Null when it has never updated itself.</summary>
    public AgentUpdateReport? LastUpdate { get; init; }
}

/// <summary>The values of <c>Agent:Updates:Mode</c>.</summary>
public static class AgentUpdateModes
{
    /// <summary>The agent never updates itself and refuses update actions.</summary>
    public const string Off = "off";

    /// <summary>The default: the agent updates when asked from the dashboard (or by <c>devdash-agent update</c>), never on its own.</summary>
    public const string Notify = "notify";

    /// <summary>As <see cref="Notify"/>, and the agent also looks for new releases itself and installs them.</summary>
    public const string Auto = "auto";

    /// <summary>Lower-cases and checks a configured value. Null when it is not one of the three.</summary>
    public static string? Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Off => Off,
        Notify => Notify,
        Auto => Auto,
        _ => null,
    };

    /// <summary>True when an agent reporting this mode accepts an update queued from the dashboard.</summary>
    public static bool AcceptsRequests(string? mode) => Normalize(mode) is Notify or Auto;
}

public static class AgentUpdateOutcomes
{
    /// <summary>The new version started and reported to the hub.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>The new version did not report in, so the agent put the previous one back.</summary>
    public const string RolledBack = "rolledBack";

    /// <summary>The update was abandoned and nothing was left changed.</summary>
    public const string Failed = "failed";
}

/// <summary>The outcome of an agent's last self-update, reported in every snapshot until the next one.</summary>
public sealed record AgentUpdateReport
{
    /// <summary>One of <see cref="AgentUpdateOutcomes"/>.</summary>
    public required string Outcome { get; init; }

    /// <summary>The version the agent was on before the update.</summary>
    public string FromVersion { get; init; } = "";

    /// <summary>The version it tried to move to.</summary>
    public string ToVersion { get; init; } = "";
    public DateTimeOffset At { get; init; }
    public string? Detail { get; init; }
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

    /// <summary>
    /// A compact commit graph for the branch map: what is not on the default branch yet, a little of the
    /// default branch for context, and the commits branches fork from. Null from an agent that predates
    /// it (0.3.x and older), and on a linked worktree whose main working tree is scanned as well.
    /// </summary>
    public CommitGraph? Graph { get; init; }

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

    /// <summary>The commit the stash was made on. Null from older agents.</summary>
    public string? BaseSha { get; init; }
}

/// <summary>
/// Part of a repository's commit graph, small enough to send with every snapshot. Commits are
/// content-addressed, so the hub joins the graphs of several machines by <see cref="GraphCommit.Sha"/>.
/// </summary>
public sealed record CommitGraph
{
    /// <summary>The ref the default branch was read from: <c>origin/main</c>, or the local branch when there is no remote one.</summary>
    public string? TrunkRef { get; init; }

    public IReadOnlyList<GraphCommit> Commits { get; init; } = [];

    /// <summary>Remote-tracking tips that matter: the upstream of each local branch and the default branch's remote ref.</summary>
    public IReadOnlyList<GraphRef> RemoteRefs { get; init; } = [];

    /// <summary>True when there were more commits off the default branch than the agent sends.</summary>
    public bool Truncated { get; init; }

    /// <summary>How many commits are off the default branch in all, when <see cref="Truncated"/> and it could be counted.</summary>
    public int? SideCommitCount { get; init; }
}

public sealed record GraphCommit
{
    public required string Sha { get; init; }

    /// <summary>All parents, whether or not they are in <see cref="CommitGraph.Commits"/>.</summary>
    public IReadOnlyList<string> Parents { get; init; } = [];
    public DateTimeOffset Date { get; init; }
    public string Author { get; init; } = "";
    public string Subject { get; init; } = "";

    /// <summary>
    /// For a commit on the default branch's first-parent line: how many commits that line has up to and
    /// including this one. The same on every machine, so the hub can order these commits and say how
    /// many lie between two of them.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TrunkPos { get; init; }

    /// <summary>
    /// True for a commit that is part of the default branch's history but not on its first-parent line:
    /// the tip of a branch that was merged, for instance. Such a commit has no <see cref="TrunkPos"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool InTrunk { get; init; }

    /// <summary>True when the commit is on a local branch and on no remote-tracking branch of any remote.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Unpushed { get; init; }
}

/// <summary>A remote-tracking ref and the commit it points at, as this machine last fetched it.</summary>
public sealed record GraphRef
{
    /// <summary>Short name, e.g. <c>origin/feature-x</c>.</summary>
    public required string Name { get; init; }
    public required string Sha { get; init; }
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
