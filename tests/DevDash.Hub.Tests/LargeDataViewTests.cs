using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DevDash.Contracts;
using DevDash.Core.GitHub;
using DevDash.Hub.GitHub;
using DevDash.Hub.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Hub.Tests;

/// <summary>The helpers and page behaviour that keep the hub readable with a lot of data.</summary>
public class LargeDataViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static PullRequestInfo Pr(string repo, int number, string title = "A change", string? author = "me", bool draft = false, int ageDays = 1,
        CiState ci = CiState.None, string? review = null, string? mergeable = "MERGEABLE", bool authored = true, bool reviewRequested = false, DateTimeOffset? now = null) => new()
    {
        Repo = repo, Number = number, Title = title, Url = $"https://github.com/{repo}/pull/{number}", Author = author, IsDraft = draft,
        UpdatedAt = (now ?? Now).AddDays(-ageDays), CreatedAt = (now ?? Now).AddDays(-ageDays - 1), Ci = ci, ReviewDecision = review, Mergeable = mergeable,
        Authored = authored, ReviewRequested = reviewRequested,
    };

    [Theory]
    [InlineData("dependabot[bot]", true)]
    [InlineData("some-new-app[bot]", true)]
    [InlineData("copilot-swe-agent", true)]
    [InlineData("Copilot", true)]
    [InlineData("app/copilot-swe-agent", true)]
    [InlineData("renovate", true)]
    [InlineData("octocat", false)]
    [InlineData("robot-fan", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Bots_are_logins_ending_in_bot_or_known_agents(string? login, bool expected) =>
        Assert.Equal(expected, BotAuthors.IsBot(login));

    [Fact]
    public void Only_old_drafts_from_bots_are_hidden_from_review_requests()
    {
        Assert.True(PrTriage.IsStaleBotDraft(Pr("a/b", 1, author: "copilot-swe-agent", draft: true, ageDays: 31), Now));
        Assert.False(PrTriage.IsStaleBotDraft(Pr("a/b", 2, author: "copilot-swe-agent", draft: true, ageDays: 29), Now));   // recent
        Assert.False(PrTriage.IsStaleBotDraft(Pr("a/b", 3, author: "copilot-swe-agent", draft: false, ageDays: 200), Now)); // ready for review
        Assert.False(PrTriage.IsStaleBotDraft(Pr("a/b", 4, author: "octocat", draft: true, ageDays: 200), Now));            // a person
    }

    [Fact]
    public void Each_pull_request_lands_in_one_bucket_and_needs_action_for_one_reason()
    {
        var snapshot = new GitHubSnapshot
        {
            PullRequests =
            [
                Pr("a/b", 1, author: "copilot-swe-agent", draft: true, ageDays: 300, reviewRequested: true),
                Pr("a/b", 2, author: "octocat", authored: false, reviewRequested: true),
                Pr("a/b", 3, ci: CiState.Failure, review: "APPROVED"),
                Pr("a/b", 4, review: "CHANGES_REQUESTED", mergeable: "CONFLICTING"),
                Pr("a/b", 5, mergeable: "CONFLICTING"),
                Pr("a/b", 6, ci: CiState.Success, review: "APPROVED"),
                Pr("a/b", 7, ci: CiState.Success, review: "APPROVED", draft: true),
                Pr("a/b", 8, ci: CiState.Pending, review: "APPROVED"),
                Pr("a/b", 9, ci: CiState.Success),
                Pr("a/b", 10, authored: false) with { Assigned = true },
            ],
        };

        var buckets = PrTriage.Split(snapshot, Now);

        Assert.Equal([2], buckets.Review.Select(p => p.Number));
        Assert.Equal([1], buckets.StaleBotDrafts.Select(p => p.Number));
        Assert.Equal([3, 4, 5, 6, 7, 8, 9], buckets.Mine.Select(p => p.Number));
        Assert.Equal([10], buckets.Assigned.Select(p => p.Number));
        Assert.Equal([3], buckets.MineNeeding(PrAttention.CiFailing).Select(p => p.Number));
        Assert.Equal([4], buckets.MineNeeding(PrAttention.ChangesRequested).Select(p => p.Number));
        Assert.Equal([5], buckets.MineNeeding(PrAttention.Conflicts).Select(p => p.Number));
        Assert.Equal([6], buckets.MineNeeding(PrAttention.ReadyToMerge).Select(p => p.Number));
        Assert.Equal(4, buckets.MineNeedingAction);
    }

    [Fact]
    public void Repos_with_three_or_more_pull_requests_collapse_into_a_summarised_group()
    {
        var items = new List<PullRequestInfo>
        {
            Pr("acme/solo", 1),
            Pr("org/cue", 1, ci: CiState.Success),
            Pr("org/cue", 2, ci: CiState.Success, review: "APPROVED"),
            Pr("org/cue", 3, ci: CiState.Success, review: "APPROVED", ageDays: 22),
            Pr("acme/pair", 1),
            Pr("acme/pair", 2),
            Pr("org/mixed", 1, ci: CiState.Failure),
            Pr("org/mixed", 2, ci: CiState.Pending, draft: true),
            Pr("org/mixed", 3, ci: CiState.Success, mergeable: "CONFLICTING"),
            Pr("org/mixed", 4, review: "CHANGES_REQUESTED"),
        };

        var (groups, singles) = PrGrouping.Group(items);

        Assert.Equal(["org/mixed", "org/cue"], groups.Select(g => g.Repo));
        Assert.Equal(["acme/solo", "acme/pair", "acme/pair"], singles.Select(p => p.Repo));

        var cue = groups[1];
        Assert.Equal("cue", cue.Name);
        Assert.Equal("org", cue.Owner);
        Assert.False(cue.HasTrouble);
        Assert.Equal("3 open PRs · all passing · 2 approved", string.Join(" · ", cue.Summary().Select(p => p.Text)));
        Assert.Equal(Now.AddDays(-1), cue.UpdatedAt);

        Assert.True(groups[0].HasTrouble);
        Assert.Equal("4 open PRs · 1 failing · 1 running · 1 passing · 1 conflict · 1 changes requested · 1 draft", string.Join(" · ", groups[0].Summary().Select(p => p.Text)));
        Assert.Equal("3 open PRs · no CI", string.Join(" · ", new PrGroup("a/b", "github.com/a/b", [Pr("a/b", 1), Pr("a/b", 2), Pr("a/b", 3)]).Summary().Select(p => p.Text)));
    }

    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(850, "850 ms")]
    [InlineData(1000, "1 s")]
    [InlineData(17_400, "17 s")]
    [InlineData(22_538, "23 s")]
    [InlineData(65_000, "1 m 5 s")]
    [InlineData(120_000, "2 m")]
    [InlineData(3_600_000, "1 h")]
    [InlineData(3_780_000, "1 h 3 m")]
    public void Durations_are_written_for_people(long ms, string expected) => Assert.Equal(expected, Fmt.Duration(ms));

    [Fact]
    public void Paths_under_the_machines_home_are_shortened()
    {
        Assert.Equal("/Users/mike", Fmt.HomeOf(["/Users/mike/Developer", "/Users/mike/work/code"]));
        Assert.Equal("/home/dev", Fmt.HomeOf(["/home/dev/src"]));
        Assert.Equal(@"C:\Users\mike", Fmt.HomeOf([@"C:\Users\mike\source"]));
        Assert.Null(Fmt.HomeOf(["/Users/mike/Developer", "/Users/other/Developer"]));
        Assert.Null(Fmt.HomeOf(["/srv/code"]));
        Assert.Null(Fmt.HomeOf([]));

        Assert.Equal("~/Developer/BCLP", Fmt.Tilde("/Users/mike/Developer/BCLP", "/Users/mike"));
        Assert.Equal("git -C '~/Developer/BCLP' push -- 'origin'", Fmt.Tilde("git -C '/Users/mike/Developer/BCLP' push -- 'origin'", "/Users/mike"));
        Assert.Equal("/Users/mikey/x", Fmt.Tilde("/Users/mikey/x", "/Users/mike"));
        Assert.Equal("/private/tmp/x", Fmt.Tilde("/private/tmp/x", "/Users/mike"));
        Assert.Equal("/Users/mike/x", Fmt.Tilde("/Users/mike/x", null));
        Assert.Equal("1 part", Fmt.Count(1, "part"));
        Assert.Equal("2 entries", Fmt.Count(2, "entry", "entries"));
    }

    private static RepoRow Row(string key, string name, string? owner = null, EntryKind kind = EntryKind.GitRepo, int? activityDaysAgo = null, GitState? git = null, string? path = null)
    {
        var entry = new DevEntry { Root = "/Users/mike/Developer", Name = name, Path = path ?? $"/Users/mike/Developer/{name}", Kind = kind, Git = git };
        var machine = new MachineView(new MachineSnapshot { Machine = new MachineInfo { MachineId = "mac", Hostname = "Mac" }, Roots = ["/Users/mike/Developer"], Entries = [entry] }, Now, Now);
        return new RepoRow
        {
            Key = key, Name = name, Owner = owner, Kind = kind, Clones = [new CloneView(machine, entry, null)],
            LastActivity = activityDaysAgo is { } days ? Now.AddDays(-days) : null,
        };
    }

    [Fact]
    public void Local_rows_sort_by_activity_name_or_state()
    {
        var rows = new[]
        {
            Row("k/clean", "beta", activityDaysAgo: 5, git: new GitState()),
            Row("k/dirty", "Alpha", activityDaysAgo: 30, git: new GitState { Unstaged = 2 }),
            Row("k/ahead", "delta", activityDaysAgo: 1, git: new GitState { Ahead = 1 }),
            Row("local:mac:/x", "notes", kind: EntryKind.NonGitFolder),
        };

        Assert.Equal(LocalSort.Activity, LocalView.ParseSort(null));
        Assert.Equal(LocalSort.Activity, LocalView.ParseSort("nonsense"));
        Assert.Equal(LocalSort.Activity, LocalView.ParseSort("7"));
        Assert.Equal(LocalSort.State, LocalView.ParseSort("STATE"));
        Assert.True(LocalView.DefaultDescending(LocalSort.Activity));
        Assert.False(LocalView.DefaultDescending(LocalSort.Name));

        Assert.Equal(["delta", "beta", "Alpha", "notes"], LocalView.Sort(rows, LocalSort.Activity, descending: true).Select(r => r.Name));
        Assert.Equal(["notes", "Alpha", "beta", "delta"], LocalView.Sort(rows, LocalSort.Activity, descending: false).Select(r => r.Name));
        Assert.Equal(["Alpha", "beta", "delta", "notes"], LocalView.Sort(rows, LocalSort.Name, descending: false).Select(r => r.Name));
        Assert.Equal(["Alpha", "delta", "beta", "notes"], LocalView.Sort(rows, LocalSort.State, descending: true).Select(r => r.Name));
    }

    [Fact]
    public void Rows_that_share_a_name_get_something_to_tell_them_apart()
    {
        var rows = new[]
        {
            Row("github.com/acme/core", "core", "acme"),
            Row("local:mac:/Users/mike/Developer/old/core", "core", path: "/Users/mike/Developer/old/core"),
            Row("github.com/acme/tool", "tool", "acme"),
            Row("github.com/other/tool", "tool", "other"),
            Row("github.com/org/planner", "planner", "org", path: "/Users/mike/Developer/simple-team-planner"),
            Row("github.com/org/simple-team-planner", "planner", "org", path: "/Users/mike/Developer/planner"),
            Row("github.com/acme/unique", "unique", "acme"),
        };

        var hints = LocalView.Disambiguators(rows);

        Assert.Equal("acme · ~/Developer/core", hints["github.com/acme/core"]);
        Assert.Equal("~/Developer/old/core", hints["local:mac:/Users/mike/Developer/old/core"]);
        Assert.Equal("acme", hints["github.com/acme/tool"]);
        Assert.Equal("other", hints["github.com/other/tool"]);
        Assert.Equal("org · ~/Developer/simple-team-planner", hints["github.com/org/planner"]);
        Assert.Equal("org · ~/Developer/planner", hints["github.com/org/simple-team-planner"]);
        Assert.False(hints.ContainsKey("github.com/acme/unique"));

        Assert.Equal([("acme", 3), ("org", 2), ("other", 1), (LocalView.NoOwner, 1)], LocalView.Owners(rows));
    }

    [Fact]
    public void Search_finds_repos_pull_requests_and_machines()
    {
        var rows = new[]
        {
            Row("github.com/acme/widget", "widget", "acme", git: new GitState { Branch = "feature/login" }),
            Row("github.com/acme/gadget", "gadget", "acme", git: new GitState { Branch = "main" }),
        };
        var prs = new[] { Pr("acme/widget", 7, "Add login form"), Pr("acme/gadget", 31, "Widget support") };
        var machines = new[] { rows[0].Clones[0].Machine };

        IReadOnlyList<SearchHit> Find(string query) => GlobalSearch.Search(query, rows, prs, machines);

        Assert.Empty(Find("   "));
        Assert.Empty(Find("zzz"));

        var widget = Find("widget");
        Assert.Equal((SearchHitKind.Repo, "widget", "repo?key=github.com%2Facme%2Fwidget", false), (widget[0].Kind, widget[0].Title, widget[0].Url, widget[0].External));
        Assert.Contains(widget, h => h.Kind == SearchHitKind.PullRequest && h.Title == "Widget support");
        Assert.Contains(widget, h => h.Kind == SearchHitKind.PullRequest && h.Title == "Add login form"); // by repo

        Assert.Equal(["widget", "Add login form"], Find("LOGIN").Select(h => h.Title).Order(StringComparer.Ordinal).Reverse()); // branch, and title
        Assert.Equal(["Widget support"], Find("#31").Select(h => h.Title));
        Assert.Equal(["Add login form"], Find("acme form").Select(h => h.Title)); // every word must match
        var pr = Find("#7").Single();
        Assert.Equal(("https://github.com/acme/widget/pull/7", true, "acme/widget#7"), (pr.Url, pr.External, pr.Detail));

        var mac = Find("mac").Single();
        Assert.Equal((SearchHitKind.Machine, "Mac", "machines#machine-mac"), (mac.Kind, mac.Title, mac.Url));
        Assert.Single(GlobalSearch.Search("acme", rows, prs, machines, limit: 1));
    }

    private static async Task<HubFactory> SeededAsync(params string[] warnings)
    {
        var factory = new HubFactory();
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync());
        (await agent.PostAsync("/api/v1/snapshots", JsonContent.Create(HubFactory.Snapshot(), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();

        var now = DateTimeOffset.UtcNow;
        var prs = new List<PullRequestInfo>
        {
            Pr("acme/widget", 7, "Add login form", ci: CiState.Failure, now: now),
            Pr("acme/widget", 8, "Ready to ship", ci: CiState.Success, review: "APPROVED", now: now),
            Pr("acme/gadget", 3, "Please review gadget", author: "octocat", authored: false, reviewRequested: true, now: now),
            Pr("acme/gadget", 4, "Agent draft from the spring", author: "copilot-swe-agent", draft: true, ageDays: 200, authored: false, reviewRequested: true, now: now),
        };
        prs.AddRange(Enumerable.Range(1, 5).Select(i => Pr("acme/stack", i, $"Stack part {i}", ci: CiState.Success, now: now)));
        await factory.Services.GetRequiredService<GitHubStore>().SetAsync(new GitHubSnapshot
        {
            ViewerLogin = "me", FetchedAt = now, Organizations = ["acme"], PullRequests = prs, Warnings = warnings,
        });
        return factory;
    }

    private static async Task<string> GetAsync(HubFactory factory, string url)
    {
        var response = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Github_opens_on_what_needs_action_and_keeps_the_rest_under_all()
    {
        using var factory = await SeededAsync();

        var needs = await GetAsync(factory, "/github");
        Assert.Contains("Please review gadget", needs);
        Assert.Contains("Mine, CI failing", needs);
        Assert.Contains("Add login form", needs);
        Assert.Contains("Mine, approved and ready to merge", needs);
        Assert.Contains("Ready to ship", needs);
        Assert.DoesNotContain("Stack part 1", needs);
        Assert.DoesNotContain("Mine, merge conflicts", needs);
        Assert.Contains("href=\"http://localhost/github?tab=all\"", needs);

        var all = await GetAsync(factory, "/github?tab=all");
        Assert.Contains("Stack part 1", all);
        Assert.Contains("5 open PRs", all);
        Assert.Contains("all passing", all);
        Assert.Contains("<details class=\"pr-group", all);
        Assert.Contains("Add login form", all);

        // An unknown tab is the default one.
        Assert.Contains("Mine, CI failing", await GetAsync(factory, "/github?tab=nope"));
    }

    [Fact]
    public async Task Old_bot_drafts_are_hidden_from_review_requests_until_asked_for()
    {
        using var factory = await SeededAsync();

        var hidden = await GetAsync(factory, "/github");
        Assert.DoesNotContain("Agent draft from the spring", hidden);
        Assert.Contains("Hide bot drafts older than 30 days", hidden);
        Assert.Contains("1 hidden", hidden);
        Assert.Contains("href=\"http://localhost/github?bots=show\"", hidden);

        var shown = await GetAsync(factory, "/github?bots=show");
        Assert.Contains("Agent draft from the spring", shown);
        Assert.DoesNotContain("1 hidden", shown);
        Assert.Contains("href=\"http://localhost/github?tab=all&bots=show\"", shown); // the choice follows you between tabs
    }

    [Fact]
    public async Task The_poll_warning_counts_its_parts_properly_and_shows_the_first_one()
    {
        using (var one = await SeededAsync("acme/secret: could not be checked (FORBIDDEN)."))
        {
            var html = await GetAsync(one, "/github");
            Assert.Contains("1 part of the last poll could not be read", html);
            Assert.Contains("acme/secret: could not be checked (FORBIDDEN).", html);
            Assert.DoesNotContain("1 parts", html);
        }

        using var two = await SeededAsync("first warning", "second warning");
        var both = await GetAsync(two, "/github");
        Assert.Contains("2 parts of the last poll could not be read", both);
        Assert.Contains("+1 more", both);
        Assert.Contains("second warning", both);
    }

    [Fact]
    public async Task The_navigation_counts_review_requests_and_high_severity_findings()
    {
        using var factory = await SeededAsync();
        var counts = factory.Services.GetRequiredService<NavCounts>();
        var findings = await factory.Services.GetRequiredService<FindingsService>().GetAsync();
        var high = findings.Count(v => v.IsActive && v.Finding.Severity == DevDash.Core.Cleanup.FindingSeverity.High);

        Assert.Equal(1, counts.GitHub()); // the bot's old draft does not count
        Assert.Equal(high, await counts.CleanupAsync());

        var html = await GetAsync(factory, "/local");
        Assert.Matches(new Regex(@"GitHub\s*<span class=""nav-count""[^>]*>1</span>"), html);
        Assert.Equal(high > 0, html.Contains("nav-count danger", StringComparison.Ordinal));
        Assert.Contains("id=\"global-search\"", html);
    }

    [Fact]
    public async Task Local_sorts_from_the_query_string_and_folds_non_git_folders_away()
    {
        using var factory = await SeededAsync();

        var html = await GetAsync(factory, "/local");
        Assert.Contains("1 non-git folder", html);
        Assert.True(html.IndexOf("id=\"non-git\"", StringComparison.Ordinal) < html.IndexOf("Reference Docs", StringComparison.Ordinal));
        Assert.Contains("aria-sort=\"descending\"", html);
        Assert.Contains("href=\"http://localhost/local?sort=name\"", html);
        Assert.Contains("href=\"http://localhost/local?dir=asc\"", html); // clicking the active column flips it
        Assert.Contains("<option value=\"acme\">acme (1)</option>", html);

        var byName = await GetAsync(factory, "/local?sort=name&dir=desc&owner=acme");
        Assert.Contains("aria-sort=\"descending\"", byName);
        Assert.Contains("href=\"http://localhost/local?sort=name&owner=acme\"", byName);
        Assert.Contains("widget", byName);
        Assert.DoesNotContain("Reference Docs", byName); // not acme's

        Assert.DoesNotContain("widget</strong>", await GetAsync(factory, "/local?owner=" + Uri.EscapeDataString(LocalView.NoOwner)));
    }
}
