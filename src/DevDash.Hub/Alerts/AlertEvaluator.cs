using System.Globalization;
using DevDash.Contracts;
using DevDash.Core.Cleanup;
using DevDash.Core.Cleanup.Rules;
using DevDash.Core.GitHub;
using DevDash.Core.Safety;
using DevDash.Hub.Data;
using DevDash.Hub.Services;

namespace DevDash.Hub.Alerts;

public enum AlertSeverity
{
    Info,
    Warning,
    High,
}

/// <summary>The names of the triggers, as stored and as sent in a payload.</summary>
public static class AlertTriggers
{
    public const string AtRisk = "at-risk";
    public const string MachineOffline = "machine-offline";
    public const string Diverged = "diverged";
    public const string CiFailed = "ci-failed";
    public const string ActionFailed = "action-failed";
    public const string Digest = "digest";

    /// <summary>Several new alerts sent as one.</summary>
    public const string Summary = "summary";
    public const string Test = "test";

    public static string Label(string trigger) => trigger switch
    {
        AtRisk => "at risk",
        MachineOffline => "machine offline",
        Diverged => "diverged",
        CiFailed => "CI failing",
        ActionFailed => "action failed",
        Digest => "daily digest",
        Summary => "summary",
        Test => "test",
        _ => trigger,
    };
}

/// <summary>Something that is true right now and worth telling the user about.</summary>
public sealed record AlertCandidate
{
    /// <summary>Identifies the condition across evaluations: the same key is the same alert. Holds nothing volatile.</summary>
    public required string Key { get; init; }
    public required string Trigger { get; init; }
    public required string Title { get; init; }
    public string Body { get; init; } = "";

    /// <summary>The hub page about it, relative to the hub: <c>safety</c>, <c>machines#machine-x</c>.</summary>
    public required string Path { get; init; }

    /// <summary>A page outside the hub, when there is one: the pull request on GitHub.</summary>
    public string? ExternalUrl { get; init; }
    public AlertSeverity Severity { get; init; } = AlertSeverity.Warning;

    /// <summary>
    /// Something that happened, not a state that lasts: a failed action. It is sent once and never
    /// "clears and returns".
    /// </summary>
    public bool IsEvent { get; init; }

    public static string MakeKey(string trigger, params string?[] parts) => $"{trigger}:{Finding.MakeFingerprint(trigger, parts)}";
}

/// <summary>Everything the triggers look at, gathered in one place so the evaluation itself is pure.</summary>
public sealed record AlertInputs
{
    public IReadOnlyList<SafetyRow> Safety { get; init; } = [];
    public IReadOnlyList<MachineView> Machines { get; init; } = [];

    /// <summary>Cleanup findings with their snooze and dismiss decisions: a muted finding does not alert.</summary>
    public IReadOnlyList<FindingView> Findings { get; init; } = [];
    public IReadOnlyList<PullRequestInfo> PullRequests { get; init; } = [];

    /// <summary>Actions that failed recently.</summary>
    public IReadOnlyList<AgentActionEntity> FailedActions { get; init; } = [];

    /// <summary>When this hub process started. A machine is not called offline for the time the hub itself was down.</summary>
    public DateTimeOffset HubUpSince { get; init; } = DateTimeOffset.MinValue;

    /// <summary>Keys of the alerts already open, so an open "offline" stays open across a hub restart.</summary>
    public IReadOnlySet<string> OpenKeys { get; init; } = new HashSet<string>();
}

/// <summary>Turns the hub's current state into the list of things that should be alerting. Pure.</summary>
public static class AlertEvaluator
{
    private static readonly string DivergedRule = new DivergedAcrossMachinesRule().Id;
    private static readonly string FailingCiRule = new FailingCiPullRequestRule().Id;

