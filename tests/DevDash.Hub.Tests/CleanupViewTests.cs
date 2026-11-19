using System.Net;
using System.Net.Http.Json;
using DevDash.Contracts;
using DevDash.Core.Cleanup;
using DevDash.Core.GitHub;
using DevDash.Hub.Data;
using DevDash.Hub.GitHub;
using DevDash.Hub.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Hub.Tests;

/// <summary>What counts as urgent, and how findings are filtered and grouped.</summary>
public class FindingTriageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static Finding Make(string rule, FindingSeverity severity, double? idleDays, string title = "widget: something",
        string? machine = "mac", string? repo = "github.com/acme/widget", string? path = "/dev/widget", string? url = null) => new()
        {
            RuleId = rule,
            Severity = severity,
            Title = title,
            Explanation = "why",
            MachineId = machine,
            MachineName = machine is null ? null : machine + "-host",
            RepoKey = repo,
            Path = path,
            Url = url,
            LastActivityAt = idleDays is { } days ? Now.AddDays(-days) : null,
            Fingerprint = Finding.MakeFingerprint(rule, machine, repo, path, title),
        };

    private static FindingView View(Finding finding) => new(finding, null, null);

    [Theory]
    [InlineData(0, FindingAge.Recent)]
    [InlineData(30, FindingAge.Recent)]
    [InlineData(31, FindingAge.Idle)]
    [InlineData(90, FindingAge.Idle)]
    [InlineData(91, FindingAge.Neglected)]
    [InlineData(400, FindingAge.Neglected)]
    public void Age_follows_the_thresholds(int idleDays, FindingAge expected)
    {
        Assert.Equal(30, FindingTriage.RecentDays);
        Assert.Equal(90, FindingTriage.NeglectedDays);
        Assert.Equal(expected, FindingTriage.AgeOf(Make("unpushed", FindingSeverity.Medium, idleDays), Now));
    }

    [Fact]
    public void Age_is_unknown_without_a_date() =>
        Assert.Equal(FindingAge.Unknown, FindingTriage.AgeOf(Make("non-git-folder", FindingSeverity.Low, null), Now));

    [Theory]
    // Live work that exists in one place only.
    [InlineData("unpushed", FindingSeverity.Medium, 3, true)]
    [InlineData("old-uncommitted", FindingSeverity.Medium, 20, true)]
    [InlineData("diverged-across-machines", FindingSeverity.High, 1, true)]
    // The same things once they have sat: tidy-up, not urgent, however severe.
    [InlineData("unpushed", FindingSeverity.Medium, 45, false)]
    [InlineData("old-uncommitted", FindingSeverity.High, 117, false)]
    [InlineData("old-uncommitted", FindingSeverity.High, 393, false)]
    [InlineData("diverged-across-machines", FindingSeverity.High, 200, false)]
    // Recent but harmless.
    [InlineData("unpushed", FindingSeverity.Low, 3, false)]
    [InlineData("gone-upstream", FindingSeverity.Medium, 3, false)]
    [InlineData("large-build-dirs", FindingSeverity.Medium, 1, false)]
    // Anything recent that a rule calls high.
    [InlineData("remote-archived-or-deleted", FindingSeverity.High, 2, true)]
    // Pull requests are listed from the PR data, not from findings.
    [InlineData("failing-ci-pr", FindingSeverity.High, 1, false)]
    [InlineData("stale-pr", FindingSeverity.Low, 31, false)]
    public void Only_live_at_risk_work_needs_action_today(string rule, FindingSeverity severity, int idleDays, bool expected) =>
        Assert.Equal(expected, FindingTriage.NeedsActionToday(Make(rule, severity, idleDays), Now));

    [Fact]
    public void Without_a_date_only_high_severity_is_urgent()
    {
        Assert.True(FindingTriage.NeedsActionToday(Make("diverged-across-machines", FindingSeverity.High, null), Now));
        Assert.False(FindingTriage.NeedsActionToday(Make("unpushed", FindingSeverity.Medium, null), Now));
    }

    [Fact]
    public void Filters_combine_machine_severity_age_and_text()
    {
        var recent = Make("unpushed", FindingSeverity.Medium, 3, "widget: 2 unpushed commits on feature/login");
        var old = Make("old-uncommitted", FindingSeverity.High, 200, "gadget: uncommitted changes for 200 days", machine: "win", repo: "github.com/acme/gadget", path: @"C:\dev\gadget");
        var pr = Make("stale-pr", FindingSeverity.Low, 40, "acme/widget#7: idle for 40 days", machine: null, path: null, url: "https://github.com/acme/widget/pull/7");
        Finding[] all = [recent, old, pr];

        List<Finding> Where(FindingFilter filter) => all.Where(f => FindingTriage.Matches(filter, f, Now)).ToList();

        Assert.Equal(all, Where(FindingFilter.None));
        Assert.True(FindingFilter.None.IsEmpty);
        Assert.False(new FindingFilter { Text = "x" }.IsEmpty);
        Assert.Equal([recent], Where(new FindingFilter { MachineId = "MAC" }));
        Assert.Equal([recent, old], Where(new FindingFilter { MinSeverity = FindingSeverity.Medium }));
        Assert.Equal([old], Where(new FindingFilter { MinSeverity = FindingSeverity.High }));
        Assert.Equal([recent], Where(new FindingFilter { Age = FindingAge.Recent }));
        Assert.Equal([pr], Where(new FindingFilter { Age = FindingAge.Idle }));
        Assert.Equal([old], Where(new FindingFilter { Age = FindingAge.Neglected }));
        // Text: title, repo key, branch (in the title), path; every word must match, in any case.
        Assert.Equal([recent], Where(new FindingFilter { Text = "FEATURE/login" }));
        Assert.Equal([recent, pr], Where(new FindingFilter { Text = "acme/widget" }));
        Assert.Equal([old], Where(new FindingFilter { Text = @"  dev\gadget  " }));
        Assert.Equal([recent], Where(new FindingFilter { Text = "widget unpushed" }));
        Assert.Empty(Where(new FindingFilter { Text = "widget", MachineId = "win" }));
    }

    [Fact]
    public void A_finding_that_spans_machines_matches_each_of_them()
    {
        var diverged = Make("diverged-across-machines", FindingSeverity.High, 1, machine: null) with
        {
            Commands =
            [
                new FindingCommand { Label = "Push", Command = "git push", Kind = CommandKind.Fix, MachineId = "mac", MachineName = "mac-host" },
                new FindingCommand { Label = "Push", Command = "git push", Kind = CommandKind.Fix, MachineId = "win", MachineName = "win-host" },
            ],
        };

        Assert.True(FindingTriage.Matches(new FindingFilter { MachineId = "win" }, diverged, Now));
        Assert.False(FindingTriage.Matches(new FindingFilter { MachineId = "linux" }, diverged, Now));
    }

    [Fact]
    public void Repo_groups_never_mix_machines_and_put_the_worst_and_newest_first()
    {
        var views = new[]
        {
            Make("gone-upstream", FindingSeverity.Low, 5, "widget: upstream gone for a"),
            Make("unpushed", FindingSeverity.Medium, 50, "widget: 1 unpushed commit on b"),
            Make("unpushed", FindingSeverity.Medium, 2, "widget: 1 unpushed commit on c"),
            Make("gone-upstream", FindingSeverity.Low, 1, "widget: upstream gone for a", machine: "win", path: @"C:\dev\widget"),
            Make("stale-pr", FindingSeverity.Low, 40, "acme/widget#7: idle for 40 days", machine: null, path: null, url: "https://github.com/acme/widget/pull/7"),
            Make("non-git-folder", FindingSeverity.High, null, "Not a git repo: Docs", repo: null, path: "/dev/Docs"),
            Make("unpushed", FindingSeverity.Medium, 9, "scratch: no remote", repo: "local:mac:/dev/scratch", path: "/dev/scratch"),
        }.Select(View).ToList();

        var groups = FindingTriage.ByRepo(views, Now);

        Assert.Equal(["Docs", "acme/widget", "scratch", "acme/widget", "acme/widget"], groups.Select(g => g.Name));
        Assert.Equal(["mac", "mac", "mac", "win", null], groups.Select(g => g.MachineId));
        Assert.All(groups, g => Assert.Single(g.Items.Select(v => v.Finding.MachineId).Distinct()));
        // Inside the group: worst first, then the most recently touched.
        Assert.Equal(["widget: 1 unpushed commit on c", "widget: 1 unpushed commit on b", "widget: upstream gone for a"], groups[1].Items.Select(v => v.Finding.Title));
        Assert.Equal(Now.AddDays(-2), groups[1].LastActivityAt);
        Assert.Equal(FindingSeverity.Medium, groups[1].Worst);
        // acme/widget on two machines and on GitHub is one repo; the folder and the local repo make three.
        Assert.Equal(3, FindingTriage.RepoCount(views));
    }

    [Fact]
    public void A_per_repo_script_holds_one_machine_only()
    {
        var mac = Make("merged-branch", FindingSeverity.Low, 5) with { Commands = [new FindingCommand { Label = "Delete", Command = "git -C '/dev/widget' branch -d 'a'", Kind = CommandKind.Fix }] };
        var win = Make("merged-branch", FindingSeverity.Low, 5, machine: "win", path: @"C:\dev\widget") with { Commands = [new FindingCommand { Label = "Delete", Command = @"git -C 'C:\dev\widget' branch -d 'a'", Kind = CommandKind.Fix }] };

        var groups = FindingTriage.ByRepo([View(mac), View(win)], Now);

        Assert.Equal(2, groups.Count);
        foreach (var group in groups)
        {
            var script = Assert.Single(CleanupScript.Build(group.Name, group.Items.Select(v => v.Finding), Now));
            Assert.Equal(group.MachineId, script.MachineId);
            Assert.Equal(1, script.CommandCount);
        }
    }
}

