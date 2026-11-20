using DevDash.Contracts;
using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>Build output and dependency folders that can be regenerated.</summary>
public sealed class LargeBuildDirsRule : ICleanupRule
{
    public string Id => "large-build-dirs";
    public string Title => "Large reclaimable build dirs";
    public string Description => "node_modules, bin/obj, .venv, target, dist and .next folders that a build or install recreates.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        foreach (var snapshot in context.Machines)
        {
            foreach (var entry in RepoIdentity.Flatten(snapshot.Entries))
            {
                if (entry.Size is not { } size || size.ReclaimableBytes < context.Options.BuildDirBytes)
                {
                    continue;
                }

                var dirs = size.ReclaimableDirs.OrderByDescending(d => d.Bytes).ToList();
                var top = string.Join(", ", dirs.Take(3).Select(d => $"{RelativeTo(entry.Path, d.Path)} ({Size(d.Bytes)})"));
                yield return this.ForEntry(snapshot, entry, size.ReclaimableBytes >= context.Options.BuildDirBytes * 4 ? FindingSeverity.Medium : FindingSeverity.Low,
                    $"{entry.Name}: {Size(size.ReclaimableBytes)} of build output",
                    $"{Size(size.ReclaimableBytes)} of {Size(size.TotalBytes)} is regenerable. Largest: {top}.",
                    [
                        Inspect("Size of each folder", "du -sh " + string.Join(' ', dirs.Select(d => Quote(d.Path)))),
                        // An agent can do this in a repository, where git can say that nothing in the folders is tracked.
                        Fix("Delete the build output (for good: a build recreates it)", "rm -rf " + string.Join(' ', dirs.Select(d => Quote(d.Path))),
                            entry is { Kind: EntryKind.GitRepo, Git: not null } ? AgentAction.CleanBuildDirs(entry.Path, dirs.Select(d => d.Path)) : null),
                    ]);
            }
        }
    }

    /// <summary>
    /// The folder relative to the repo, keeping the reporting machine's separators. Not
    /// Path.GetRelativePath: the hub may run on a different OS from the machine that sent the paths.
    /// </summary>
    public static string RelativeTo(string root, string path)
    {
        var trimmed = root.TrimEnd('/', '\\');
        return path.Length > trimmed.Length + 1
            && path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
            && path[trimmed.Length] is '/' or '\\'
            ? path[(trimmed.Length + 1)..]
            : path;
    }
}
