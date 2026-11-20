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

    /// <summary>
    /// True when this agent knows the RemoveProject action and has actions on: it can move a project the
    /// safety analysis calls safe to the trash. False from agents before 0.6.0, which could not read the request.
    /// </summary>
    public bool AcceptsRemoveProject { get; init; }

    /// <summary>True when this agent knows the Clone action and has actions on: it can put a removed project back.</summary>
    public bool AcceptsClone { get; init; }

    /// <summary>True when this agent knows the CleanBuildDirs action and has actions on: it can delete build output.</summary>
    public bool AcceptsCleanBuildDirs { get; init; }

    /// <summary>True when this agent knows the PullFastForward action and has actions on: it can fast-forward a branch.</summary>
    public bool AcceptsPull { get; init; }

    /// <summary>How the agent's most recent self-update went, including a roll back. Null when it has never updated itself.</summary>
    public AgentUpdateReport? LastUpdate { get; init; }

    /// <summary>
    /// The SDKs and runtimes installed on this machine, collected at start and every few hours. Null from
    /// an agent that predates it (before 0.6.0) or has <c>Agent:Toolchains:Enabled</c> off: the hub then
    /// says nothing about whether a repo's pinned version is installed there.
    /// </summary>
    public ToolchainInventory? Toolchains { get; init; }

    /// <summary>
    /// Whether this agent runs the slow dependency checks (<c>Agent:Dependencies:Enabled</c>). Null from
    /// an agent that predates them.
    /// </summary>
    public bool? DependencyChecks { get; init; }
}

/// <summary>The names used for <see cref="InstalledToolchain.Tool"/> and <see cref="ToolchainPin.Tool"/>.</summary>
public static class ToolNames
{
    public const string Dotnet = "dotnet";
    public const string Node = "node";
    public const string Python = "python";
    public const string Go = "go";
    public const string Rust = "rust";
    public const string Java = "java";

    /// <summary>What a person calls it.</summary>
    public static string Display(string tool) => tool switch
    {
        Dotnet => ".NET SDK",
        Node => "Node",
        Python => "Python",
        Go => "Go",
        Rust => "Rust",
        Java => "Java",
        _ => tool,
    };
}

/// <summary>What is installed on a machine, as far as the agent could find out without a shell.</summary>
public sealed record ToolchainInventory
{
    public DateTimeOffset CollectedAt { get; init; }
    public IReadOnlyList<InstalledToolchain> Tools { get; init; } = [];

