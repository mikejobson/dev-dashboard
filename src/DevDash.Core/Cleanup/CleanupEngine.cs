using DevDash.Core.Cleanup.Rules;

namespace DevDash.Core.Cleanup;

/// <summary>Runs every rule over a context. Pure: it reads snapshots and returns findings, nothing else.</summary>
public sealed class CleanupEngine
{
    public CleanupEngine(IEnumerable<ICleanupRule>? rules = null)
    {
        Rules = (rules ?? DefaultRules()).ToList();
    }

    public IReadOnlyList<ICleanupRule> Rules { get; }

    public static IReadOnlyList<ICleanupRule> DefaultRules() =>
    [
        new NonGitFolderRule(),
        new StaleWorktreeRule(),
        new MergedBranchRule(),
        new GoneUpstreamRule(),
        new OldUncommittedChangesRule(),
        new UnpushedCommitsRule(),
        new OldStashRule(),
        new UntrackedSecretsRule(),
        new LargeBuildDirsRule(),
        new ToolchainMismatchRule(),
        new VulnerableDependenciesRule(),
        new UntouchedRepoRule(),
        new DivergedAcrossMachinesRule(),
        new RemoteArchivedOrDeletedRule(),
        new StalePullRequestRule(),
        new FailingCiPullRequestRule(),
        new DuplicateClonesRule(),
    ];

    public IReadOnlyList<Finding> Evaluate(CleanupContext context)
    {
        var findings = new List<Finding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in Rules)
        {
            foreach (var finding in rule.Evaluate(context))
            {
                if (seen.Add(finding.Fingerprint))
                {
                    findings.Add(finding);
                }
            }
        }

        return findings;
    }
}
