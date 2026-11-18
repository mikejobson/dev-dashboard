using DevDash.Contracts;
using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>The same branch of the same repo has different unpushed commits on two machines.</summary>
public sealed class DivergedAcrossMachinesRule : ICleanupRule
{
    public string Id => "diverged-across-machines";
    public string Title => "Same repo diverged across machines";
    public string Description => "Two machines have the same branch checked out at different commits, and both have unpushed work.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        var groups = context.Clones()
            .Where(c => !RepoIdentity.IsLocalOnly(c.RepoKey) && c.Git is { Branch: not null, HeadSha: not null, Ahead: > 0 })
            .GroupBy(c => (c.RepoKey, Branch: c.Git.Branch!));

        foreach (var group in groups)
        {
            var perMachine = group
                .GroupBy(c => c.Machine.MachineId)
                .Select(g => g.First())
                .OrderBy(c => c.Machine.MachineId, StringComparer.Ordinal)
                .ToList();
            if (perMachine.Count < 2 || perMachine.Select(c => c.Git.HeadSha).Distinct().Count() < 2)
            {
                continue;
            }

            var first = perMachine[0];
            var where = string.Join("; ", perMachine.Select(c =>
                $"{c.Machine.Hostname} at {c.Git.HeadSha![..7]} ({c.Git.Ahead} ahead{(c.Git.Behind > 0 ? $", {c.Git.Behind} behind" : "")})"));
            yield return new Finding
            {
                RuleId = Id,
                Severity = FindingSeverity.High,
                Title = $"{first.Entry.Name}: {group.Key.Branch} diverged across {perMachine.Count} machines",
                Explanation = $"{group.Key.Branch} has different unpushed commits on each machine: {where}. "
                    + "Take the machines one at a time: rebase onto the remote, then push. The first push goes straight through; "
                    + "each later machine picks up the earlier commits in its rebase.",
                Commands = [.. perMachine.SelectMany(c => CommandsFor(c, group.Key.Branch))],
                RepoKey = group.Key.RepoKey,
                Fingerprint = Finding.MakeFingerprint(Id, [group.Key.RepoKey, group.Key.Branch, .. perMachine.Select(c => c.Machine.MachineId)]),
            };
        }
    }

    /// <summary>The same steps on every machine, so the order the machines are done in does not matter.</summary>
    private static IEnumerable<FindingCommand> CommandsFor(LocalClone clone, string branch)
    {
        var git = clone.Git;
        var path = clone.Entry.Path; // this working tree has the branch checked out
        var target = SplitUpstream(git, git.Upstream);
        var remote = target?.Remote ?? RepoIdentity.PrimaryRemote(git)?.Name ?? "origin";
        var sameName = target is null || target.Value.Branch == branch;
        var refspec = sameName ? branch : $"{branch}:{target!.Value.Branch}";

        IEnumerable<FindingCommand> commands = git.Upstream is { } upstream && !git.UpstreamGone
            ?
            [
                Inspect("Refresh from the remote", GitCmd(path, "fetch --prune"), AgentAction.Fetch(path)),
                Inspect($"Compare with {upstream} (< theirs, > yours)", GitCmd(path, "log --oneline --left-right " + Range(upstream, branch, bothSides: true))),
                Fix($"Rebase onto {upstream}", GitCmd(path, "pull --rebase")),
                // Offered to the agent only while this machine is not known to be behind; the agent checks again after fetching.
                Fix($"Then push to {remote}", GitCmd(path, $"push {Quote(remote)} {Quote(refspec)}"),
                    target is not null && sameName && git.Behind == 0 ? AgentAction.Push(path, remote, branch) : null),
            ]
            : [Inspect("Status", GitCmd(path, "status --short --branch"))];

        return commands.Concat(Open(path))
            .Select(c => c with { MachineId = clone.Machine.MachineId, MachineName = clone.Machine.Hostname });
    }
}
