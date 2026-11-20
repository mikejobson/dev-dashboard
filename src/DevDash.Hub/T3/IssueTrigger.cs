using DevDash.Hub.GitHub;

namespace DevDash.Hub.T3;

/// <summary>A machine with a clone of the issue's repository, and whether it can start a thread now.</summary>
public sealed record IssueMachineOption(string MachineId, string Hostname, string RepoPath, bool CanStart, string? WhyNot, DateTimeOffset LastSeenAt);

public sealed record IssueTriggerInput
{
    public required TrackedIssue Issue { get; init; }
    public required IssueSettings Settings { get; init; }

    /// <summary>Null when not known: treated as public.</summary>
    public bool? RepoIsPrivate { get; init; }

    /// <summary>The user the hub's GitHub token belongs to.</summary>
    public string? ViewerLogin { get; init; }

    /// <summary>Who added the label most recently, from the issue's events. Null when that could not be found.</summary>
    public string? Labeler { get; init; }

    /// <summary>A thread was already started for the issue, in any state.</summary>
    public bool AlreadyStarted { get; init; }
    public IReadOnlyList<IssueMachineOption> Machines { get; init; } = [];
}

public enum IssueTriggerVerdict
{
    /// <summary>Nothing to do: no label, trigger off for the repo, or already started.</summary>
    Skip,

    /// <summary>A rule says a person must look first. The page shows why and offers Start.</summary>
    NeedsManualStart,

    /// <summary>It should start, but no machine can take it now. Tried again on every poll.</summary>
    Pending,
    Start,
}

public sealed record IssueTriggerDecision(IssueTriggerVerdict Verdict, string Reason, IssueMachineOption? Machine = null);

/// <summary>
/// The label trigger's rules, pure. An issue in a public repository is written by anyone, and a T3 thread
/// usually runs with full access to the machine, so the label alone is not enough:
/// <list type="number">
/// <item>The trigger must be on, for this repository, and the issue must carry the label.</item>
/// <item>Never a second thread for one issue: once one was started (whatever became of it), only a person can start another.</item>
/// <item>The label must have been added by an allowed login (by default the token's own user), read from the issue's events, not assumed.</item>
/// <item>On a repository not known to be private, the issue's author must be allowed too: otherwise anyone could
/// write the instructions and only need someone trusted to add a label.</item>
/// <item>The preferred machine if the repo has one (and only it), else the most recently seen machine with a
/// clone that can start threads now. None: pending, with the reason.</item>
/// </list>
/// </summary>
public static class IssueTrigger
{
    /// <summary>Whether the issue is one the trigger looks at at all: cheap, so the events are only read for these.</summary>
    public static bool Watches(IssueSettings settings, TrackedIssue issue) =>
        settings.AutoStart && settings.For(issue.RepoKey) is { AutoStart: true } && issue.HasLabel(settings.Label);

    public static IssueTriggerDecision Decide(IssueTriggerInput input)
    {
        var settings = input.Settings;
        var issue = input.Issue;
        if (!Watches(settings, issue))
        {
            return new IssueTriggerDecision(IssueTriggerVerdict.Skip, "not watched");
        }

        if (input.AlreadyStarted)
        {
            return new IssueTriggerDecision(IssueTriggerVerdict.Skip, "a thread was already started for this issue");
        }

        var allowed = settings.AllowedSet(input.ViewerLogin);
        if (allowed.Count == 0)
        {
            return Manual("the hub does not know which GitHub user its token belongs to yet, so nobody is allowed to trigger a thread");
        }

        if (string.IsNullOrWhiteSpace(input.Labeler))
        {
            return Manual($"could not tell from the issue's events who added the {settings.Label} label");
        }

        if (!allowed.Contains(input.Labeler))
        {
            return Manual($"the {settings.Label} label was added by {input.Labeler}, who is not allowed to trigger threads");
        }

        if (input.RepoIsPrivate != true && (issue.Author is null || !allowed.Contains(issue.Author)))
        {
            var visibility = input.RepoIsPrivate is null ? "the repository may be public" : "the repository is public";
            return Manual($"{visibility} and the issue was written by {issue.Author ?? "an unknown user"}, who is not on the allowed list: read it before starting a thread");
        }

        var preferred = settings.For(issue.RepoKey)?.PreferredMachine;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var wanted = input.Machines.FirstOrDefault(m => string.Equals(m.MachineId, preferred, StringComparison.OrdinalIgnoreCase));
            if (wanted is null)
            {
                return Pending($"the preferred machine {preferred} has no clone of this repository");
            }

            return wanted.CanStart
                ? new IssueTriggerDecision(IssueTriggerVerdict.Start, $"on {wanted.Hostname}, the preferred machine", wanted)
                : Pending($"the preferred machine {wanted.Hostname} cannot start it now: {wanted.WhyNot}");
        }

        var best = input.Machines.Where(m => m.CanStart).OrderByDescending(m => m.LastSeenAt).ThenBy(m => m.MachineId, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (best is not null)
        {
            return new IssueTriggerDecision(IssueTriggerVerdict.Start, $"on {best.Hostname}, the most recently seen machine with a clone", best);
        }

        return Pending(input.Machines.Count == 0
            ? "no machine has a clone of this repository"
            : "no machine with a clone can start a T3 thread now: " + string.Join("; ", input.Machines.Select(m => m.WhyNot).Where(w => w is not null).Distinct()));
    }

    private static IssueTriggerDecision Manual(string reason) => new(IssueTriggerVerdict.NeedsManualStart, reason);

    private static IssueTriggerDecision Pending(string reason) => new(IssueTriggerVerdict.Pending, reason);
}
