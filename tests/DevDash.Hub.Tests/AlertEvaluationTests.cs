using DevDash.Contracts;
using DevDash.Core.Cleanup;
using DevDash.Core.GitHub;
using DevDash.Core.Safety;
using DevDash.Hub.Alerts;
using DevDash.Hub.Data;
using DevDash.Hub.Services;

namespace DevDash.Hub.Tests;

/// <summary>The pure half of alerts: what should be alerting, and which of that is new, due, held or cleared.</summary>
public class AlertEvaluationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Sha = "1111111111111111111111111111111111111111";

    // ---- fixtures -------------------------------------------------------------------------------

    internal static DevEntry Repo(string name, DateTimeOffset now, double? unpushedDays = null, double? uncommittedDays = null, double? untrackedDays = null, string? path = null)
    {
        var branches = new List<BranchInfo>
        {
            new() { Name = "main", Sha = Sha, Upstream = "origin/main", IsCurrent = true, OnRemote = true, LastCommitDate = now.AddDays(-30) },
        };
        if (unpushedDays is { } days)
        {
            branches.Add(new BranchInfo
            {
                Name = "feature/x", Sha = "2222222222222222222222222222222222222222", Upstream = "origin/feature/x", Ahead = 2,
                OnRemote = false, UnpushedCount = 2, OldestUnpushedAt = now.AddDays(-days), LastCommitDate = now.AddDays(-days),
            });
        }

        path ??= "/dev/" + name;
        return new DevEntry
        {
            Root = "/dev",
            Name = name,
            Path = path,
            Kind = EntryKind.GitRepo,
            LastModified = now.AddDays(-30),
            Git = new GitState
            {
                Branch = "main",
                HeadSha = Sha,
                Upstream = "origin/main",
                DefaultBranch = "main",
                CommonDir = path + "/.git",
                Unstaged = uncommittedDays is null ? 0 : 2,
                NewestTrackedChangeAt = uncommittedDays is { } u ? now.AddDays(-u) : null,
                Untracked = untrackedDays is null ? 0 : 1,
                NewestUntrackedAt = untrackedDays is { } t ? now.AddDays(-t) : null,
                LastCommit = new CommitInfo { Sha = Sha, Date = now.AddDays(-30), Subject = "work", Author = "me" },
                Remotes = [new RemoteInfo { Name = "origin", Url = $"https://github.com/acme/{name}.git", Normalized = $"github.com/acme/{name}" }],
                Branches = branches,
                Worktrees = [new WorktreeInfo { Path = path, Branch = "main" }],
                Safety = new SafetyFacts { BranchesChecked = true, HeadOnRemote = true },
            },
        };
    }

    internal static MachineSnapshot Snapshot(string machineId, DateTimeOffset now, params DevEntry[] entries) => new()
    {
        Machine = new MachineInfo { MachineId = machineId, Hostname = machineId + "-host", Os = "macOS", AgentVersion = "0.6.0" },
        ScannedAt = now,
        Roots = ["/dev"],
        Entries = entries,
    };

    private static MachineView Machine(string id, DateTimeOffset lastSeen, params DevEntry[] entries) =>
        new(Snapshot(id, Now, entries), lastSeen.AddDays(-10), lastSeen);

    private static IReadOnlyList<SafetyRow> Rows(params MachineView[] machines) =>
        machines.SelectMany(m => RepoSafety.AssessAll(m.Snapshot, Now).Select(c => new SafetyRow(m, c))).ToList();

    private static AlertTriggerOptions Only(Action<AlertTriggerOptions> enable)
    {
        var options = new AlertTriggerOptions
        {
            AtRisk = { Enabled = false },
            MachineOffline = { Enabled = false },
            Diverged = { Enabled = false },
            CiFailed = { Enabled = false },
            ActionFailed = { Enabled = false },
        };
        enable(options);
        return options;
    }

    private static PullRequestInfo Pr(int number, CiState ci, bool authored = true, bool draft = false) => new()
    {
        Repo = "acme/widget", Number = number, Title = $"Change {number}", HeadRef = $"branch-{number}", Ci = ci, Authored = authored, IsDraft = draft,
        Url = $"https://github.com/acme/widget/pull/{number}",
    };

    private static FindingView Finding(string rule, string fingerprint, FindingStatus? status = null, string title = "widget: main diverged across 2 machines") =>
        new(new Finding { RuleId = rule, Severity = FindingSeverity.High, Title = title, Explanation = "main has different unpushed commits on each machine.", RepoKey = "github.com/acme/widget", Fingerprint = fingerprint }, status, null);

    private static AlertCandidate Candidate(string key, bool isEvent = false) =>
        new() { Key = key, Trigger = isEvent ? AlertTriggers.ActionFailed : AlertTriggers.AtRisk, Title = key, Path = "safety", IsEvent = isEvent };

    private static AlertStateEntity State(string key, DateTimeOffset? notified, bool isEvent = false, int attempts = 0, DateTimeOffset? attempted = null, DateTimeOffset? seen = null) =>
        new() { Key = key, Trigger = isEvent ? AlertTriggers.ActionFailed : AlertTriggers.AtRisk, IsEvent = isEvent, FirstSeenAt = Now.AddDays(-1), LastSeenAt = seen ?? Now, LastNotifiedAt = notified, Attempts = attempts, LastAttemptAt = attempted };

    // ---- at risk --------------------------------------------------------------------------------

    [Fact]
    public void Unpushed_commits_alert_once_they_are_older_than_the_threshold()
    {
        var laptop = Machine("laptop", Now, Repo("old", Now, unpushedDays: 4), Repo("fresh", Now, unpushedDays: 1), Repo("clean", Now));
        var inputs = new AlertInputs { Safety = Rows(laptop) };

        var alert = Assert.Single(AlertEvaluator.Evaluate(inputs, Only(t => t.AtRisk.Enabled = true), Now));

        Assert.Equal(AlertTriggers.AtRisk, alert.Trigger);
        Assert.Equal("old on laptop-host: 2 commits on feature/x not on any remote, oldest 4 days", alert.Title);
        Assert.Contains("/dev/old", alert.Body);
        Assert.Equal("safety", alert.Path);
        Assert.Equal(AlertSeverity.High, alert.Severity);

        // The threshold is the setting, and the trigger can be switched off.
        Assert.Equal(2, AlertEvaluator.Evaluate(inputs, Only(t => { t.AtRisk.Enabled = true; t.AtRisk.Days = 1; }), Now).Count);
        Assert.Empty(AlertEvaluator.Evaluate(inputs, Only(t => { t.AtRisk.Enabled = true; t.AtRisk.Days = 10; }), Now));
        Assert.Empty(AlertEvaluator.Evaluate(inputs, Only(_ => { }), Now));
    }

    [Fact]
    public void By_default_only_commits_on_no_remote_alert_and_the_level_widens_it()
    {
        var laptop = Machine("laptop", Now, Repo("dirty", Now, uncommittedDays: 20), Repo("stray", Now, untrackedDays: 20), Repo("both", Now, unpushedDays: 9, uncommittedDays: 20));
        var inputs = new AlertInputs { Safety = Rows(laptop) };

        Assert.Equal(["both"], Titles(Only(t => t.AtRisk.Enabled = true)));
        Assert.Equal(["both", "dirty"], Titles(Only(t => { t.AtRisk.Enabled = true; t.AtRisk.Level = "medium"; })));
        Assert.Equal(["both", "dirty", "stray"], Titles(Only(t => { t.AtRisk.Enabled = true; t.AtRisk.Level = "Low"; })));
        Assert.Equal(["both"], Titles(Only(t => { t.AtRisk.Enabled = true; t.AtRisk.Level = "nonsense"; })));

        // The most serious risk leads; the others follow in the body.
        var both = AlertEvaluator.Evaluate(inputs, Only(t => { t.AtRisk.Enabled = true; t.AtRisk.Level = "Medium"; }), Now).Single(a => a.Title.StartsWith("both"));
        Assert.Contains("not on any remote", both.Title);
        Assert.Contains("uncommitted change", both.Body);
        Assert.Equal(AlertSeverity.Warning, AlertEvaluator.Evaluate(inputs, Only(t => { t.AtRisk.Enabled = true; t.AtRisk.Level = "Medium"; }), Now).Single(a => a.Title.StartsWith("dirty")).Severity);

        List<string> Titles(AlertTriggerOptions options) =>
            AlertEvaluator.Evaluate(inputs, options, Now).Select(a => a.Title.Split(' ')[0]).Order().ToList();
    }

    [Fact]
    public void The_same_work_keeps_the_same_key_and_another_machine_or_folder_gets_its_own()
    {
        var triggers = Only(t => t.AtRisk.Enabled = true);
        string[] Keys(DateTimeOffset now, params MachineView[] machines) =>
            AlertEvaluator.Evaluate(new AlertInputs { Safety = machines.SelectMany(m => RepoSafety.AssessAll(m.Snapshot, now).Select(c => new SafetyRow(m, c))).ToList() }, triggers, now).Select(a => a.Key).ToArray();

        var first = Keys(Now, Machine("laptop", Now, Repo("old", Now, unpushedDays: 4)));
        var later = Keys(Now.AddDays(6), Machine("laptop", Now, Repo("old", Now, unpushedDays: 4))); // the wording ages ("oldest 10 days"); the key does not
        var elsewhere = Keys(Now, Machine("desktop", Now, Repo("old", Now, unpushedDays: 4)), Machine("laptop", Now, Repo("old", Now, unpushedDays: 4, path: "/dev/old-copy")));

        Assert.Equal(first, later);
        Assert.Equal(2, elsewhere.Distinct().Count());
        Assert.DoesNotContain(first[0], elsewhere);
        Assert.True(first[0].Length <= 64);
    }

    // ---- machine offline ------------------------------------------------------------------------

    [Fact]
    public void A_machine_is_offline_after_the_configured_minutes_unless_it_is_left_out()
    {
        var inputs = new AlertInputs
        {
            Machines = [Machine("gone", Now.AddMinutes(-61)), Machine("late", Now.AddMinutes(-59)), Machine("sleepy", Now.AddHours(-9)), Machine("here", Now)],
        };

        var alerts = AlertEvaluator.Evaluate(inputs, Only(t => { t.MachineOffline.Enabled = true; t.MachineOffline.Ignore = "sleepy-host, other"; }), Now);

        var alert = Assert.Single(alerts);
        Assert.Equal("gone-host is offline", alert.Title);
        Assert.Equal("machines#machine-gone", alert.Path);
        Assert.Contains("1h ago", alert.Body);

        // By machine id too, and the threshold is the setting.
        Assert.Empty(AlertEvaluator.Evaluate(inputs, Only(t => { t.MachineOffline.Enabled = true; t.MachineOffline.Ignore = "sleepy;gone"; }), Now));
        Assert.Equal(3, AlertEvaluator.Evaluate(inputs, Only(t => { t.MachineOffline.Enabled = true; t.MachineOffline.Minutes = 30; }), Now).Count);
    }

    [Fact]
    public void A_hub_that_just_started_gives_every_machine_the_time_over_again()
    {
        var triggers = Only(t => t.MachineOffline.Enabled = true);
        var machines = new[] { Machine("gone", Now.AddHours(-5)) };
        var key = AlertCandidate.MakeKey(AlertTriggers.MachineOffline, "gone");

        // The hub was down for hours: its agents could not report, so nobody is called offline yet.
        Assert.Empty(AlertEvaluator.Evaluate(new AlertInputs { Machines = machines, HubUpSince = Now.AddMinutes(-10) }, triggers, Now));

        // An hour of the hub being up with still no report: now it is.
        Assert.Single(AlertEvaluator.Evaluate(new AlertInputs { Machines = machines, HubUpSince = Now.AddMinutes(-61) }, triggers, Now));

        // A machine already known to be offline stays so across the restart: no clear, so no second alert.
        var open = AlertEvaluator.Evaluate(new AlertInputs { Machines = machines, HubUpSince = Now.AddMinutes(-10), OpenKeys = new HashSet<string> { key } }, triggers, Now);
        Assert.Equal(key, Assert.Single(open).Key);
    }

    // ---- diverged, CI, actions ------------------------------------------------------------------

    [Fact]
    public void A_diverged_branch_alerts_unless_its_finding_is_snoozed_or_dismissed()
    {
        var inputs = new AlertInputs
        {
            Findings =
            [
                Finding("diverged-across-machines", "aaa"),
                Finding("diverged-across-machines", "bbb", FindingStatus.Dismissed),
                Finding("diverged-across-machines", "ccc", FindingStatus.Snoozed),
                Finding("old-stash", "ddd"),
            ],
        };

        var alert = Assert.Single(AlertEvaluator.Evaluate(inputs, Only(t => t.Diverged.Enabled = true), Now));

        Assert.Equal("widget: main diverged across 2 machines", alert.Title);
        Assert.Equal("repo?key=github.com%2Facme%2Fwidget", alert.Path);
        Assert.Equal(AlertSeverity.High, alert.Severity);
        Assert.False(alert.IsEvent);
    }

    [Fact]
    public void Failing_ci_on_my_pull_requests_alerts_with_a_link_to_the_pull_request()
    {
        var dismissed = DevDash.Core.Cleanup.Finding.MakeFingerprint("failing-ci-pr", "github.com/acme/widget", "5");
        var inputs = new AlertInputs
        {
            PullRequests = [Pr(1, CiState.Failure), Pr(2, CiState.Success), Pr(3, CiState.Pending), Pr(4, CiState.Failure, authored: false), Pr(5, CiState.Failure), Pr(6, CiState.Failure, draft: true)],
            Findings = [Finding("failing-ci-pr", dismissed, FindingStatus.Dismissed, "acme/widget#5: CI failing")],
        };

        var alert = Assert.Single(AlertEvaluator.Evaluate(inputs, Only(t => t.CiFailed.Enabled = true), Now));

        Assert.Equal("CI failing: acme/widget#1", alert.Title);
        Assert.Equal("Change 1 (branch-1)", alert.Body);
        Assert.Equal("github", alert.Path);
        Assert.Equal("https://github.com/acme/widget/pull/1", alert.ExternalUrl);

        Assert.Equal(["CI failing: acme/widget#1", "CI failing: acme/widget#6"],
            AlertEvaluator.Evaluate(inputs, Only(t => { t.CiFailed.Enabled = true; t.CiFailed.IncludeDrafts = true; }), Now).Select(a => a.Title));
    }

    [Fact]
    public void A_failed_action_is_an_event_and_a_failed_refresh_is_not_worth_an_alert()
    {
        var inputs = new AlertInputs
        {
            FailedActions =
            [
                new AgentActionEntity { Id = 7, MachineId = "mac", MachineName = "mac-host", Kind = AgentActionKind.Push, CommandLine = "git push origin main", Status = AgentActionStatus.Failed, Output = "rejected\n(non-fast-forward)" },
                new AgentActionEntity { Id = 8, MachineId = "mac", MachineName = "mac-host", Kind = AgentActionKind.Refresh, Status = AgentActionStatus.Failed, Output = "busy" },
                new AgentActionEntity { Id = 9, MachineId = "mac", MachineName = "mac-host", Kind = AgentActionKind.Fetch, Status = AgentActionStatus.Succeeded },
            ],
        };

        var alert = Assert.Single(AlertEvaluator.Evaluate(inputs, Only(t => t.ActionFailed.Enabled = true), Now));

        Assert.Equal("Action failed on mac-host: git push origin main", alert.Title);
        Assert.Equal("rejected (non-fast-forward)", alert.Body);
        Assert.Equal("machines", alert.Path);
        Assert.True(alert.IsEvent);
    }

    [Fact]
    public void The_digest_counts_what_is_at_risk_and_what_is_safe_to_remove()
    {
        var clean = Repo("clean", Now) with { Size = new SizeInfo { TotalBytes = 3L * 1024 * 1024 * 1024, MeasuredAt = Now } };
        var tidy = Repo("tidy", Now) with { Size = new SizeInfo { TotalBytes = 1288490188, MeasuredAt = Now } };
        var laptop = Machine("laptop", Now, Repo("a", Now, unpushedDays: 4), Repo("b", Now, uncommittedDays: 1), Repo("c", Now, untrackedDays: 1), clean, tidy);
        var inputs = new AlertInputs
        {
            Safety = Rows(laptop),
            Machines = [laptop, Machine("gone", Now.AddDays(-2))],
            PullRequests = [Pr(1, CiState.Failure), Pr(2, CiState.Failure, authored: false)],
        };

        var digest = AlertEvaluator.Digest(inputs, new AlertTriggerOptions(), Now);

        Assert.Equal("DevDash daily digest", digest.Title);
        Assert.Equal("3 repos at risk, 2 safe to remove, 4.2 GB.\n1 machine offline, 1 pull request with failing CI.", digest.Body);
        Assert.Equal(AlertTriggers.Digest, digest.Trigger);

        Assert.Equal("0 repos at risk, none safe to remove.", AlertEvaluator.Digest(new AlertInputs(), new AlertTriggerOptions(), Now).Body);
    }

    [Fact]
    public void A_summary_names_the_alerts_it_stands_for()
    {
        var alerts = Enumerable.Range(1, 12).Select(i => Candidate($"alert {i}") with { Severity = i == 3 ? AlertSeverity.High : AlertSeverity.Warning }).ToList();

        var summary = AlertEvaluator.Summary(alerts);

        Assert.Equal("12 new alerts", summary.Title);
        Assert.Contains("• alert 1\n", summary.Body);
        Assert.EndsWith("and 2 more", summary.Body);
        Assert.Equal("alerts", summary.Path);
        Assert.Equal(AlertSeverity.High, summary.Severity);
    }

    // ---- dedupe ---------------------------------------------------------------------------------

    [Fact]
    public void A_new_condition_is_due_and_an_open_one_is_not()
    {
        var plan = AlertPlanner.Plan([Candidate("new"), Candidate("open")], [State("open", Now.AddHours(-30))], new AlertOptions(), Now, quiet: false);

        Assert.Equal(["new"], plan.Fresh.Select(c => c.Key));
        Assert.Equal(["new"], plan.Due.Select(c => c.Key));
        Assert.Empty(plan.Held);
        Assert.Empty(plan.Cleared);
    }

    [Fact]
    public void A_condition_that_has_gone_is_cleared_so_that_its_return_is_new()
    {
        var plan = AlertPlanner.Plan([], [State("was-open", Now.AddHours(-3))], new AlertOptions(), Now, quiet: false);
        Assert.Equal(["was-open"], plan.Cleared);

        // Its row is gone by the next look, so the same key is fresh again.
        var back = AlertPlanner.Plan([Candidate("was-open")], [], new AlertOptions(), Now.AddHours(1), quiet: false);
        Assert.Equal(["was-open"], back.Due.Select(c => c.Key));
    }

    [Fact]
    public void A_renotify_interval_says_it_again_while_it_lasts_but_never_for_an_event()
    {
        var options = new AlertOptions { RenotifyHours = 24 };
        AlertStateEntity[] open = [State("recent", Now.AddHours(-23)), State("old", Now.AddHours(-24)), State("event", Now.AddDays(-3), isEvent: true)];
        AlertCandidate[] active = [Candidate("recent"), Candidate("old"), Candidate("event", isEvent: true)];

        Assert.Equal(["old"], AlertPlanner.Plan(active, open, options, Now, quiet: false).Due.Select(c => c.Key));
        Assert.Empty(AlertPlanner.Plan(active, open, new AlertOptions(), Now, quiet: false).Due); // 0: once
        Assert.Empty(AlertPlanner.Plan(active, open, options, Now, quiet: false).Fresh);
    }

    [Fact]
    public void An_event_is_kept_for_a_week_after_it_was_last_seen_and_only_then_forgotten()
    {
        AlertStateEntity[] open = [State("recent", Now.AddDays(-2), isEvent: true, seen: Now.AddDays(-2)), State("ancient", Now.AddDays(-8), isEvent: true, seen: Now.AddDays(-8))];

        var plan = AlertPlanner.Plan([], open, new AlertOptions(), Now, quiet: false);

        Assert.Equal(["ancient"], plan.Cleared);
    }

    [Fact]
    public void Quiet_hours_hold_what_is_due_and_the_end_of_them_releases_it()
    {
        AlertStateEntity[] open = [State("waiting", null), State("sent", Now.AddHours(-1))];
        AlertCandidate[] active = [Candidate("new"), Candidate("waiting"), Candidate("sent")];

        var night = AlertPlanner.Plan(active, open, new AlertOptions(), Now, quiet: true);
        Assert.Empty(night.Due);
        Assert.Equal(["new", "waiting"], night.Held.Select(c => c.Key));
        Assert.Equal(["new"], night.Fresh.Select(c => c.Key)); // its row is opened all the same, so it is remembered

        var morning = AlertPlanner.Plan(active, [.. open, State("new", null)], new AlertOptions(), Now.AddHours(8), quiet: false);
        Assert.Equal(["new", "waiting"], morning.Due.Select(c => c.Key));
        Assert.Empty(morning.Fresh);
    }

    [Fact]
    public void An_alert_that_reached_no_channel_waits_longer_each_time_and_is_given_up_on()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), AlertPlanner.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(4), AlertPlanner.Backoff(3));
        Assert.Equal(TimeSpan.FromMinutes(60), AlertPlanner.Backoff(9));

        bool Due(int attempts, double minutesSince) =>
            AlertPlanner.Plan([Candidate("a")], [State("a", null, attempts: attempts, attempted: Now.AddMinutes(-minutesSince))], new AlertOptions(), Now, quiet: false).Due.Count == 1;

        Assert.False(Due(1, 0.5));
        Assert.True(Due(1, 1));
        Assert.False(Due(3, 3));
        Assert.True(Due(3, 4));
        Assert.False(Due(AlertPlanner.MaxAttempts, 100000));

        // A renotify that fails is retried on the same terms, not on every look.
        var renotify = new AlertOptions { RenotifyHours = 1 };
        Assert.Empty(AlertPlanner.Plan([Candidate("a")], [State("a", Now.AddHours(-2), attempts: 2, attempted: Now.AddMinutes(-1))], renotify, Now, quiet: false).Due);
        Assert.Single(AlertPlanner.Plan([Candidate("a")], [State("a", Now.AddHours(-2), attempts: 2, attempted: Now.AddMinutes(-2))], renotify, Now, quiet: false).Due);
    }

    [Fact]
    public void The_digests_own_row_is_never_cleared()
    {
        var digest = new AlertStateEntity { Key = AlertTriggers.Digest, Trigger = AlertTriggers.Digest, LastNotifiedAt = Now.AddDays(-1), LastSeenAt = Now.AddDays(-1) };

        Assert.Empty(AlertPlanner.Plan([], [digest], new AlertOptions(), Now, quiet: false).Cleared);
    }

    [Fact]
    public void The_digest_is_due_once_a_day_from_its_time_of_day()
    {
        var zone = TimeZoneInfo.Utc;
        var options = new DigestTriggerOptions { Enabled = true, Time = "08:00" };
        var morning = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
        AlertStateEntity Sent(DateTimeOffset at) => new() { Key = AlertTriggers.Digest, Trigger = AlertTriggers.Digest, LastNotifiedAt = at };

        Assert.False(AlertPlanner.DigestDue(options, null, morning.AddMinutes(-1), zone));
        Assert.True(AlertPlanner.DigestDue(options, null, morning, zone));
        Assert.True(AlertPlanner.DigestDue(options, Sent(morning.AddDays(-1)), morning.AddMinutes(3), zone));
        Assert.False(AlertPlanner.DigestDue(options, Sent(morning), morning.AddHours(9), zone));
        Assert.True(AlertPlanner.DigestDue(options, Sent(morning), morning.AddDays(1), zone));

        Assert.False(AlertPlanner.DigestDue(new DigestTriggerOptions { Enabled = false, Time = "08:00" }, null, morning, zone));
        Assert.False(AlertPlanner.DigestDue(new DigestTriggerOptions { Enabled = true, Time = "soon" }, null, morning, zone));

        // The day is the configured zone's day: 08:00 in a zone five hours behind UTC is 13:00 UTC.
        var behind = TimeZoneInfo.CreateCustomTimeZone("test-5", TimeSpan.FromHours(-5), "test-5", "test-5");
        Assert.False(AlertPlanner.DigestDue(options, null, morning.AddHours(4), behind));
        Assert.True(AlertPlanner.DigestDue(options, null, morning.AddHours(5), behind));
    }

    // ---- settings -------------------------------------------------------------------------------

    [Theory]
    [InlineData("22:00", "07:00", "23:30", true)]
    [InlineData("22:00", "07:00", "06:59", true)]
    [InlineData("22:00", "07:00", "07:00", false)]
    [InlineData("22:00", "07:00", "12:00", false)]
    [InlineData("13:00", "14:30", "13:00", true)]
    [InlineData("13:00", "14:30", "14:30", false)]
    [InlineData("9", "17", "10:00", true)]
    [InlineData("08:00", "08:00", "08:00", false)]
    [InlineData("late", "07:00", "23:00", false)]
    public void Quiet_hours_may_run_past_midnight(string start, string end, string at, bool quiet)
    {
        var hours = new QuietHoursOptions { Enabled = true, Start = start, End = end };

        Assert.Equal(quiet, hours.IsQuiet(TimeOnly.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.False(new QuietHoursOptions { Enabled = false, Start = start, End = end }.IsQuiet(TimeOnly.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("http://ha.example.test:8123/api/webhook/abc-123", "http://ha.example.test:8123", "abc-123")]
    [InlineData("http://ha.example.test:8123/api/webhook/abc-123/", "http://ha.example.test:8123", "abc-123")]
    [InlineData("https://ha.example.test/base/api/webhook/abc?x=1", "https://ha.example.test/base", "abc")]
    [InlineData("http://ha.example.test:8123/", "http://ha.example.test:8123", null)]
    [InlineData("", "", null)]
    public void A_home_assistant_webhook_address_is_split_into_where_and_which(string typed, string url, string? id)
    {
        Assert.Equal((url, id), HomeAssistantChannelOptions.Split(typed));

        var options = new AlertOptions { Channels = { HomeAssistant = { Url = typed } } }.Normalize();
        Assert.Equal(url, options.Channels.HomeAssistant.Url);
        Assert.Equal(id ?? "", options.Channels.HomeAssistant.WebhookId);
    }

    [Fact]
    public void Settings_are_put_in_range_and_a_bad_time_zone_falls_back_to_the_hubs_own()
    {
        var options = new AlertOptions
        {
            EvaluateIntervalMinutes = 0, RenotifyHours = -4, MaxPerEvaluation = 0, TimeZone = " Mars/Olympus ",
            Triggers = { AtRisk = { Days = -1, Level = "medium" }, MachineOffline = { Minutes = 0 } },
            Channels = { Ntfy = { Server = "https://ntfy.example.test/", Topic = " alerts " } },
        }.Normalize();

        Assert.Equal((1, 0, 1), (options.EvaluateIntervalMinutes, options.RenotifyHours, options.MaxPerEvaluation));
        Assert.Equal((0, "Medium", 1), (options.Triggers.AtRisk.Days, options.Triggers.AtRisk.Level, options.Triggers.MachineOffline.Minutes));
        Assert.Equal(("https://ntfy.example.test", "alerts"), (options.Channels.Ntfy.Server, options.Channels.Ntfy.Topic));
        Assert.False(options.ZoneIsKnown);
        Assert.Equal(TimeZoneInfo.Local, options.Zone());
        Assert.True(new AlertOptions { TimeZone = "UTC" }.ZoneIsKnown);
    }

    [Fact]
    public void Links_are_built_on_the_configured_public_address_and_left_out_without_one()
    {
        Assert.Equal("https://hub.example.test/safety", AlertService.Link("https://hub.example.test", "safety"));
        Assert.Equal("https://hub.example.test/dash/machines#machine-a", AlertService.Link("https://hub.example.test/dash/", "machines#machine-a"));
        Assert.Equal("https://hub.example.test/repo?key=github.com%2Facme%2Fwidget", AlertService.Link("https://hub.example.test/", "repo?key=github.com%2Facme%2Fwidget"));
        Assert.Null(AlertService.Link("", "safety"));
        Assert.Null(AlertService.Link("hub.example.test", "safety"));
        Assert.Null(AlertService.Link("ftp://hub.example.test", "safety"));
    }
}
