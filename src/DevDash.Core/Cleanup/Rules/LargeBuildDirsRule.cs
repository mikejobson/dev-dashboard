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
                var top = string.Join(", ", dirs.Take(3).Select(d => $"{System.IO.Path.GetRelativePath(entry.Path, d.Path)} ({Size(d.Bytes)})"));
                yield return this.ForEntry(snapshot, entry, size.ReclaimableBytes >= context.Options.BuildDirBytes * 4 ? FindingSeverity.Medium : FindingSeverity.Low,
                    $"{entry.Name}: {Size(size.ReclaimableBytes)} of build output",
                    $"{Size(size.ReclaimableBytes)} of {Size(size.TotalBytes)} is regenerable. Largest: {top}.",
                    [
                        Inspect("Size of each folder", "du -sh " + string.Join(' ', dirs.Select(d => Quote(d.Path)))),
                        Fix("Delete the build output", "rm -rf " + string.Join(' ', dirs.Select(d => Quote(d.Path)))),
                    ]);
            }
        }
    }
}
