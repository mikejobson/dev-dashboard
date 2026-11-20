namespace DevDash.Core.GitHub;

public enum CiState
{
    None,
    Pending,
    Success,
    Failure,
}

public sealed record GitHubRepo
{
    public required string NameWithOwner { get; init; }
    public string Url { get; init; } = "";
    public string? Description { get; init; }
    public DateTimeOffset? PushedAt { get; init; }
    public bool IsArchived { get; init; }
    public bool IsPrivate { get; init; }
    public bool IsFork { get; init; }
    public string? DefaultBranch { get; init; }
    public string? HeadSha { get; init; }
    public DateTimeOffset? HeadCommittedAt { get; init; }
    public string? HeadMessage { get; init; }

    /// <summary>Author of the default-branch head commit (best available proxy for "who pushed").</summary>
    public string? LastPusher { get; init; }
    public int OpenPullRequests { get; init; }
    public string? PrimaryLanguage { get; init; }

    public string Owner => NameWithOwner.Split('/')[0];

    /// <summary>Join key matching <see cref="DevDash.Contracts.RemoteInfo.Normalized"/>.</summary>
    public string Key => GitHubKeys.ForRepo(NameWithOwner);
}

public sealed record PullRequestInfo
{
    public required string Repo { get; init; }
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Author { get; init; }
    public bool IsDraft { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>APPROVED, CHANGES_REQUESTED, REVIEW_REQUIRED or null.</summary>
    public string? ReviewDecision { get; init; }

    /// <summary>MERGEABLE, CONFLICTING or UNKNOWN.</summary>
    public string? Mergeable { get; init; }
    public CiState Ci { get; init; }
    public string? HeadRef { get; init; }

    /// <summary>The commit the pull request's branch is at on GitHub. Null in a poll cached before this was asked for.</summary>
    public string? HeadSha { get; init; }

    /// <summary>The head branch lives in a fork, so its name says nothing about a branch of this repository.</summary>
    public bool IsCrossRepository { get; init; }
    public string? BaseRef { get; init; }
    public bool Authored { get; init; }
    public bool ReviewRequested { get; init; }
    public bool Assigned { get; init; }

    public string RepoKey => GitHubKeys.ForRepo(Repo);
}

public sealed record IssueInfo
{
    public required string Repo { get; init; }
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Author { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public bool Assigned { get; init; }
    public bool Mentioned { get; init; }

    public string RepoKey => GitHubKeys.ForRepo(Repo);
}

/// <summary>Result of looking a locally cloned remote up on GitHub.</summary>
public sealed record RemoteRepoLookup
{
    public required string Key { get; init; }

    /// <summary>False when GitHub says the repo does not exist or the token cannot see it.</summary>
    public bool Found { get; init; }
    public GitHubRepo? Repo { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
}

/// <summary>Everything the hub knows from GitHub after one poll.</summary>
public sealed record GitHubSnapshot
{
    public string? ViewerLogin { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    public IReadOnlyList<string> Organizations { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> PullRequests { get; init; } = [];
    public IReadOnlyList<IssueInfo> Issues { get; init; } = [];
    public IReadOnlyList<GitHubRepo> RecentRepos { get; init; } = [];

    /// <summary>Lookups for remotes that agents reported, keyed by <c>github.com/owner/repo</c>.</summary>
    public IReadOnlyDictionary<string, RemoteRepoLookup> LocalRemotes { get; init; } = new Dictionary<string, RemoteRepoLookup>();
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static GitHubSnapshot Empty { get; } = new();
}

public static class GitHubKeys
{
    public const string Host = "github.com";

    public static string ForRepo(string nameWithOwner) => $"{Host}/{nameWithOwner}".ToLowerInvariant();

    public static bool IsGitHubKey(string key) => key.StartsWith(Host + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits <c>github.com/owner/repo</c> into owner and repo. Returns false for anything else.</summary>
    public static bool TrySplit(string key, out string owner, out string name)
    {
        owner = name = "";
        var parts = key.Split('/');
        if (parts.Length != 3 || !parts[0].Equals(Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        owner = parts[1];
        name = parts[2];
        return owner.Length > 0 && name.Length > 0;
    }
}
