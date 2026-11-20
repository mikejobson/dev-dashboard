using System.Net;
using System.Net.Http.Json;
using DevDash.Contracts;
using DevDash.Core.GitHub;
using DevDash.Hub.Alerts;
using DevDash.Hub.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Hub.Tests;

/// <summary>Toolchains, dependency health, the activity heatmap and the command palette, on the hub's side.</summary>
public class ActivityViewTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    private static MachineView Machine(string id) =>
        new(new MachineSnapshot { Machine = new MachineInfo { MachineId = id, Hostname = id + "-host" }, Roots = ["/dev"] }, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static CloneView Clone(string machine, Dictionary<string, int>? days, Dictionary<string, int>? mine = null) =>
        new(Machine(machine), new DevEntry
        {
            Root = "/dev", Name = "widget", Path = "/dev/widget", Kind = EntryKind.GitRepo,
            Git = new GitState { Activity = days is null ? null : new CommitActivity { Days = days, Mine = mine } },
        }, null);

    private static RepoRow Row(string key, params CloneView[] clones) => new() { Key = key, Name = key.Split('/')[^1], Kind = EntryKind.GitRepo, Clones = clones };

    [Fact]
    public void A_commit_two_machines_have_is_counted_once_and_repos_add_up()
    {
        // Both machines have the 3 commits of the 18th; the laptop has 2 more on the 19th that the desktop has not fetched.
        var widget = Row("github.com/acme/widget",
            Clone("laptop", new() { ["2026-11-18"] = 3, ["2026-11-19"] = 2 }, new() { ["2026-11-18"] = 1 }),
            Clone("desktop", new() { ["2026-11-18"] = 3, ["2026-11-17"] = 1 }, new() { ["2026-11-18"] = 1, ["2026-11-17"] = 1 }),
            Clone("old-agent", null));
        var gadget = Row("github.com/acme/gadget", Clone("laptop", new() { ["2026-11-18"] = 4, ["bad"] = 9, ["2026-11-16"] = 0 }));

        Assert.Equal(new Dictionary<DateOnly, int> { [new(2026, 11, 17)] = 1, [new(2026, 11, 18)] = 3, [new(2026, 11, 19)] = 2 }, ActivityView.ForRepo(widget, mine: false));
        Assert.Equal(new Dictionary<DateOnly, int> { [new(2026, 11, 17)] = 1, [new(2026, 11, 18)] = 1 }, ActivityView.ForRepo(widget, mine: true));

        var total = ActivityView.Total([widget, gadget], mine: false);
        Assert.Equal(new Dictionary<DateOnly, int> { [new(2026, 11, 17)] = 1, [new(2026, 11, 18)] = 7, [new(2026, 11, 19)] = 2 }, total);

        // gadget's clone has no user.email, so it cannot say which commits are mine and adds none.
        Assert.Equal(2, ActivityView.Total([widget, gadget], mine: true).Values.Sum());
        Assert.True(ActivityView.Any([widget]));
        Assert.True(ActivityView.AnyMine([widget, gadget]));
        Assert.False(ActivityView.AnyMine([gadget]));
        Assert.False(ActivityView.Any([Row("k/none", Clone("old-agent", null))]));
    }

    [Fact]
    public void The_grid_is_a_year_of_whole_weeks_starting_on_monday()
    {
        var days = new Dictionary<DateOnly, int>
        {
            [Today] = 12,
            [new(2026, 11, 3)] = 1,
            [Today.AddDays(-364)] = 2,   // the first day of the window
            [Today.AddDays(-365)] = 50,  // too old
            [Today.AddDays(1)] = 50,     // the future
        };

        var map = ActivityView.Build(days, Today);

        Assert.Equal(365, map.Cells.Count);
        Assert.Equal((Today.AddDays(-364), Today), (map.From, map.To));
        Assert.Equal((15, 3), (map.Total, map.ActiveDays));
        Assert.Equal(Today, map.Busiest!.Date);
        Assert.InRange(map.Weeks, 53, 54);
        Assert.All(map.Cells, c => Assert.Equal(((int)c.Date.DayOfWeek + 6) % 7, c.Weekday)); // Monday is row 0
        Assert.Equal(0, map.Cells[0].Week);
        Assert.Equal(map.Weeks - 1, map.Cells[^1].Week);
        Assert.Equal(4, map.Cells.Single(c => c.Date == Today).Level);
        Assert.Equal(1, map.Cells.Single(c => c.Date == new DateOnly(2026, 11, 3)).Level);
        Assert.Equal(0, map.Cells.Single(c => c.Date == new DateOnly(2026, 11, 4)).Level);
        Assert.InRange(map.Months.Count, 11, 13);
        Assert.Equal("Nov", map.Months[^1].Label);
        Assert.Equal(15, map.ByMonth.Sum(m => m.Count));
    }

    [Fact]
    public void One_enormous_day_does_not_wash_out_the_rest()
    {
        var days = Enumerable.Range(0, 40).ToDictionary(i => Today.AddDays(-i * 2), i => i == 0 ? 500 : 6);

        var map = ActivityView.Build(days, Today);

        Assert.Equal(4, map.Cells.Single(c => c.Count == 500).Level);
        Assert.All(map.Cells.Where(c => c.Count == 6), c => Assert.Equal(4, c.Level));
    }

    [Fact]
    public void Tooltips_and_the_summary_read_as_sentences()
    {
        var map = ActivityView.Build(new Dictionary<DateOnly, int> { [new(2026, 11, 3)] = 12, [new(2025, 12, 24)] = 1 }, Today);

        Assert.Equal("12 commits on 3 Nov", ActivityView.Tooltip(map.Cells.Single(c => c.Count == 12), Today));
        Assert.Equal("1 commit on 24 Dec 2025", ActivityView.Tooltip(map.Cells.Single(c => c.Count == 1), Today));
        Assert.Equal("No commits on 4 Nov", ActivityView.Tooltip(map.Cells.Single(c => c.Date == new DateOnly(2026, 11, 4)), Today));
        Assert.Equal("13 commits on 2 days in the last year. Busiest: 12 commits on 3 Nov.", ActivityView.Summary(map, Today));
        Assert.Equal("No commits in the last year.", ActivityView.Summary(ActivityView.Build(new Dictionary<DateOnly, int>(), Today), Today));
    }

    [Fact]
    public void The_svg_has_a_titled_square_per_day_and_theme_classes_not_colours()
    {
        var map = ActivityView.Build(new Dictionary<DateOnly, int> { [new(2026, 11, 3)] = 12 }, Today);

        var svg = ActivityView.Svg(map, Today, compact: false);

        Assert.Equal(365, System.Text.RegularExpressions.Regex.Matches(svg, "<rect ").Count);
        Assert.Equal(365, System.Text.RegularExpressions.Regex.Matches(svg, "<title>").Count);
        Assert.Contains("<title>12 commits on 3 Nov</title>", svg);
        Assert.Contains("role=\"img\" aria-label=\"12 commits on 1 day in the last year. Busiest: 12 commits on 3 Nov.\"", svg);
        Assert.Contains("class=\"h4\"", svg);
        Assert.Contains(">Mon<", svg);
        Assert.DoesNotContain("fill=", svg);
        Assert.DoesNotContain("#", svg);
        Assert.DoesNotContain(">Mon<", ActivityView.Svg(map, Today, compact: true));
    }
}