    public InstalledToolchain? Find(string tool) => Tools.FirstOrDefault(t => string.Equals(t.Tool, tool, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One tool and every version of it found on the machine.</summary>
public sealed record InstalledToolchain
{
    /// <summary>One of <see cref="ToolNames"/>.</summary>
    public required string Tool { get; init; }

    /// <summary>The version the tool itself reports when run (<c>node --version</c>). Null when only a version manager's folders were found.</summary>
    public string? Active { get; init; }

    /// <summary>Every version found, <see cref="Active"/> included, without a leading <c>v</c>. For .NET these are the SDKs.</summary>
    public IReadOnlyList<string> Versions { get; init; } = [];

    /// <summary>Version managers whose folders were found: <c>nvm</c>, <c>fnm</c>, <c>volta</c>, <c>pyenv</c>, <c>asdf</c>, <c>mise</c>.</summary>
    public IReadOnlyList<string> Managers { get; init; } = [];
}

/// <summary>A version a repository asks for, exactly as its file says it.</summary>
public sealed record ToolchainPin
{
    /// <summary>One of <see cref="ToolNames"/>.</summary>
    public required string Tool { get; init; }

    /// <summary>
    /// The version or range as written: <c>10.0.100</c>, <c>&gt;=18</c>, <c>lts/iron</c>, <c>3.12</c>. From
    /// <c>.tool-versions</c> it may be several versions separated by spaces: any of them will do.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>Where it was read: <c>global.json</c>, <c>.nvmrc</c>, <c>.node-version</c>, <c>package.json engines.node</c>, <c>package.json volta.node</c>, <c>.python-version</c>, <c>.tool-versions</c>.</summary>
    public required string Source { get; init; }

    /// <summary><c>global.json</c> only: <c>sdk.rollForward</c> as written. Null when the file does not set it (the SDK then uses <c>latestPatch</c>).</summary>
    public string? RollForward { get; init; }

    /// <summary><c>global.json</c> only: <c>sdk.allowPrerelease</c> when the file sets it.</summary>
    public bool? AllowPrerelease { get; init; }
}

/// <summary>
/// What the package tools said about a repository's dependencies, the last time the agent asked them
/// (once a day by default). Counts and a few names only: never the tools' full output.
/// </summary>
public sealed record DependencyHealth
{
    /// <summary>Most package names sent in any one list.</summary>
    public const int MaxNames = 10;

    public DateTimeOffset CheckedAt { get; init; }
    public IReadOnlyList<DependencyReport> Reports { get; init; } = [];

    [JsonIgnore]
    public int Critical => Reports.Sum(r => r.Vulnerable?.Critical ?? 0);

    [JsonIgnore]
    public int High => Reports.Sum(r => r.Vulnerable?.High ?? 0);

    [JsonIgnore]
    public int VulnerableTotal => Reports.Sum(r => r.Vulnerable?.Total ?? 0);

    [JsonIgnore]
    public int OutdatedTotal => Reports.Sum(r => r.Outdated ?? 0);
}

/// <summary>One ecosystem in one repository.</summary>
public sealed record DependencyReport
{
    public const string NuGet = "nuget";
    public const string Npm = "npm";

    /// <summary><see cref="NuGet"/> or <see cref="Npm"/>.</summary>
    public required string Ecosystem { get; init; }

    /// <summary>The file the tool was pointed at, relative to the repository: <c>App.sln</c>, <c>package-lock.json</c>.</summary>
    public string? Target { get; init; }

    /// <summary>Packages with a known vulnerability, by severity. Null when that check did not run or failed; <see cref="Skipped"/> says why.</summary>
    public SeverityCounts? Vulnerable { get; init; }

    /// <summary>The worst of them, most severe first, at most <see cref="DependencyHealth.MaxNames"/>.</summary>
    public IReadOnlyList<VulnerablePackage> TopVulnerable { get; init; } = [];

    /// <summary>How many packages have a newer version. Null when that check did not run or failed.</summary>
    public int? Outdated { get; init; }

    /// <summary>Some of them, at most <see cref="DependencyHealth.MaxNames"/>.</summary>
    public IReadOnlyList<OutdatedPackage> TopOutdated { get; init; } = [];

    /// <summary>Why a check was skipped, in a few words: "not restored", "timed out", "the package source could not be reached".</summary>
    public string? Skipped { get; init; }
}

public sealed record SeverityCounts
{
    public int Critical { get; init; }
    public int High { get; init; }
    public int Moderate { get; init; }
    public int Low { get; init; }

    [JsonIgnore]
    public int Total => Critical + High + Moderate + Low;
}

public sealed record VulnerablePackage
{
    public required string Name { get; init; }
    public string? Version { get; init; }

    /// <summary><c>critical</c>, <c>high</c>, <c>moderate</c> or <c>low</c>.</summary>
    public string Severity { get; init; } = "";

    /// <summary>Advisory ids, e.g. <c>GHSA-xxxx-xxxx-xxxx</c>. At most three per package.</summary>
    public IReadOnlyList<string> Advisories { get; init; } = [];
}

public sealed record OutdatedPackage
{
    public required string Name { get; init; }
    public string? Current { get; init; }
    public string? Latest { get; init; }
}

/// <summary>
/// Commits per day for the last year, across every branch and remote-tracking ref of one repository.
/// The day is the commit's author date, in the author's own time zone: the day the work was done,
/// which a rebase does not move. Only days with commits are listed.
/// </summary>
public sealed record CommitActivity
{
    /// <summary><c>yyyy-MM-dd</c> to the number of commits authored that day.</summary>
    public IReadOnlyDictionary<string, int> Days { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// The same, counting only commits whose author email is the repository's configured
    /// <c>user.email</c>. Null when no <c>user.email</c> is configured there. The email is compared on
    /// the machine and never sent.
    /// </summary>
    public IReadOnlyDictionary<string, int>? Mine { get; init; }
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

    /// <summary>
    /// The tool versions the folder's own files ask for (<c>global.json</c>, <c>.nvmrc</c>,
    /// <c>.python-version</c>…). Null when it pins nothing, and from agents before 0.6.0.
    /// </summary>
    public IReadOnlyList<ToolchainPin>? Pins { get; init; }

    /// <summary>The last dependency check of this repository. Null until one has run, and on machines where they are off.</summary>
    public DependencyHealth? Dependencies { get; init; }
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

    /// <summary>
    /// For a linked worktree: the path of the main working tree it belongs to, as git reports it.
    /// Null on a main working tree, and from agents before 0.6.0 (the first of <see cref="Worktrees"/> is the same thing).
    /// </summary>
    public string? MainWorktreePath { get; init; }

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

    /// <summary>
    /// What it takes to say whether this working tree holds the only copy of anything: local-only tags,
    /// ignored and secret-looking files, submodules. Null from an agent that predates it (before 0.6.0):
    /// the hub then never calls the repo safe to remove.
    /// </summary>
    public SafetyFacts? Safety { get; init; }

    /// <summary>
    /// Commits per day for the last year. Null from an agent that predates it (before 0.6.0), and on a
    /// linked worktree whose main working tree is scanned as well: they share one history.
    /// </summary>
    public CommitActivity? Activity { get; init; }

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

    /// <summary>
    /// Whether the branch is merged into the line the branch map is drawn along
    /// (<see cref="CommitGraph.TrunkRef"/>), when that is not the default branch: <c>develop</c> in a
    /// git-flow repository. Null when the two are the same line, or the agent does not report it
    /// (before 0.6.0): <see cref="MergedIntoDefault"/> then answers for the map too. Only the map
    /// reads this; cleanup keeps meaning "merged into the default branch".
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MergedIntoTrunk { get; init; }
    public DateTimeOffset? LastCommitDate { get; init; }
    public string? Subject { get; init; }

    /// <summary>
    /// True when the branch's tip is reachable from some remote-tracking ref of any remote, as last
    /// fetched: nothing on it exists only here. That covers a branch that was merged and then deleted
    /// on the remote. Null when it was not worked out (agents before 0.6.0).
    /// </summary>
    public bool? OnRemote { get; init; }

    /// <summary>How many of the branch's commits are on no remote-tracking ref. Null when not counted (no remote, or too many to walk).</summary>
    public int? UnpushedCount { get; init; }

    /// <summary>The commit date of the oldest of those commits. Null when there are none or they were not counted.</summary>
    public DateTimeOffset? OldestUnpushedAt { get; init; }
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
/// The extra facts behind the "only copy" and "safe to remove" analysis. Names and counts only: the
/// agent never reads or sends the contents of a file. Every list is capped at <see cref="MaxNames"/>;
/// the matching count is the real total.
/// </summary>
public sealed record SafetyFacts
{
    /// <summary>Most names sent in any one list.</summary>
    public const int MaxNames = 50;

    /// <summary>How many tags the repository has.</summary>
    public int TagCount { get; init; }

    /// <summary>Tags that are on no remote (or point at something else there). Without a remote, every tag.</summary>
    public IReadOnlyList<string> LocalOnlyTags { get; init; } = [];
    public int LocalOnlyTagCount { get; init; }

    /// <summary>
    /// When the agent last listed the remotes' tags to compare. Null when it never has (fetching is off
    /// on that machine, or the remote could not be reached): with a remote and <see cref="TagCount"/>
    /// above zero, the local-only tags are then unknown, not none.
    /// </summary>
    public DateTimeOffset? RemoteTagsCheckedAt { get; init; }

    /// <summary>
    /// True when <see cref="BranchInfo.OnRemote"/> was worked out for every branch. False when the
    /// repository has a remote but the check failed.
    /// </summary>
    public bool BranchesChecked { get; init; }

    /// <summary>
    /// Whether the checked-out commit is on some remote-tracking ref. Matters for a detached HEAD, whose
    /// commits no branch holds. Null when unknown or there is no commit yet.
    /// </summary>
    public bool? HeadOnRemote { get; init; }

    /// <summary>
    /// Ignored files and folders that are not known build output or caches, relative to the working
    /// tree: local config, databases, notes. A folder ends with <c>/</c>. Deleting the clone loses them.
    /// </summary>
    public IReadOnlyList<string> IgnoredFiles { get; init; } = [];
    public int IgnoredFileCount { get; init; }

    /// <summary>
    /// Untracked files with a name that looks like a secret (<c>.env</c>, <c>*.pem</c>, <c>id_rsa</c>…)
    /// that <c>.gitignore</c> does not cover, relative to the working tree. Names only.
    /// </summary>
    public IReadOnlyList<string> SecretFiles { get; init; } = [];
    public int SecretFileCount { get; init; }

    /// <summary>Every submodule of the working tree, nested ones included.</summary>
    public IReadOnlyList<SubmoduleInfo> Submodules { get; init; } = [];
}

/// <summary>One submodule, as seen from the working tree that contains it.</summary>
public sealed record SubmoduleInfo
{
    /// <summary>Path relative to the containing working tree.</summary>
    public required string Path { get; init; }

    /// <summary>False when the submodule is registered but not checked out here: there is nothing local to lose.</summary>
    public bool Initialized { get; init; }

    /// <summary>False when the agent did not look inside (too many submodules, or git failed): its state is unknown.</summary>
    public bool Checked { get; init; }

    /// <summary>Uncommitted changes or untracked files inside the submodule.</summary>
    public bool Dirty { get; init; }

    /// <summary>The submodule has commits, on its HEAD or a local branch, that are on none of its remotes.</summary>
    public bool Unpushed { get; init; }

    /// <summary>Stashes kept in the submodule's own repository.</summary>
    public int Stashes { get; init; }
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

    /// <summary>
    /// For a commit that is in the default branch's history but not on its first-parent line (a merged
    /// branch's tip, or the commit a branch forks from inside one): the first commit of that line to
    /// contain it, which is the merge that brought it in. The agent also sends the commits that lead
    /// from here up to that merge when they are few; when they are not, this is the only link, and the
    /// hub draws it. Null when not worked out (agents before 0.6.0, or merged too long ago).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JoinsTrunkAt { get; init; }
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