/// <summary>The Overview and Cleanup pages over a seeded hub.</summary>
public class CleanupPageTests
{
    private const string Snapshots = "/api/v1/snapshots";

    private static async Task<HubFactory> SeededAsync(Func<MachineSnapshot, MachineSnapshot>? change = null, WebApplicationFactory<Program>? host = null, bool actions = false)
    {
        var factory = new HubFactory();
        var services = (host ?? factory).Services;
        var keys = services.GetRequiredService<DevDash.Hub.Auth.ApiKeyService>();
        var agent = (host ?? factory).CreateClient();
        agent.DefaultRequestHeaders.Authorization = new("Bearer", (await keys.IssueAsync("mac")).Plaintext);
        var snapshot = HubFactory.Snapshot(actions: actions);
        (await agent.PostAsync(Snapshots, JsonContent.Create(change?.Invoke(snapshot) ?? snapshot, options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();

        var now = DateTimeOffset.UtcNow;
        await services.GetRequiredService<GitHubStore>().SetAsync(new GitHubSnapshot
        {
            ViewerLogin = "me",
            FetchedAt = now,
            PullRequests =
            [
                new PullRequestInfo { Repo = "acme/widget", Number = 7, Title = "Add login form", Url = "https://github.com/acme/widget/pull/7", Authored = true, Ci = CiState.Failure, UpdatedAt = now.AddDays(-2), CreatedAt = now.AddDays(-9) },
                new PullRequestInfo { Repo = "acme/widget", Number = 2, Title = "Abandoned experiment", Url = "https://github.com/acme/widget/pull/2", Authored = true, Ci = CiState.Failure, UpdatedAt = now.AddDays(-300), CreatedAt = now.AddDays(-400) },
                new PullRequestInfo { Repo = "acme/gadget", Number = 3, Title = "Please review gadget", Url = "https://github.com/acme/gadget/pull/3", ReviewRequested = true, Author = "octocat", UpdatedAt = now.AddDays(-1), CreatedAt = now.AddDays(-2) },
                new PullRequestInfo { Repo = "acme/gadget", Number = 4, Title = "Draft from a person", Url = "https://github.com/acme/gadget/pull/4", ReviewRequested = true, IsDraft = true, Author = "octocat", UpdatedAt = now.AddDays(-80), CreatedAt = now.AddDays(-90) },
                new PullRequestInfo { Repo = "acme/gadget", Number = 5, Title = "Bump everything", Url = "https://github.com/acme/gadget/pull/5", ReviewRequested = true, IsDraft = true, Author = "dependabot[bot]", UpdatedAt = now.AddDays(-45), CreatedAt = now.AddDays(-50) },
                new PullRequestInfo { Repo = "acme/gadget", Number = 6, Title = "Fresh bot draft", Url = "https://github.com/acme/gadget/pull/6", ReviewRequested = true, IsDraft = true, Author = "dependabot[bot]", UpdatedAt = now.AddDays(-3), CreatedAt = now.AddDays(-3) },
            ],
        });
        return factory;
    }

    private static async Task<string> PageAsync(WebApplicationFactory<Program> factory, string url)
    {
        var response = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>The page's main column: the layout around it (nav, search) is not what these tests are about.</summary>
    private static string Section(string html, string from, string to)
    {
        var start = html.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' is not on the page");
        var end = html.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' does not follow '{from}'");
        return html[start..end];
    }

    // ---- overview ----------------------------------------------------------------------------------

    [Fact]
    public async Task Overview_lists_what_needs_action_today_and_sums_up_the_rest()
    {
        using var factory = await SeededAsync();

        var html = await PageAsync(factory, "/");
        var today = Section(html, "<h2>Today</h2>", "<h2>Tidy-up</h2>");
        var tidy = Section(html, "<h2>Tidy-up</h2>", "<h2>Machines</h2>");

        // PRs: failing first, then review requests with ready ones ahead of drafts.
        Assert.Contains("Add login form", today);
        Assert.True(today.IndexOf("Add login form", StringComparison.Ordinal) < today.IndexOf("Please review gadget", StringComparison.Ordinal));
        Assert.True(today.IndexOf("Please review gadget", StringComparison.Ordinal) < today.IndexOf("Fresh bot draft", StringComparison.Ordinal));
        Assert.True(today.IndexOf("Fresh bot draft", StringComparison.Ordinal) < today.IndexOf("Draft from a person", StringComparison.Ordinal));
        // A bot's draft idle for over 30 days is left out and said to be.
        Assert.DoesNotContain("Bump everything", today);
        Assert.Contains("1 bot-authored draft review request idle for more than 30 days is left out", today);
        // A failing PR nobody has touched for 300 days is not urgent.
        Assert.DoesNotContain("Abandoned experiment", today);
        Assert.Contains("1 of my failing PRs has been idle for more than 90 days", today);
        // Local work that has sat for 40 days is not urgent either.
        Assert.DoesNotContain("uncommitted changes", today);
        Assert.DoesNotContain("unpushed", today);
        Assert.DoesNotContain("Reference Docs", today);

        Assert.Contains("findings</a>", tidy);
        Assert.Contains("href=\"cleanup?view=repo\"", tidy);
        Assert.Contains("href=\"cleanup?age=old\"", tidy);
        Assert.Contains("untouched > 90 days", tidy);
        Assert.Contains("Uncommitted changes older than 14 days", tidy);
        Assert.DoesNotContain("older than N days", html);
    }

    [Fact]
    public async Task Overview_puts_recent_unpushed_and_uncommitted_work_under_today_one_row_per_repo()
    {
        // The same repo, but worked on this week: commits not pushed, and edits 20 days old.
        using var factory = await SeededAsync(s => s with
        {
            Entries =
            [
                s.Entries[0] with
                {
                    Git = s.Entries[0].Git! with
                    {
                        NewestTrackedChangeAt = s.ScannedAt.AddDays(-20),
                        Branches = [.. s.Entries[0].Git!.Branches.Select(b => b.Name == "feature/login" ? b with { LastCommitDate = s.ScannedAt.AddDays(-3) } : b)],
                    },
                },
                s.Entries[1],
            ],
        });

        var today = Section(await PageAsync(factory, "/"), "<h2>Today</h2>", "<h2>Tidy-up</h2>");

        Assert.Contains("acme/widget: 2 things at risk", today);
        Assert.Contains("widget: 2 unpushed commits on feature/login", today);
        Assert.Contains("widget: uncommitted changes for 20 days", today);
        Assert.Contains("cleanup?view=repo&age=recent&q=widget&machine=mac", today);
        Assert.DoesNotContain("Reference Docs", today);
    }

    // ---- cleanup -----------------------------------------------------------------------------------

    [Fact]
    public async Task Cleanup_shows_one_line_per_finding_until_a_row_is_opened()
    {
        using var factory = await SeededAsync();

        var html = await PageAsync(factory, "/cleanup");

        // The group holding a high-severity finding starts open, as rows: no explanation, no commands.
        Assert.Contains("acme/widget#7: CI failing", html);
        Assert.Contains("class=\"finding compact", html);
        Assert.DoesNotContain("gh pr checks", html);
        Assert.DoesNotContain("class=\"fpanel\"", html);
        Assert.Contains("Snooze all 30d", html);
        Assert.Contains("Dismiss all", html);
        // Headings carry the real thresholds.
        Assert.Contains("Uncommitted changes older than 14 days", html);
        Assert.Contains("Stale PRs", html);
        Assert.DoesNotContain("older than N days", html);

        var open = await PageAsync(factory, "/cleanup?open=all");
        Assert.Contains("class=\"fpanel\"", open);
        Assert.Contains("gh pr checks 7 --repo 'acme/widget'", open);
        Assert.Contains("open on GitHub", open);
    }

    [Fact]
    public async Task Cleanup_headings_follow_the_configured_thresholds()
    {
        using var seedOnly = new HubFactory();
        using var host = seedOnly.WithWebHostBuilder(b => b.UseSetting("Hub:Cleanup:UncommittedDays", "21"));
        using var _ = await SeededAsync(host: host);

        var html = await PageAsync(host, "/cleanup");

        Assert.Contains("Uncommitted changes older than 21 days", html);
        Assert.DoesNotContain("older than 14 days", html);
    }

    [Fact]
    public async Task Cleanup_by_repo_groups_each_repos_findings_with_a_script_for_its_machine()
    {
        using var factory = await SeededAsync();

        var html = await PageAsync(factory, "/cleanup?view=repo&open=all");

        Assert.Contains("class=\"rule repo-group open\"", html);
        Assert.Contains("<h3>acme/widget</h3>", html);
        Assert.Contains("<h3>Reference Docs</h3>", html);
        // The repo's local findings and its pull requests sit in separate groups under the same name.
        Assert.Equal(2, html.Split("<h3>acme/widget</h3>").Length - 1);
        Assert.Contains("# DevDash cleanup: acme/widget (mac-host) on mac-host, generated ", html);
        Assert.Contains("git -C '/dev/widget' branch -d 'old-fix'", html);
        // The group heading already names the repo, so the rows do not repeat it.
        Assert.Contains("2 unpushed commits on feature/login", html);
        Assert.DoesNotContain("<span class=\"frow-title\">widget: 2 unpushed commits on feature/login</span>", html);
        Assert.Contains("aria-current=\"true\" href=\"http://localhost/cleanup?view=repo\"", html);
    }

    [Theory]
    [InlineData("/cleanup?open=all&q=reference", "Not a git repo: Reference Docs", "acme/widget#7")]
    [InlineData("/cleanup?open=all&q=feature/login", "widget: 2 unpushed commits on feature/login", "Reference Docs")]
    [InlineData("/cleanup?open=all&severity=high", "acme/widget#7: CI failing", "Reference Docs")]
    [InlineData("/cleanup?open=all&age=recent", "acme/widget#7: CI failing", "uncommitted changes for 40 days")]
    [InlineData("/cleanup?open=all&age=old", "Not a git repo: Reference Docs", "acme/widget#7: CI failing")]
    [InlineData("/cleanup?open=all&age=idle", "widget: uncommitted changes for 40 days", "Reference Docs")]
    [InlineData("/cleanup?open=all&machine=mac", "widget: uncommitted changes for 40 days", "acme/widget#7: CI failing")]
    // A filtered result this small opens its groups, so the row shows without ?open=all.
    [InlineData("/cleanup?view=repo&age=old", "Not a git repo: Reference Docs", "feature/login")]
    public async Task Cleanup_filters_come_from_the_query_string(string url, string expected, string absent)
    {
        using var factory = await SeededAsync();

        var html = await PageAsync(factory, url);
        var list = html[html.IndexOf("class=\"rule-index\"", StringComparison.Ordinal)..];

        Assert.Contains(expected, list);
        Assert.DoesNotContain(absent, list);
        Assert.Contains("Clear filters", html);
    }

    [Fact]
    public async Task Cleanup_says_so_when_the_filters_match_nothing()
    {
        using var factory = await SeededAsync();

        var html = await PageAsync(factory, "/cleanup?machine=no-such-machine");

        Assert.Contains("No findings match these filters.", html);
        Assert.DoesNotContain("class=\"rule-index\"", html);
    }

    // ---- bulk actions ------------------------------------------------------------------------------

    [Fact]
    public async Task A_group_is_snoozed_or_dismissed_in_one_go_with_one_notification()
    {
        using var factory = await SeededAsync();
        var findings = factory.Services.GetRequiredService<FindingsService>();
        var notifier = factory.Services.GetRequiredService<DashboardNotifier>();
        var all = await findings.GetAsync();
        var group = all.Where(v => v.Finding.RepoKey == "github.com/acme/widget" && v.Finding.MachineId == "mac").Select(v => v.Finding).ToList();
        Assert.True(group.Count >= 3);
        // One of them already has a decision: it is updated, not duplicated.
        await findings.DismissAsync(group[0]);

        var notified = 0;
        void Count(DashboardChange change) => notified += change == DashboardChange.Findings ? 1 : 0;
        notifier.Changed += Count;
        try
        {
            Assert.Equal(group.Count, await findings.SnoozeManyAsync([.. group, group[1]], TimeSpan.FromDays(30)));
            Assert.Equal(1, notified);

            var after = await findings.GetAsync();
            var touched = after.Where(v => group.Any(f => f.Fingerprint == v.Finding.Fingerprint)).ToList();
            Assert.Equal(group.Count, touched.Count);
            Assert.All(touched, v => Assert.Equal(FindingStatus.Snoozed, v.Status));
            Assert.All(touched, v => Assert.InRange(v.SnoozedUntil!.Value, DateTimeOffset.UtcNow.AddDays(29), DateTimeOffset.UtcNow.AddDays(31)));
            Assert.Equal(all.Count - group.Count, after.Count(v => v.IsActive));

            Assert.Equal(2, await findings.DismissManyAsync(group.Take(2)));
            Assert.Equal(2, notified);
            Assert.Equal(2, (await findings.GetAsync()).Count(v => v.Status == FindingStatus.Dismissed));

            Assert.Equal(0, await findings.DismissManyAsync([]));
            Assert.Equal(2, notified);
        }
        finally
        {
            notifier.Changed -= Count;
        }

        // Snoozed and dismissed findings leave the page, and come back when asked for.
        var html = await PageAsync(factory, "/cleanup?open=all");
        Assert.DoesNotContain("widget: uncommitted changes for 40 days", html);
        var hidden = await PageAsync(factory, "/cleanup?open=all&hidden=1");
        Assert.Contains("widget: uncommitted changes for 40 days", hidden);
        Assert.Contains("Restore", hidden);
    }

    [Fact]
    public async Task Run_buttons_stay_behind_the_opened_row()
    {
        using var factory = await SeededAsync(actions: true);

        Assert.DoesNotContain("class=\"run\"", await PageAsync(factory, "/cleanup?view=repo"));
        Assert.Contains("Run on mac-host", await PageAsync(factory, "/cleanup?view=repo&open=all"));
    }
}