public class PaletteTests
{
    private static MachineView Machine(string id, bool refresh = true) => new(
        new MachineSnapshot
        {
            Machine = new MachineInfo { MachineId = id, Hostname = id + "-host", Os = "macOS", AcceptsRefresh = refresh, AgentVersion = "0.6.0" },
            Roots = ["/dev"],
            Entries = [Entry("widget"), Entry("gadget"), Entry("web-widget-kit")],
        }, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static DevEntry Entry(string name) => new()
    {
        Root = "/dev", Name = name, Path = "/dev/" + name, Kind = EntryKind.GitRepo,
        Git = new GitState { Branch = name == "gadget" ? "feature/login" : "main", Remotes = [new RemoteInfo { Name = "origin", Url = $"https://github.com/acme/{name}.git", Normalized = $"github.com/acme/{name}" }] },
    };

    private static RepoRow Row(string name, params MachineView[] machines) => new()
    {
        Key = "github.com/acme/" + name, Name = name, Owner = "acme", Kind = EntryKind.GitRepo,
        Clones = machines.Select(m => new CloneView(m, m.Snapshot.Entries.Single(e => e.Name == name), null)).ToList(),
    };

    private static readonly MachineView Mac = Machine("mac");
    private static readonly MachineView Pc = Machine("pc", refresh: false);

    private static IReadOnlyList<PaletteItem> Items(int safe = 3, int updatable = 2) => Palette.Items(
        [Row("widget", Mac, Pc), Row("gadget", Mac), Row("web-widget-kit", Mac)],
        [new PullRequestInfo { Repo = "acme/widget", Number = 7, Title = "Add login form", Url = "https://github.com/acme/widget/pull/7", HeadRef = "feature/login" }],
        [Mac, Pc],
        m => m.Machine.AcceptsRefresh,
        safe,
        updatable);

    private static List<string> Titles(string query, IReadOnlyList<string>? recent = null, int limit = Palette.DefaultLimit) =>
        Palette.Search(query, Items(), recent ?? [], limit).Select(i => i.Title).ToList();

    [Fact]
    public void A_prefix_beats_a_word_start_which_beats_a_match_in_the_middle()
    {
        // "widget" is the whole name of one repo, the start of a word in another, and in the middle of nothing else's name.
        Assert.Equal(["widget", "web-widget-kit", "Refresh widget on mac-host", "Refresh web-widget-kit on mac-host"], Titles("widget").Take(4));
        Assert.Equal(["widget", "web-widget-kit"], Titles("wid").Take(2));          // prefix, then word start
        Assert.Equal("web-widget-kit", Titles("web")[0]);
        Assert.Equal(["gadget", "widget"], Titles("dget").Where(t => !t.StartsWith("Refresh", StringComparison.Ordinal)).Take(2)); // only in the middle: shortest first
        Assert.Equal("Safety", Titles("saf")[0]);                                     // the page before "Remove 3 safe projects…"
        Assert.Contains("Remove 3 safe projects…", Titles("saf"));
        Assert.Empty(Titles("zzz"));
    }

    [Fact]
    public void Every_word_must_match_and_other_fields_count_for_less_than_the_title()
    {
        Assert.Equal(["Refresh gadget on mac-host"], Titles("ref gad"));
        Assert.Equal("Add login form", Titles("login")[0]);              // a title beats gadget's branch feature/login
        Assert.Contains("gadget", Titles("login"));
        Assert.Equal(["Add login form"], Titles("#7"));
        Assert.Equal(["Machines", "mac-host"], Titles("mac").Take(2));  // both start with it: the page first
        Assert.Equal("Machines", Titles("toolchains")[0]);               // a page's keyword
        Assert.Equal("Fetch all", Titles("fetch")[0]);
        Assert.Equal(5, Titles("acme", limit: 5).Count);
    }

    [Fact]
    public void Something_used_recently_moves_up_but_not_past_a_much_better_match()
    {
        Assert.Equal(["widget", "web-widget-kit"], Titles("wid").Take(2));
        Assert.Equal(["web-widget-kit", "widget"], Titles("wid", ["repo:github.com/acme/web-widget-kit"]).Take(2).Order(StringComparer.Ordinal).ToList());
        // Word start (70) + most recent (25) does not beat a prefix (100)…
        Assert.Equal("widget", Titles("wid", ["repo:github.com/acme/web-widget-kit"])[0]);
        // …but between two equal matches the recent one comes first.
        Assert.Equal("gadget", Titles("dget")[0]);
        Assert.Equal("widget", Titles("dget", ["repo:github.com/acme/widget"])[0]);
        Assert.Equal((25, 22, 0), (Palette.RecentBoost(["a", "b"], "a"), Palette.RecentBoost(["a", "b"], "b"), Palette.RecentBoost(["a", "b"], "c")));
    }

    [Fact]
    public void With_nothing_typed_it_opens_with_recents_then_pages_then_the_general_actions()
    {
        var shown = Palette.Search("  ", Items(), ["repo:github.com/acme/gadget", "page:safety", "gone:whatever"]);

        Assert.Equal(["gadget", "Safety", "Overview", "Local", "GitHub", "Cleanup", "Alerts", "Machines", "Fetch all", "Remove 3 safe projects…", "Update all agents (2)…"], shown.Select(i => i.Title));
        Assert.Single(shown, i => i.Title == "Safety");
    }

    [Fact]
    public void Destructive_things_are_links_to_a_page_that_confirms_never_commands()
    {
        var items = Items();

        var remove = items.Single(i => i.Id == "action:remove-safe");
        var update = items.Single(i => i.Id == "action:update-all");
        Assert.Equal((PaletteCommand.Navigate, "safety?ask=remove-safe"), (remove.Command, remove.Url));
        Assert.Equal((PaletteCommand.Navigate, "machines?ask=update-all"), (update.Command, update.Url));
        Assert.Empty(remove.Targets);
        Assert.Empty(update.Targets);

        // The only thing the palette itself does is ask for a refresh, and only of machines that take one.
        var commands = items.Where(i => i.Command != PaletteCommand.Navigate).ToList();
        Assert.All(commands, c => Assert.Equal(PaletteCommand.Refresh, c.Command));
        Assert.Equal(["Fetch all", "Refresh gadget on mac-host", "Refresh web-widget-kit on mac-host", "Refresh widget on mac-host"], commands.Select(c => c.Title).Order(StringComparer.Ordinal));
        Assert.All(commands.SelectMany(c => c.Targets), t => Assert.Equal("mac", t.Machine.MachineId));
        var all = commands.Single(c => c.Id == "action:fetch-all");
        Assert.Equal(("Refresh 3 repos on 1 machine now", 3, (string?)null), (all.Detail, all.Targets.Count, all.Url));

        // Nothing to remove or update: those rows are not offered at all.
        Assert.DoesNotContain(Items(safe: 0, updatable: 0), i => i.Id is "action:remove-safe" or "action:update-all");
        Assert.Equal("Update the agent that is behind…", Items(updatable: 1).Single(i => i.Id == "action:update-all").Title);
    }

    [Fact]
    public void Fetch_all_never_asks_for_more_than_the_hub_keeps_track_of()
    {
        var entries = Enumerable.Range(0, 90).Select(i => Entry("repo" + i)).ToList();
        var machine = new MachineView(new MachineSnapshot { Machine = new MachineInfo { MachineId = "mac", Hostname = "mac-host", AcceptsRefresh = true }, Roots = ["/dev"], Entries = entries }, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var rows = entries.Select((e, i) => new RepoRow
        {
            Key = "github.com/acme/" + e.Name, Name = e.Name, Kind = EntryKind.GitRepo, Clones = [new CloneView(machine, e, null)],
            LastActivity = DateTimeOffset.UnixEpoch.AddDays(i),
        }).ToList();

        var all = Palette.Items(rows, [], [machine], _ => true, 0, 0).Single(i => i.Id == "action:fetch-all");

        Assert.Equal(Palette.FetchAllLimit, all.Targets.Count);
        Assert.True(Palette.FetchAllLimit < ActionQueue.KeepRefreshes);
        Assert.Equal("Refresh 60 repos on 1 machine now (the 60 most recently active of 90)", all.Detail);
        Assert.Equal("/dev/repo89", all.Targets[0].Path);
    }

    [Theory]
    [InlineData("", "page:overview")]
    [InlineData("safety", "page:safety")]
    [InlineData("safety?group=repo", "page:safety")]
    [InlineData("machines#machine-mac", "page:machines")]
    [InlineData("repo?key=github.com%2Facme%2FWidget", "repo:github.com/acme/widget")]
    [InlineData("repo", null)]
    [InlineData("not-found", null)]
    public void An_address_is_remembered_as_the_page_or_repo_it_is(string url, string? expected) => Assert.Equal(expected, Palette.IdOf(url));
}

public class PartFourPageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static MachineSnapshot Snapshot()
    {
        var snapshot = HubFactory.Snapshot(version: "0.6.0", refresh: true);
        var widget = snapshot.Entries[0];
        var today = DateOnly.FromDateTime(DateTime.Now);
        return snapshot with
        {
            Machine = snapshot.Machine with
            {
                DependencyChecks = true,
                Toolchains = new ToolchainInventory
                {
                    CollectedAt = Now,
                    Tools =
                    [
                        new InstalledToolchain { Tool = ToolNames.Node, Active = "20.11.0", Versions = ["22.3.0", "20.11.0"], Managers = ["nvm"] },
                        new InstalledToolchain { Tool = ToolNames.Dotnet, Versions = ["9.0.305", "8.0.404"] },
                    ],
                },
            },
            Entries =
            [
                widget with
                {
                    Pins =
                    [
                        new ToolchainPin { Tool = ToolNames.Dotnet, Version = "10.0.100", Source = "global.json", RollForward = "latestFeature" },
                        new ToolchainPin { Tool = ToolNames.Node, Version = "20", Source = ".nvmrc" },
                    ],
                    Dependencies = new DependencyHealth
                    {
                        CheckedAt = Now.AddHours(-3),
                        Reports =
                        [
                            new DependencyReport
                            {
                                Ecosystem = DependencyReport.NuGet, Target = "Widget.sln",
                                Vulnerable = new SeverityCounts { Critical = 1, Moderate = 1 },
                                TopVulnerable = [new VulnerablePackage { Name = "Some.Json", Version = "12.0.1", Severity = "critical", Advisories = ["GHSA-aaaa-bbbb-cccc"] }],
                                Outdated = 2,
                                TopOutdated = [new OutdatedPackage { Name = "Some.Json", Current = "12.0.1", Latest = "13.0.3" }],
                            },
                            new DependencyReport { Ecosystem = DependencyReport.Npm, Target = "package-lock.json", Skipped = "the package source could not be reached" },
                        ],
                    },
                    Git = widget.Git! with
                    {
                        Activity = new CommitActivity
                        {
                            Days = new Dictionary<string, int> { [today.ToString("yyyy-MM-dd")] = 5, [today.AddDays(-10).ToString("yyyy-MM-dd")] = 2 },
                            Mine = new Dictionary<string, int> { [today.ToString("yyyy-MM-dd")] = 3 },
                        },
                    },
                },
                snapshot.Entries[1],
            ],
        };
    }

    private static async Task<HubFactory> SeededAsync(MachineSnapshot? snapshot = null)
    {
        var factory = new HubFactory();
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync());
        (await agent.PostAsync("/api/v1/snapshots", JsonContent.Create(snapshot ?? Snapshot(), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();
        return factory;
    }

    private static async Task<string> GetAsync(HubFactory factory, string url)
    {
        var response = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_new_fields_survive_ingest_and_a_restart_of_the_hub()
    {
        using var factory = await SeededAsync();

        var restored = ActivatorUtilities.CreateInstance<SnapshotStore>(factory.Services);
        await restored.LoadAsync();
        var machine = Assert.Single(restored.Machines);
        var widget = machine.Snapshot.Entries.Single(e => e.Name == "widget");

        Assert.Equal(["22.3.0", "20.11.0"], machine.Machine.Toolchains!.Find(ToolNames.Node)!.Versions);
        Assert.True(machine.Machine.DependencyChecks);
        Assert.Equal("latestFeature", widget.Pins![0].RollForward);
        Assert.Equal((1, 2), (widget.Dependencies!.Critical, widget.Dependencies.OutdatedTotal));
        Assert.Equal(7, widget.Git!.Activity!.Days.Values.Sum());
        Assert.Equal(SnapshotSchema.Version, machine.Snapshot.SchemaVersion); // optional fields: the format did not change
    }

    [Fact]
    public async Task The_machines_page_lists_toolchains_and_the_pins_they_do_not_meet()
    {
        using var factory = await SeededAsync();

        var html = await GetAsync(factory, "/machines");

        Assert.Contains("id=\"toolchains\"", html);
        Assert.Contains(".NET SDK", html);
        Assert.Contains("9.0.305, 8.0.404", html);
        Assert.Contains("22.3.0, 20.11.0", html);
        Assert.Contains("· nvm", html);
        Assert.Contains("1 pin not met", html);
        Assert.Contains("href=\"cleanup#toolchain-mismatch\"", html);
        Assert.Contains("dependency checks on", html);
    }

    [Fact]
    public async Task A_machine_that_sends_no_inventory_says_why_and_is_never_flagged()
    {
        using var factory = await SeededAsync(Snapshot() with { Machine = HubFactory.Snapshot(version: "0.5.0").Machine });

        var html = await GetAsync(factory, "/machines");
        var findings = await factory.Services.GetRequiredService<FindingsService>().GetAsync();

        Assert.Contains("Update the agent on mac-host to 0.6.0 or newer to see its toolchains.", html);
        Assert.DoesNotContain(findings, f => f.Finding.RuleId == "toolchain-mismatch");
    }

    [Fact]
    public async Task The_repo_page_shows_pins_dependencies_and_a_small_heatmap()
    {
        using var factory = await SeededAsync();

        var html = await GetAsync(factory, "/repo?key=github.com%2Facme%2Fwidget");

        Assert.Contains("id=\"toolchains\"", html);
        Assert.Contains("rollForward latestFeature", html);
        Assert.Contains("needs 10.0.100 or a later 10.0 SDK; this machine has 9.0.305, 8.0.404", html);
        Assert.Contains("uses 20.11.0", html);
        Assert.Contains("id=\"dependencies\"", html);
        Assert.Contains("1 critical", html);
        Assert.Contains("https://github.com/advisories/GHSA-aaaa-bbbb-cccc", html);
        Assert.Contains("2 outdated", html);
        Assert.Contains("the package source could not be reached", html);
        Assert.Contains("2 vulnerable", html); // the chip beside the clone
        Assert.Contains("class=\"heat-svg\"", html);
        Assert.Contains("7 commits", html);
        Assert.Contains("3 of them with your git email.", html);
        Assert.Contains("<title>5 commits on ", html);
    }

    [Fact]
    public async Task The_overview_heatmap_counts_mine_by_default_and_everyones_on_request()
    {
        using var factory = await SeededAsync();

        var mine = await GetAsync(factory, "/");
        var all = await GetAsync(factory, "/?activity=all");

        Assert.Contains("id=\"activity\"", mine);
        Assert.Contains("<strong>3 commits</strong>", mine);
        Assert.Contains("of mine in the last year, across 1 repo", mine);
        Assert.Contains("<strong>7 commits</strong>", all);
        Assert.Contains("By month", all);
        Assert.Contains("role=\"img\"", all);
    }

    [Fact]
    public async Task Without_activity_from_any_agent_there_is_no_heatmap()
    {
        using var factory = await SeededAsync(HubFactory.Snapshot());

        Assert.DoesNotContain("heat-svg", await GetAsync(factory, "/"));
        Assert.DoesNotContain("heat-svg", await GetAsync(factory, "/repo?key=github.com%2Facme%2Fwidget"));
    }

    [Fact]
    public async Task The_local_page_and_cleanup_show_the_dependency_and_toolchain_findings()
    {
        using var factory = await SeededAsync();

        var local = await GetAsync(factory, "/local");
        var cleanup = await GetAsync(factory, "/cleanup?open=all");
        var findings = await factory.Services.GetRequiredService<FindingsService>().GetAsync();

        Assert.Contains("2 vulnerable", local);
        Assert.Contains("class=\"chip danger\"", local);
        Assert.Contains("widget: .NET SDK 10.0.100 is not on mac-host", cleanup);
        Assert.Contains("widget: 1 critical vulnerability in its dependencies", cleanup);
        Assert.Equal(Core.Cleanup.FindingSeverity.High, findings.Single(f => f.Finding.RuleId == "vulnerable-deps").Finding.Severity);
        Assert.Equal(Core.Cleanup.FindingSeverity.Medium, findings.Single(f => f.Finding.RuleId == "toolchain-mismatch").Finding.Severity);
    }

    [Fact]
    public async Task Every_page_has_the_palette_button_and_none_ships_the_dialog_open()
    {
        using var factory = await SeededAsync();

        foreach (var url in (string[])["/", "/local", "/safety", "/machines", "/alerts", "/cleanup", "/github"])
        {
            var html = await GetAsync(factory, url);
            Assert.Contains("class=\"cmdk-open\"", html);
            Assert.Contains("aria-haspopup=\"dialog\"", html);
            Assert.DoesNotContain("role=\"dialog\" aria-modal=\"true\" aria-label=\"Search and commands\"", html);
        }

        var script = await factory.CreateClient().GetAsync("/palette.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("export function init", await script.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_palette_link_alone_removes_and_updates_nothing()
    {
        using var factory = await SeededAsync();
        var queue = factory.Services.GetRequiredService<ActionQueue>();

        // What the palette's "Remove N safe projects…" and "Update all agents…" open. Fetching them is all a link can do.
        await GetAsync(factory, "/safety?ask=" + Palette.AskRemoveSafe);
        await GetAsync(factory, "/machines?ask=" + Palette.AskUpdateAll);

        Assert.Empty(await queue.RecentAsync(refreshes: false, 50));
        Assert.Empty(await queue.RecentAsync(refreshes: true, 50));
    }
}

/// <summary>
/// Part 3 keeps alert secrets encrypted with ASP.NET Core data protection. That only works across a
/// restart if the key ring is kept: the hub writes it to <c>&lt;Hub:DataDirectory&gt;/keys</c>, and in the
/// container that directory is the <c>/data</c> volume. These tests restart a hub on the same data
/// directory, which is what <c>docker compose pull &amp;&amp; docker compose up -d</c> does.
/// </summary>
public sealed class AlertSecretsRestartTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("devdash-data-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    /// <summary>A hub as the container runs it: the database and the keys both in one data directory.</summary>
    private HubFactory Hub(string? keysFrom = null)
    {
        var factory = new HubFactory();
        factory.Settings["ConnectionStrings:DevDash"] = $"Data Source={Path.Combine(_data, "devdash.db")}";
        factory.Settings["Hub:DataDirectory"] = keysFrom ?? _data;
        return factory;
    }

    private static readonly Dictionary<string, string?> Secrets = new()
    {
        [AlertSettings.NtfyTopic] = "a-topic-nobody-could-guess",
        [AlertSettings.WebhookToken] = "hook-t0ken",
    };

    [Fact]
    public async Task Saved_secrets_are_still_readable_after_the_hub_is_replaced()
    {
        using (var first = Hub())
        {
            var settings = first.Services.GetRequiredService<AlertSettings>();
            await settings.SaveAsync(await settings.GetAsync(), Secrets, "me");
            Assert.Equal("a-topic-nobody-could-guess", (await settings.GetAsync()).Channels.Ntfy.Topic);
        }

        // The key ring is on disk beside the database, not in the process or the container's home folder.
        var keys = Directory.GetFiles(Path.Combine(_data, "keys"), "key-*.xml");
        Assert.NotEmpty(keys);
        Assert.DoesNotContain("a-topic-nobody-could-guess", await File.ReadAllTextAsync(Path.Combine(_data, "devdash.db"), System.Text.Encoding.Latin1));

        using var second = Hub();
        var after = await second.Services.GetRequiredService<AlertSettings>().GetAsync();

        Assert.Equal(("a-topic-nobody-could-guess", "hook-t0ken"), (after.Channels.Ntfy.Topic, after.Channels.Webhook.Token));
        Assert.Equal(keys, Directory.GetFiles(Path.Combine(_data, "keys"), "key-*.xml")); // the same key, not a new one
    }

    [Fact]
    public async Task Without_the_key_ring_the_secrets_are_lost_not_leaked_and_the_hub_still_starts()
    {
        using (var first = Hub())
        {
            var settings = first.Services.GetRequiredService<AlertSettings>();
            await settings.SaveAsync(await settings.GetAsync(), Secrets, "me");
        }

        // The database kept, the keys not: what a data volume that only held the database would do on every update.
        var elsewhere = Directory.CreateDirectory(Path.Combine(_data, "another-volume")).FullName;
        using var second = Hub(keysFrom: elsewhere);
        var settings2 = second.Services.GetRequiredService<AlertSettings>();

        var after = await settings2.GetAsync();
        Assert.Equal(("", ""), (after.Channels.Ntfy.Topic ?? "", after.Channels.Webhook.Token ?? ""));
        Assert.Equal(HttpStatusCode.OK, (await second.CreateClient().GetAsync("/alerts")).StatusCode);

        // Entering it again works with the new key.
        await settings2.SaveAsync(after, Secrets, "me");
        Assert.Equal("hook-t0ken", (await settings2.GetAsync()).Channels.Webhook.Token);
    }
}