    public static IReadOnlyList<AlertCandidate> Evaluate(AlertInputs inputs, AlertTriggerOptions triggers, DateTimeOffset now)
    {
        var result = new List<AlertCandidate>();
        if (triggers.AtRisk.Enabled)
        {
            result.AddRange(AtRisk(inputs, triggers.AtRisk, now));
        }

        if (triggers.MachineOffline.Enabled)
        {
            result.AddRange(Offline(inputs, triggers.MachineOffline, now));
        }

        if (triggers.Diverged.Enabled)
        {
            result.AddRange(Diverged(inputs));
        }

        if (triggers.CiFailed.Enabled)
        {
            result.AddRange(CiFailed(inputs, triggers.CiFailed));
        }

        if (triggers.ActionFailed.Enabled)
        {
            result.AddRange(ActionFailed(inputs));
        }

        // One alert per key, whatever the inputs repeat.
        return result.GroupBy(c => c.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();
    }

    /// <summary>One alert per working tree with a risk of the configured level that has been there long enough.</summary>
    private static IEnumerable<AlertCandidate> AtRisk(AlertInputs inputs, AtRiskTriggerOptions options, DateTimeOffset now)
    {
        var min = options.MinLevel;
        var age = TimeSpan.FromDays(options.Days);
        foreach (var row in inputs.Safety.Where(r => r.Safety.IsAtRisk))
        {
            var old = row.Safety.Risks
                .Where(r => r.Level >= min)
                .Where(r => (r.Since ?? row.Safety.OldestRiskAt ?? row.Safety.LastTouchedAt) is { } since && now - since >= age)
                .OrderByDescending(r => r.Level)
                .ThenBy(r => r.Since ?? DateTimeOffset.MaxValue)
                .ToList();
            if (old.Count == 0)
            {
                continue;
            }

            var host = row.Machine.Machine.Hostname;
            yield return new AlertCandidate
            {
                Key = AlertCandidate.MakeKey(AlertTriggers.AtRisk, row.Machine.MachineId, row.Entry.Path),
                Trigger = AlertTriggers.AtRisk,
                Title = $"{row.RepoName} on {host}: {old[0].Text}",
                Body = string.Join("\n", [.. old.Skip(1).Select(r => r.Text), $"Only on {host}, in {row.Entry.Path}."]),
                Path = "safety",
                Severity = old[0].Level == RiskLevel.High ? AlertSeverity.High : AlertSeverity.Warning,
            };
        }
    }

    private static IEnumerable<AlertCandidate> Offline(AlertInputs inputs, MachineOfflineTriggerOptions options, DateTimeOffset now)
    {
        var limit = TimeSpan.FromMinutes(options.Minutes);
        var ignore = options.IgnoreSet;
        foreach (var machine in inputs.Machines)
        {
            if (ignore.Contains(machine.Machine.Hostname) || ignore.Contains(machine.MachineId) || now - machine.LastSeenAt < limit)
            {
                continue;
            }

            // Straight after a restart every machine looks late: give each the same time over again,
            // unless it was already known to be offline.
            var key = AlertCandidate.MakeKey(AlertTriggers.MachineOffline, machine.MachineId);
            if (now - inputs.HubUpSince < limit && !inputs.OpenKeys.Contains(key))
            {
                continue;
            }

            yield return new AlertCandidate
            {
                Key = key,
                Trigger = AlertTriggers.MachineOffline,
                Title = $"{machine.Machine.Hostname} is offline",
                Body = $"Its agent last reported {Fmt.Ago(machine.LastSeenAt, now)} ({Fmt.Date(machine.LastSeenAt)}).",
                Path = "machines#machine-" + Uri.EscapeDataString(machine.MachineId),
                Severity = AlertSeverity.Warning,
            };
        }
    }

    private static IEnumerable<AlertCandidate> Diverged(AlertInputs inputs) =>
        inputs.Findings
            .Where(v => v.IsActive && v.Finding.RuleId == DivergedRule)
            .Select(v => new AlertCandidate
            {
                Key = AlertCandidate.MakeKey(AlertTriggers.Diverged, v.Finding.Fingerprint),
                Trigger = AlertTriggers.Diverged,
                Title = v.Finding.Title,
                Body = v.Finding.Explanation,
                Path = v.Finding.RepoKey is { } repo ? Fmt.RepoUrl(repo) : "cleanup",
                Severity = AlertSeverity.High,
            });

    /// <summary>My pull requests whose checks are failing. Snoozing or dismissing the cleanup finding for one silences it here too.</summary>
    private static IEnumerable<AlertCandidate> CiFailed(AlertInputs inputs, CiTriggerOptions options)
    {
        var muted = inputs.Findings
            .Where(v => !v.IsActive && v.Finding.RuleId == FailingCiRule)
            .Select(v => v.Finding.Fingerprint)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var pr in inputs.PullRequests.Where(p => p.Authored && p.Ci == CiState.Failure && (options.IncludeDrafts || !p.IsDraft)))
        {
            var number = pr.Number.ToString(CultureInfo.InvariantCulture);
            if (muted.Contains(Finding.MakeFingerprint(FailingCiRule, pr.RepoKey, number)))
            {
                continue;
            }

            yield return new AlertCandidate
            {
                Key = AlertCandidate.MakeKey(AlertTriggers.CiFailed, pr.RepoKey, number),
                Trigger = AlertTriggers.CiFailed,
                Title = $"CI failing: {pr.Repo}#{number}",
                Body = pr.Title + (pr.HeadRef is null ? "" : $" ({pr.HeadRef})"),
                Path = "github",
                ExternalUrl = pr.Url.Length > 0 ? pr.Url : null,
                Severity = AlertSeverity.High,
            };
        }
    }

