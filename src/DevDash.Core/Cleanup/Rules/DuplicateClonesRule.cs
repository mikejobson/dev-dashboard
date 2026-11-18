namespace DevDash.Core.Cleanup.Rules;

/// <summary>The same remote cloned more than once on one machine (linked worktrees do not count).</summary>
public sealed class DuplicateClonesRule : ICleanupRule
{
    public string Id => "duplicate-clones";
    public string Title => "Duplicate clones on one machine";
    public string Description => "Separate clones of the same remote on one machine. A worktree usually does the job with one copy of the history.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        var groups = context.Repositories()
            .Where(c => !RepoIdentity.IsLocalOnly(c.RepoKey))
            .GroupBy(c => (c.Machine.MachineId, c.RepoKey));

        foreach (var group in groups)
        {
            var clones = group.OrderBy(c => c.Entry.Path, StringComparer.Ordinal).ToList();
            if (clones.Count < 2)
            {
                continue;
            }

            var first = clones[0];
            yield return new Finding
            {
                RuleId = Id,
                Severity = FindingSeverity.Low,
                Title = $"{group.Key.RepoKey} is cloned {clones.Count} times on {first.Machine.Hostname}",
                Explanation = "Separate clones: " + string.Join(", ", clones.Select(c => $"{c.Entry.Path} ({c.Git.Branch ?? "detached"})"))
                    + ". Keep one and use `git worktree add` for parallel branches.",
                Commands =
                [
                    .. clones.Select(c => RuleHelpers.Inspect($"State of {c.Entry.Name}", RuleHelpers.GitCmd(c.Entry.Path, "status --short --branch"))),
                    .. RuleHelpers.Open(first.Entry.Path),
                ],
                MachineId = first.Machine.MachineId,
                MachineName = first.Machine.Hostname,
                Path = first.Entry.Path,
                RepoKey = group.Key.RepoKey,
                Fingerprint = Finding.MakeFingerprint(Id, [group.Key.MachineId, group.Key.RepoKey, .. clones.Select(c => c.Entry.Path)]),
            };
        }
    }
}
