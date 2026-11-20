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

    /// <summary>
    /// Compares two paths reported by one machine. Git prints forward slashes on Windows where the
    /// scanner has backslashes, so separators and case are ignored.
    /// </summary>
    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(a.Replace('\\', '/').TrimEnd('/'), b.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The main working tree of each repository in a snapshot, by shared .git directory. A linked
    /// worktree belongs under the entry found here; one whose main working tree is outside the scan
    /// roots is not in the result.
    /// </summary>
    public static IReadOnlyDictionary<string, DevEntry> MainWorkingTrees(IEnumerable<DevEntry> entries)
    {
        var result = new Dictionary<string, DevEntry>(StringComparer.Ordinal);
        foreach (var entry in Flatten(entries))
        {
            if (entry is { Kind: EntryKind.GitRepo, Git: { IsLinkedWorktree: false, CommonDir: { } dir } })
            {
                result.TryAdd(dir, entry);
            }
        }

        return result;
    }

    /// <summary>The main working tree a linked worktree belongs to, when it is in the same snapshot.</summary>
    public static DevEntry? MainOf(DevEntry entry, IReadOnlyDictionary<string, DevEntry> mains) =>
        entry.Git is { IsLinkedWorktree: true, CommonDir: { } dir } && mains.TryGetValue(dir, out var main) ? main : null;

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
