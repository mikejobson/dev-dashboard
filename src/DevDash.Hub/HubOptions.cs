using DevDash.Core.Cleanup;

namespace DevDash.Hub;

public sealed class HubOptions
{
    public const string SectionName = "Hub";

    /// <summary>Folder for the SQLite database and the local-dev agent key. Relative paths are under the content root.</summary>
    public string DataDirectory { get; set; } = "devdash-data";

    /// <summary>A machine counts as online when its last snapshot is newer than this.</summary>
    public int OnlineWindowMinutes { get; set; } = 35;

    public CleanupOptions Cleanup { get; set; } = new();

    public HubActionOptions Actions { get; set; } = new();

    public HubUpdateOptions Updates { get; set; } = new();

    /// <summary>
    /// The address people open the dashboard at, e.g. <c>https://devdash.example.ts.net</c>. Alerts link
    /// back to it. The hub cannot know it by itself (it sits behind a proxy), so links are left out when this is empty.
    /// </summary>
    public string PublicUrl { get; set; } = "";

    public Alerts.AlertOptions Alerts { get; set; } = new();

    public Mcp.McpOptions Mcp { get; set; } = new();

    /// <summary>Repo tags, notes and pins (§20).</summary>
    public Services.RepoTagOptions Tags { get; set; } = new();
    public History.HistoryOptions History { get; set; } = new();
}

/// <summary>Agent updates: knowing the latest release, and asking agents to move to it.</summary>
public sealed class HubUpdateOptions
{
    /// <summary>
    /// Check GitHub for new agent releases and offer Update buttons. Separate from
    /// <see cref="HubActionOptions.Enabled"/>: an update is not a git action, and each agent decides for
    /// itself whether to accept one (<c>Agent:Updates:Mode</c>).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The GitHub repository whose releases are the agent's releases.</summary>
    public string Repository { get; set; } = "mikejobson/dev-dashboard";

    /// <summary>How often to look for a new release.</summary>
    public int CheckIntervalMinutes { get; set; } = 60;

    /// <summary>Also count releases marked as pre-release (or tagged like <c>v1.0.0-rc.1</c>). Drafts are never counted.</summary>
    public bool IncludePrereleases { get; set; }

    /// <summary>An update an agent has claimed but not reported on after this long is recorded as failed. Longer than for git actions: it downloads a release first.</summary>
    public int LeaseMinutes { get; set; } = 15;
}

/// <summary>Agent actions: safe fixes queued from the dashboard for a machine's agent to run.</summary>
public sealed class HubActionOptions
{
    /// <summary>Hub-wide switch. Even when on, a machine only gets Run buttons if its own agent enabled actions.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A queued action that no agent has picked up after this long will never run.</summary>
    public int ExpiryMinutes { get; set; } = 10;

    /// <summary>A claimed action with no result after this long is recorded as failed. It is not retried.</summary>
    public int LeaseMinutes { get; set; } = 5;

    /// <summary>As <see cref="LeaseMinutes"/>, for a clone: it downloads a whole repository.</summary>
    public int CloneLeaseMinutes { get; set; } = 20;

    /// <summary>
    /// As <see cref="LeaseMinutes"/>, for archiving a project or restoring one: it reads and writes the whole folder.
    /// Keep it above the agents' <c>Agent:Archive:TimeoutMinutes</c> (180), so the agent's own result arrives first.
    /// </summary>
    public int ArchiveLeaseMinutes { get; set; } = 240;

    /// <summary>How many actions the audit trail keeps.</summary>
    public int KeepLast { get; set; } = 500;
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// How browser users are identified. <c>LocalDev</c> trusts loopback connections. Phase 4 adds
    /// <c>Tailscale</c>, which reads identity headers set by <c>tailscale serve</c>.
    /// </summary>
    public string Mode { get; set; } = "LocalDev";

    /// <summary>LocalDev only: refuse requests that do not come from this machine.</summary>
    public bool RequireLoopback { get; set; } = true;

    public string LocalUser { get; set; } = "local";

    /// <summary>
    /// Tailscale only: comma-separated tailnet logins (e.g. <c>you@example.com,you@github</c>) allowed to use
    /// the dashboard. Empty means any tailnet user whose request reaches the hub through <c>tailscale serve</c>,
    /// which the dashboard warns about.
    /// </summary>
    public string AllowedUsers { get; set; } = "";

    /// <summary>
    /// Tailscale only: the loopback address the hub listens on and <c>tailscale serve</c> proxies to.
    /// Identity headers are trusted only on connections that arrive at this address. It must not be
    /// 127.0.0.1: in userspace mode tailscaled hands a tailnet peer's direct connection to the node's
    /// own IP to 127.0.0.1 on the same port, so a hub listening there could be reached around serve,
    /// with headers the peer wrote itself.
    /// </summary>
    public string ServeAddress { get; set; } = "127.0.0.2";

    public IReadOnlySet<string> AllowedUserSet =>
        AllowedUsers.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    public bool Enabled { get; set; } = true;

    /// <summary>Personal access or OAuth token. Falls back to GITHUB_TOKEN, then to <c>gh auth token</c>.</summary>
    public string? Token { get; set; }
    public int PollIntervalMinutes { get; set; } = 5;

    /// <summary>Orgs to leave out. All others you belong to are discovered automatically.</summary>
    public List<string> ExcludeOrgs { get; set; } = [];

    /// <summary>How far back "recently pushed" goes.</summary>
    public int RecentDays { get; set; } = 30;
    public int ReposPerOrg { get; set; } = 30;
    public int MentionDays { get; set; } = 14;

    /// <summary>How far back merged pull requests are listed, to recognise squash-merged local branches.</summary>
    public int MergedDays { get; set; } = 30;
    public string ApiUrl { get; set; } = "https://api.github.com/graphql";
    public string RestUrl { get; set; } = "https://api.github.com";
}