    private static IEnumerable<AlertCandidate> ActionFailed(AlertInputs inputs) =>
        inputs.FailedActions
            .Where(a => a.Status == AgentActionStatus.Failed && a.Kind != AgentActionKind.Refresh)
            .Select(a => new AlertCandidate
            {
                Key = AlertCandidate.MakeKey(AlertTriggers.ActionFailed, a.Id.ToString(CultureInfo.InvariantCulture)),
                Trigger = AlertTriggers.ActionFailed,
                Title = $"Action failed on {(a.MachineName.Length > 0 ? a.MachineName : a.MachineId)}: {Short(a.CommandLine.Length > 0 ? a.CommandLine : a.Kind.ToString(), 90)}",
                Body = Short(a.Output ?? "The agent gave no reason.", 400),
                Path = "machines",
                Severity = AlertSeverity.Warning,
                IsEvent = true,
            });

    /// <summary>The daily digest: "3 repos at risk, 12 safe to remove, 4.2 GB".</summary>
    public static AlertCandidate Digest(AlertInputs inputs, AlertTriggerOptions triggers, DateTimeOffset now)
    {
        var atRisk = inputs.Safety.Count(r => r.Safety.IsAtRisk);
        var removable = inputs.Safety.Where(r => r.Safety.IsRemovable).ToList();
        var parts = new List<string> { $"{Fmt.Count(atRisk, "repo")} at risk" };
        parts.Add(removable.Count == 0 ? "none safe to remove" : $"{removable.Count} safe to remove, {Fmt.Size(removable.Sum(r => r.FreedBytes))}");

        var extras = new List<string>();
        var offline = inputs.Machines.Count(m => now - m.LastSeenAt >= TimeSpan.FromMinutes(triggers.MachineOffline.Minutes));
        if (offline > 0)
        {
            extras.Add($"{Fmt.Count(offline, "machine")} offline");
        }

        var failing = inputs.PullRequests.Count(p => p.Authored && p.Ci == CiState.Failure);
        if (failing > 0)
        {
            extras.Add($"{Fmt.Count(failing, "pull request")} with failing CI");
        }

        var diverged = inputs.Findings.Count(v => v.IsActive && v.Finding.RuleId == DivergedRule);
        if (diverged > 0)
        {
            extras.Add($"{Fmt.Count(diverged, "branch", "branches")} diverged");
        }

        return new AlertCandidate
        {
            Key = AlertTriggers.Digest,
            Trigger = AlertTriggers.Digest,
            Title = "DevDash daily digest",
            Body = string.Join(", ", parts) + (extras.Count == 0 ? "." : ".\n" + string.Join(", ", extras) + "."),
            Path = "safety",
            Severity = AlertSeverity.Info,
        };
    }

    /// <summary>Several new alerts as one message: their titles, and the Alerts page for the rest.</summary>
    public static AlertCandidate Summary(IReadOnlyList<AlertCandidate> alerts)
    {
        const int listed = 10;
        var lines = alerts.Take(listed).Select(a => "• " + a.Title).ToList();
        if (alerts.Count > listed)
        {
            lines.Add($"and {alerts.Count - listed} more");
        }

        return new AlertCandidate
        {
            Key = AlertTriggers.Summary,
            Trigger = AlertTriggers.Summary,
            Title = $"{alerts.Count} new alerts",
            Body = string.Join("\n", lines),
            Path = "alerts",
            Severity = alerts.Max(a => a.Severity),
        };
    }

    private static string Short(string text, int max)
    {
        var line = text.Trim().ReplaceLineEndings(" ");
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }
}
