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
    /// the dashboard. Empty means any tailnet user whose request reaches the hub through <c>tailscale serve</c>.
    /// </summary>
    public string AllowedUsers { get; set; } = "";

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
    public string ApiUrl { get; set; } = "https://api.github.com/graphql";
    public string RestUrl { get; set; } = "https://api.github.com";
}
