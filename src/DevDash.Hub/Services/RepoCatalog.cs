using DevDash.Contracts;
using DevDash.Core;
using DevDash.Core.GitHub;
using DevDash.Hub.GitHub;

namespace DevDash.Hub.Services;

/// <summary>One working tree (or folder) on one machine.</summary>
public sealed record CloneView(MachineView Machine, DevEntry Entry, string? Container)
{
    public GitState? Git => Entry.Git;
    public string DisplayName => Container is null ? Entry.Name : $"{Container}/{Entry.Name}";

    public string MachineKey() => Machine.MachineId;
}

/// <summary>A repo as the dashboard thinks of it: every clone on every machine, joined to GitHub.</summary>
public sealed record RepoRow
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Owner { get; init; }
    public EntryKind Kind { get; init; }
    public IReadOnlyList<CloneView> Clones { get; init; } = [];
    public GitHubRepo? GitHub { get; init; }

    /// <summary>GitHub says the remote does not exist (deleted, renamed away or no longer visible).</summary>
    public bool RemoteMissing { get; init; }
    public IReadOnlyList<PullRequestInfo> PullRequests { get; init; } = [];
    public IReadOnlyList<IssueInfo> Issues { get; init; } = [];
    public RepoDescription? Description { get; init; }
    public DateTimeOffset? LastActivity { get; init; }

    public bool IsHosted => !RepoIdentity.IsLocalOnly(Key);
    public bool AnyDirty => Clones.Any(c => c.Git is { IsDirty: true });
    public bool AnyAhead => Clones.Any(c => c.Git is { Ahead: > 0 });
    public bool AnyBehind => Clones.Any(c => c.Git is { Behind: > 0 });
}

/// <summary>Builds the local-to-remote join from the latest snapshots and the latest GitHub poll.</summary>
public sealed class RepoCatalog(SnapshotStore snapshots, GitHubStore github, IDescriptionProvider descriptions)
{
    public async Task<IReadOnlyList<RepoRow>> GetAsync(CancellationToken ct = default)
    {
        var gh = github.Current;
        var groups = new Dictionary<string, (EntryKind Kind, List<CloneView> Clones)>(StringComparer.OrdinalIgnoreCase);

        void Add(MachineView machine, DevEntry entry, string? container)
        {
            var key = entry.Kind == EntryKind.GitRepo
                ? RepoIdentity.KeyFor(machine.MachineId, entry)
                : $"{RepoIdentity.LocalPrefix}{machine.MachineId}:{entry.Path}";
            if (!groups.TryGetValue(key, out var group))
            {
                group = (entry.Kind, []);
                groups[key] = group;
            }

            group.Clones.Add(new CloneView(machine, entry, container));
        }

        foreach (var machine in snapshots.Machines)
        {
            foreach (var entry in machine.Snapshot.Entries)
            {
                if (entry.Kind == EntryKind.WorktreeContainer && entry.Children.Count > 0)
                {
                    foreach (var child in entry.Children)
                    {
                        Add(machine, child, entry.Name);
                    }
                }
                else
                {
                    Add(machine, entry, null);
                }
            }
        }

        var prsByRepo = gh.PullRequests.ToLookup(p => p.RepoKey, StringComparer.OrdinalIgnoreCase);
        var issuesByRepo = gh.Issues.ToLookup(i => i.RepoKey, StringComparer.OrdinalIgnoreCase);
        var recent = gh.RecentRepos.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<RepoRow>(groups.Count);
        foreach (var (key, group) in groups)
        {
            var clones = group.Clones
                .OrderBy(c => c.Machine.Machine.Hostname, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Git?.IsLinkedWorktree ?? false)
                .ThenBy(c => c.Entry.Path, StringComparer.Ordinal)
                .ToList();

            gh.LocalRemotes.TryGetValue(key.ToLowerInvariant(), out var lookup);
            var repo = lookup?.Repo ?? recent.GetValueOrDefault(key);

            string name;
            string? owner = null;
            if (RepoIdentity.IsLocalOnly(key))
            {
                name = clones[0].DisplayName;
            }
            else if (repo is not null)
            {
                var parts = repo.NameWithOwner.Split('/', 2);
                owner = parts[0];
                name = parts.Length > 1 ? parts[1] : repo.NameWithOwner;
            }
            else
            {
                var parts = key.Split('/');
                name = parts[^1];
                owner = parts.Length >= 3 ? string.Join('/', parts[1..^1]) : null;
            }

            DateTimeOffset?[] activity =
            [
                .. clones.Select(c => c.Entry.LastModified),
                .. clones.Select(c => c.Git?.LastCommit?.Date),
                repo?.PushedAt,
            ];

            rows.Add(new RepoRow
            {
                Key = key,
                Name = name,
                Owner = owner,
                Kind = group.Kind,
                Clones = clones,
                GitHub = repo,
                RemoteMissing = lookup is { Found: false },
                PullRequests = prsByRepo[key].ToList(),
                Issues = issuesByRepo[key].ToList(),
                Description = await descriptions.DescribeAsync(new DescriptionRequest(key, repo, clones.Select(c => c.Entry).ToList()), ct),
                LastActivity = activity.Max(),
            });
        }

        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
    }

    public async Task<RepoRow?> FindAsync(string key, CancellationToken ct = default) =>
        (await GetAsync(ct)).FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where a GitHub repo is cloned, e.g. for "active in org but not cloned anywhere".</summary>
    public IReadOnlyDictionary<string, List<CloneView>> ClonesByGitHubKey()
    {
        var result = new Dictionary<string, List<CloneView>>(StringComparer.OrdinalIgnoreCase);
        foreach (var machine in snapshots.Machines)
        {
            foreach (var top in machine.Snapshot.Entries)
            {
                foreach (var entry in RepoIdentity.Flatten([top]))
                {
                    if (RepoIdentity.PrimaryRemote(entry.Git)?.Normalized is { } key)
                    {
                        if (!result.TryGetValue(key, out var list))
                        {
                            result[key] = list = [];
                        }

                        list.Add(new CloneView(machine, entry, ReferenceEquals(entry, top) ? null : top.Name));
                    }
                }
            }
        }

        return result;
    }
}
