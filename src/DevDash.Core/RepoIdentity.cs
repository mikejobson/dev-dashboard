using DevDash.Contracts;

namespace DevDash.Core;

/// <summary>Decides which local entries are "the same repo" across machines and folders.</summary>
public static class RepoIdentity
{
    public const string LocalPrefix = "local:";

    /// <summary>The remote used for identity: <c>origin</c> when present, else the first hosted remote.</summary>
    public static RemoteInfo? PrimaryRemote(GitState? git)
    {
        if (git is null)
        {
            return null;
        }

        return git.Remotes.FirstOrDefault(r => r.Name == "origin" && r.Normalized is not null)
            ?? git.Remotes.FirstOrDefault(r => r.Normalized is not null);
    }

    /// <summary>
    /// <c>host/owner/repo</c> for clones of a hosted repo; otherwise a key unique to the machine and path.
    /// </summary>
    public static string KeyFor(string machineId, DevEntry entry) =>
        PrimaryRemote(entry.Git)?.Normalized ?? $"{LocalPrefix}{machineId}:{entry.Path}";

    public static bool IsLocalOnly(string key) => key.StartsWith(LocalPrefix, StringComparison.Ordinal);

    /// <summary>True for folder names that conventionally hold linked worktrees, e.g. <c>repo.worktrees</c>.</summary>
    public static bool IsWorktreeFolderName(string name) =>
        name.EndsWith(".worktrees", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("-worktrees", StringComparison.OrdinalIgnoreCase)
        || name.Equals("worktrees", StringComparison.OrdinalIgnoreCase);

    /// <summary>Flattens top-level entries and the children of worktree containers.</summary>
    public static IEnumerable<DevEntry> Flatten(IEnumerable<DevEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in Flatten(entry.Children))
            {
                yield return child;
            }
        }
    }
}
