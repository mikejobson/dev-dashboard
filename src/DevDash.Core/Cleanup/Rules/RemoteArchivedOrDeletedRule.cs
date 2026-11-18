using DevDash.Core.GitHub;
using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>Local clones whose GitHub repo is archived, deleted or no longer visible.</summary>
public sealed class RemoteArchivedOrDeletedRule : ICleanupRule
{
    public string Id => "remote-archived-or-deleted";
    public string Title => "Remote archived or deleted on GitHub";
    public string Description => "The GitHub repo behind a local clone is archived, or can no longer be found.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        foreach (var repo in context.Repositories())
        {
            var key = repo.RepoKey;
            if (!GitHubKeys.IsGitHubKey(key) || !context.RemoteRepos.TryGetValue(key, out var lookup))
            {
                continue;
            }

            if (!lookup.Found)
            {
                yield return this.ForClone(repo, FindingSeverity.Medium,
                    $"{repo.Entry.Name}: remote not found on GitHub",
                    $"{key} was deleted, renamed without a redirect, or your token can no longer see it. "
                    + "If it was deleted, this clone may be the only copy.",
                    [
                        Inspect("Where the clone points", GitCmd(repo.Entry.Path, "remote -v")),
                        Inspect("Local state", GitCmd(repo.Entry.Path, "status --short --branch")),
                    ],
                    "missing");
            }
            else if (lookup.Repo is { IsArchived: true })
            {
                yield return this.ForClone(repo, FindingSeverity.Low,
                    $"{repo.Entry.Name}: remote is archived",
                    $"{lookup.Repo.NameWithOwner} is archived on GitHub and read-only. The local clone is probably no longer needed.",
                    [
                        Inspect("Local state", GitCmd(repo.Entry.Path, "status --short --branch")),
                        Inspect("Commits that were never pushed", GitCmd(repo.Entry.Path, "log --branches --not --remotes --oneline")),
                    ],
                    "archived");
            }
        }
    }
}
