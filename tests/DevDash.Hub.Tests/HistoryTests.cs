using System.Net;
using System.Text.Json;
using DevDash.Contracts;
using DevDash.Core.Safety;
using DevDash.Hub.Alerts;
using DevDash.Hub.Data;
using DevDash.Hub.History;
using DevDash.Hub.Mcp;
using DevDash.Hub.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static DevDash.Hub.Tests.AlertEvaluationTests;

namespace DevDash.Hub.Tests;

/// <summary>History and trends (§22): the rollups, risk episodes, "since yesterday", durations in alerts, the pages and the MCP tool.</summary>
public class HistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private const long Mb = 1024 * 1024;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static DevEntry Sized(DevEntry entry, long total, long build = 0) => entry with { Size = new SizeInfo { TotalBytes = total, ReclaimableBytes = build, MeasuredAt = Now } };

    private static CloneFacts Facts(string path, string machine = "mac", bool atRisk = false, RiskLevel level = RiskLevel.None, int unpushed = 0, long? size = null,
        string[]? unpushedBranches = null, string[]? allBranches = null, SafetyVerdict? verdict = null) => new()
    {
        MachineId = machine,
        Hostname = machine + "-host",
        Path = path,
        RepoKey = "github.com/acme/" + path[(path.LastIndexOf('/') + 1)..],
        Name = path[(path.LastIndexOf('/') + 1)..],
        Level = level,
        Verdict = verdict ?? (atRisk ? SafetyVerdict.AtRisk : SafetyVerdict.Safe),
        Unpushed = unpushed,
        SizeBytes = size,
        UnpushedBranches = unpushedBranches ?? [],
        AllBranches = allBranches,
    };

    // ---- rollups ----------------------------------------------------------------------------------

    [Fact]
    public void A_snapshot_rolls_up_into_clone_facts_and_machine_totals()
    {
        var snapshot = Snapshot("mac", Now,
            Sized(Repo("unpushed", Now, unpushedDays: 4), 500 * Mb, 200 * Mb),
            Sized(Repo("dirty", Now, uncommittedDays: 1), 100 * Mb),
            Sized(Repo("clean", Now), 50 * Mb, 10 * Mb));

        var clones = HistoryRollup.Clones(snapshot, Now);
        var unpushed = clones.Single(c => c.Name == "unpushed");
        Assert.Equal((2, RiskLevel.High, SafetyVerdict.AtRisk, "github.com/acme/unpushed"), (unpushed.Unpushed, unpushed.Level, unpushed.Verdict, unpushed.RepoKey));
        Assert.Equal(["feature/x"], unpushed.UnpushedBranches);
        Assert.Equal(RiskLevel.Medium, clones.Single(c => c.Name == "dirty").Level);
        var clean = clones.Single(c => c.Name == "clean");
        Assert.Equal((RiskLevel.None, 50 * Mb), (clean.Level, clean.SafeBytes));

        var totals = HistoryRollup.Totals(clones);
        Assert.Equal(new MachineTotals(3, 1, 2, 1, 1, 0, 650 * Mb, 210 * Mb, 50 * Mb), totals);
    }

    [Fact]
    public void The_days_row_keeps_the_last_value_and_the_highest()
    {
        var row = new MachineDayEntity { MachineId = "mac" };
        HistoryRollup.Apply(row, "mac-host", new MachineTotals(3, 1, 5, 1, 0, 0, 100, 10, 0), Now);
        HistoryRollup.Apply(row, "mac-host", new MachineTotals(3, 0, 1, 0, 0, 0, 120, 10, 0), Now.AddHours(1));

        Assert.Equal((1, 5, 0, 1, 120L, 2), (row.UnpushedCommits, row.MaxUnpushedCommits, row.AtRisk, row.MaxAtRisk, row.RepoBytes, row.Samples));

        var clone = new CloneDayEntity { MachineId = "mac", Path = "/dev/a" };
        HistoryRollup.Apply(clone, Facts("/dev/a", atRisk: true, level: RiskLevel.High, unpushed: 4, size: 10), isNew: true);
        HistoryRollup.Apply(clone, Facts("/dev/a", unpushed: 0, size: null), isNew: false);
        Assert.Equal((0, 4, 0, 3, 10L), (clone.Unpushed, clone.MaxUnpushed, clone.RiskLevel, clone.MaxRiskLevel, clone.SizeBytes!.Value));
    }

    [Fact]
    public void A_risk_episode_starts_moves_between_levels_ends_and_starts_again()
    {
        var t0 = Now;
        var episode = HistoryRollup.Advance(null, "mac", "/dev/a", atRisk: true, RiskLevel.Medium, t0);
        Assert.NotNull(episode);
        Assert.Equal((t0, t0, (DateTimeOffset?)null), (episode.Since, episode.MediumSince!.Value, episode.HighSince));

        // Commits on no remote a day later: the high episode starts then, the any-level one keeps its start.
        episode = HistoryRollup.Advance(episode, "mac", "/dev/a", atRisk: true, RiskLevel.High, t0.AddDays(1));
        Assert.Equal((t0, t0, t0.AddDays(1)), (episode!.Since, episode.MediumSince!.Value, episode.HighSince!.Value));
        Assert.Equal(t0.AddDays(1), episode.SinceFor(RiskLevel.High));
        Assert.Equal(t0, episode.SinceFor(RiskLevel.Low));

        // Pushed, only untracked files left: the higher levels reset.
        episode = HistoryRollup.Advance(episode, "mac", "/dev/a", atRisk: true, RiskLevel.Low, t0.AddDays(2));
        Assert.Equal((t0, (DateTimeOffset?)null, (DateTimeOffset?)null), (episode!.Since, episode.MediumSince, episode.HighSince));

        // Recovers: the episode ends.
        Assert.Null(HistoryRollup.Advance(episode, "mac", "/dev/a", atRisk: false, RiskLevel.None, t0.AddDays(3)));

        // At risk again: a new episode, from now.
        var again = HistoryRollup.Advance(null, "mac", "/dev/a", atRisk: true, RiskLevel.High, t0.AddDays(5));
        Assert.Equal((t0.AddDays(5), t0.AddDays(5)), (again!.Since, again.HighSince!.Value));
    }

    // ---- since yesterday --------------------------------------------------------------------------

    [Fact]
    public void Since_yesterday_lists_what_changed()
    {
        CloneFacts[] before =
        [
            Facts("/dev/gone"),
            Facts("/dev/risky"),
            Facts("/dev/saved", atRisk: true, level: RiskLevel.High, unpushed: 2, unpushedBranches: ["feature/a", "feature/b"]),
            Facts("/dev/big", size: 1000 * Mb),
            Facts("/dev/small", size: 1000 * Mb),
            Facts("/dev/busy", atRisk: true, level: RiskLevel.High, unpushed: 1, unpushedBranches: ["wip"]),
            Facts("/dev/other", machine: "pc"),
        ];
        CloneFacts[] now =
        [
            Facts("/dev/new"),
            Facts("/dev/risky", atRisk: true, level: RiskLevel.Medium),
            Facts("/dev/saved", allBranches: ["main", "feature/a"]),
            Facts("/dev/big", size: 1500 * Mb),
            Facts("/dev/small", size: 1050 * Mb),
            Facts("/dev/busy", atRisk: true, level: RiskLevel.High, unpushed: 4, unpushedBranches: ["wip"]),
            Facts("/dev/elsewhere", machine: "pc"),
        ];

        var changes = SinceYesterday.Compare(before, now, new HashSet<string> { "mac" });
        var lines = changes.Select(c => $"{c.Kind} {c.Tree.Name}: {c.Text}").ToList();

        Assert.Equal(
        [
            "BecameAtRisk risky: became at risk (uncommitted changes, stashes or tags)",
            "NewUnpushed busy: 3 new unpushed commits (4 in all)",
            "GotSafe saved: no longer at risk: safe to remove",
            "BranchPushed saved: feature/a pushed",
            "BranchPushed saved: feature/b deleted (its unpushed commits are gone)",
            "SizeChanged big: grew by 500 MB to 1.5 GB",
            "Added new: new clone",
            "Removed gone: gone",
        ], lines);

        // A machine with no earlier day ("pc" here) is not compared at all.
        Assert.DoesNotContain(changes, c => c.Tree.MachineId == "pc");
    }

    [Fact]
    public void A_small_or_proportionally_small_size_change_is_not_news()
    {
        Assert.False(SinceYesterday.IsBig(50 * Mb, 100 * Mb));
        Assert.False(SinceYesterday.IsBig(150 * Mb, 10_000 * Mb));
        Assert.True(SinceYesterday.IsBig(-150 * Mb, 1000 * Mb));
        Assert.True(SinceYesterday.IsBig(200 * Mb, 0));
    }

    // ---- alerts -----------------------------------------------------------------------------------

    private static IReadOnlyList<SafetyRow> Rows(MachineSnapshot snapshot)
    {
        var machine = new MachineView(snapshot, Now.AddDays(-30), Now);
        return RepoSafety.AssessAll(snapshot, Now).Select(c => new SafetyRow(machine, c)).ToList();
    }

    private static AlertTriggerOptions AtRiskOnly(int days = 0, int forDays = 0, string level = "High") => new()
    {
        AtRisk = { Enabled = true, Days = days, ForDays = forDays, Level = level },
        MachineOffline = { Enabled = false },
        Diverged = { Enabled = false },
        CiFailed = { Enabled = false },
        ActionFailed = { Enabled = false },
    };

    [Fact]
    public void The_at_risk_alert_says_how_long_from_the_hubs_episode_or_the_commits_whichever_is_older()
    {
        var rows = Rows(Snapshot("laptop", Now, Repo("old", Now, unpushedDays: 4), Repo("edited", Now, uncommittedDays: 1)));
        var episodes = new Dictionary<(string, string), RiskEpisodeEntity>
        {
            [("laptop", "/dev/old")] = new() { MachineId = "laptop", Path = "/dev/old", Since = Now.AddDays(-7), MediumSince = Now.AddDays(-7), HighSince = Now.AddDays(-7) },
            [("laptop", "/dev/edited")] = new() { MachineId = "laptop", Path = "/dev/edited", Since = Now.AddDays(-2), MediumSince = Now.AddDays(-2) },
        };

        var alerts = AlertEvaluator.Evaluate(new AlertInputs { Safety = rows, RiskEpisodes = episodes }, AtRiskOnly(level: "Medium"), Now);

        Assert.StartsWith("Unpushed for 7 days.\n", alerts.Single(a => a.Title.StartsWith("old", StringComparison.Ordinal)).Body);
        // Uncommitted changes carry the last edit, not a start: only the hub's episode can say how long.
        Assert.StartsWith("Uncommitted for 2 days.\n", alerts.Single(a => a.Title.StartsWith("edited", StringComparison.Ordinal)).Body);

        // With no episode, the oldest unpushed commit still dates it.
        var plain = AlertEvaluator.Evaluate(new AlertInputs { Safety = rows }, AtRiskOnly(), Now);
        Assert.StartsWith("Unpushed for 4 days.\n", Assert.Single(plain).Body);
    }

    [Fact]
    public void The_at_risk_threshold_waits_until_the_hub_has_seen_it_long_enough()
    {
        var rows = Rows(Snapshot("laptop", Now, Repo("old", Now, unpushedDays: 30)));
        RiskEpisodeEntity Seen(double days) => new() { MachineId = "laptop", Path = "/dev/old", Since = Now.AddDays(-days), HighSince = Now.AddDays(-days) };

        Assert.Empty(AlertEvaluator.Evaluate(new AlertInputs { Safety = rows }, AtRiskOnly(forDays: 3), Now)); // never seen: no episode
        Assert.Empty(AlertEvaluator.Evaluate(new AlertInputs { Safety = rows, RiskEpisodes = new Dictionary<(string, string), RiskEpisodeEntity> { [("laptop", "/dev/old")] = Seen(2) } }, AtRiskOnly(forDays: 3), Now));
        Assert.Single(AlertEvaluator.Evaluate(new AlertInputs { Safety = rows, RiskEpisodes = new Dictionary<(string, string), RiskEpisodeEntity> { [("laptop", "/dev/old")] = Seen(3) } }, AtRiskOnly(forDays: 3), Now));

        // The threshold counts the episode at the configured level: at risk for weeks, but commits on no remote only since yesterday.
        var mixed = new RiskEpisodeEntity { MachineId = "laptop", Path = "/dev/old", Since = Now.AddDays(-20), MediumSince = Now.AddDays(-20), HighSince = Now.AddDays(-1) };
        Assert.Empty(AlertEvaluator.Evaluate(new AlertInputs { Safety = rows, RiskEpisodes = new Dictionary<(string, string), RiskEpisodeEntity> { [("laptop", "/dev/old")] = mixed } }, AtRiskOnly(forDays: 3), Now));

        // 0 keeps the old behaviour.
        Assert.Single(AlertEvaluator.Evaluate(new AlertInputs { Safety = rows }, AtRiskOnly(forDays: 0), Now));
    }

    [Fact]
    public void A_risk_younger_than_a_day_says_so()
    {
        var risk = new SafetyReason { Kind = SafetyReasonKind.UntrackedFiles, Effect = SafetyEffect.Risk, Level = RiskLevel.Low, Text = "1 untracked file" };
        Assert.Equal("Untracked for less than a day", AlertEvaluator.AtRiskFor(risk, new RiskEpisodeEntity { MachineId = "m", Path = "p", Since = Now.AddHours(-3) }, Now));
        Assert.Null(AlertEvaluator.AtRiskFor(risk, null, Now));
    }

    // ---- charts -----------------------------------------------------------------------------------

    [Fact]
    public void A_line_chart_breaks_at_missing_days_and_titles_every_point()
    {
        var from = new DateOnly(2026, 11, 1);
        var svg = HistoryCharts.Line(
            [new ChartSeries("mac-host", 0, [(from, 1), (from.AddDays(1), 2), (from.AddDays(5), 4), (from.AddDays(6), 3)]), new ChartSeries("pc-host", 1, [(from.AddDays(6), 7)])],
            from, from.AddDays(6), HistoryCharts.Number, "At risk");

        Assert.StartsWith("<svg class=\"chart-svg\"", svg);
        Assert.Equal(2, CountOf(svg, "<polyline"));                // two runs for mac, a dot for pc
        Assert.Contains("<title>mac-host: 4 on 6 Nov</title>", svg);
        Assert.Contains("<title>pc-host: 7 on 7 Nov</title>", svg);
        Assert.Contains("class=\"chart-series s1\"", svg);
        Assert.Contains(">pc-host</text>", svg);                    // direct label
        Assert.Contains("aria-label=\"At risk\"", svg);

        var spark = HistoryCharts.Sparkline([(from, 10 * Mb), (from.AddDays(3), 30 * Mb)], HistoryCharts.Bytes, "Size");
        Assert.Contains("Size: 10 MB on 1 Nov, 30 MB on 4 Nov", spark);
        Assert.Contains("<polyline", spark);
        Assert.Contains("no history yet", HistoryCharts.Sparkline([], HistoryCharts.Bytes, "Size"));
    }

    [Fact]
    public void Top_growing_compares_the_first_and_last_size_in_the_window()
    {
        var day = new DateOnly(2026, 11, 1);
        CloneDayEntity Row(string path, int offset, long size) => new() { MachineId = "mac", Path = path, Day = day.AddDays(offset), RepoKey = "k" + path, Name = path, SizeBytes = size };

        var top = HistoryStore.TopGrowing([Row("/a", 0, 100), Row("/a", 9, 400), Row("/b", 0, 100), Row("/b", 9, 900), Row("/c", 0, 500), Row("/c", 9, 100), Row("/d", 9, 999)],
            new Dictionary<string, string> { ["mac"] = "mac-host" }, 10);

        Assert.Equal(["/b", "/a"], top.Select(g => g.Path));
        Assert.Equal((800L, "mac-host"), (top[0].Growth, top[0].Hostname));
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;

    // ---- the hub, end to end ----------------------------------------------------------------------

    private static (HubFactory Factory, Clock Clock) Hub(params (string Key, string Value)[] settings)
    {
        var clock = new Clock(Now);
        var factory = new HubFactory { Time = clock };
        foreach (var (key, value) in settings)
        {
            factory.Settings[key] = value;
        }

        return (factory, clock);
    }

    private static Task IngestAsync(HubFactory factory, MachineSnapshot snapshot) =>
        factory.Services.GetRequiredService<SnapshotStore>().IngestAsync(snapshot);

    private static async Task<T> DbAsync<T>(HubFactory factory, Func<DevDashDbContext, Task<T>> query)
    {
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>().CreateDbContextAsync();
        return await query(db);
    }

    [Fact]
    public async Task Each_snapshot_upserts_the_days_rows_and_a_new_day_gets_new_ones()
    {
        var (factory, clock) = Hub();
        using var _ = factory;
        var history = factory.Services.GetRequiredService<HistoryStore>();
        var today = history.Today;

        await IngestAsync(factory, Snapshot("mac", clock.Now, Sized(Repo("widget", clock.Now, unpushedDays: 2), 300 * Mb)));
        clock.Now = clock.Now.AddMinutes(30);
        await IngestAsync(factory, Snapshot("mac", clock.Now, Sized(Repo("widget", clock.Now), 310 * Mb)));

        var day = Assert.Single(await DbAsync(factory, db => db.HistoryMachineDays.ToListAsync()));
        Assert.Equal((today, 2, 0, 2, 0, 1, 310 * Mb), (day.Day, day.Samples, day.UnpushedCommits, day.MaxUnpushedCommits, day.AtRisk, day.MaxAtRisk, day.RepoBytes));
        var clone = Assert.Single(await DbAsync(factory, db => db.HistoryCloneDays.ToListAsync()));
        Assert.Equal(("github.com/acme/widget", 0, 2, 3), (clone.RepoKey, clone.RiskLevel, clone.MaxUnpushed, clone.MaxRiskLevel));

        clock.Now = clock.Now.AddDays(1);
        await IngestAsync(factory, Snapshot("mac", clock.Now, Sized(Repo("widget", clock.Now), 320 * Mb)));
        Assert.Equal(2, await DbAsync(factory, db => db.HistoryMachineDays.CountAsync()));
        Assert.Equal(2, await DbAsync(factory, db => db.HistoryCloneDays.CountAsync()));

        // Forgetting a machine takes its history with it.
        await factory.Services.GetRequiredService<SnapshotStore>().ForgetAsync("mac");
        Assert.Equal(0, await DbAsync(factory, db => db.HistoryCloneDays.CountAsync()));
        Assert.Equal(0, await DbAsync(factory, db => db.HistoryMachineDays.CountAsync()));
    }

    [Fact]
    public async Task Rows_older_than_the_retention_are_pruned_once_a_day()
    {
        var (factory, clock) = Hub(("Hub:History:RetentionDays", "3"));
        using var _ = factory;

        for (var i = 0; i < 6; i++)
        {
            await IngestAsync(factory, Snapshot("mac", clock.Now, Repo("widget", clock.Now)));
            clock.Now = clock.Now.AddDays(1);
        }

        var days = await DbAsync(factory, db => db.HistoryMachineDays.Select(d => d.Day).ToListAsync());
        Assert.Equal(3, days.Count);
        Assert.Equal(3, await DbAsync(factory, db => db.HistoryCloneDays.CountAsync()));
        Assert.True(days.Min() >= factory.Services.GetRequiredService<HistoryStore>().Today.AddDays(-3));
    }

    [Fact]
    public async Task History_switched_off_records_no_rows_but_still_tracks_risk()
    {
        var (factory, clock) = Hub(("Hub:History:Enabled", "false"));
        using var _ = factory;

        await IngestAsync(factory, Snapshot("mac", clock.Now, Repo("widget", clock.Now, unpushedDays: 1)));

        Assert.Equal(0, await DbAsync(factory, db => db.HistoryMachineDays.CountAsync()));
        Assert.Equal(1, await DbAsync(factory, db => db.RiskEpisodes.CountAsync()));
    }

    [Fact]
    public async Task Risk_since_is_kept_in_the_database_across_episodes_and_feeds_the_alert()
    {
        var (factory, clock) = Hub(("Hub:Alerts:Triggers:AtRisk:Days", "0"), ("Hub:Alerts:Triggers:AtRisk:ForDays", "5"), ("Hub:Alerts:Channels:Ntfy:Enabled", "true"), ("Hub:Alerts:Channels:Ntfy:Topic", "t"));
        using var _ = factory;
        var alerts = factory.Services.GetRequiredService<AlertService>();
        var start = clock.Now;

        // Commits made today, so only the hub's episode can say how long they have waited.
        await IngestAsync(factory, Snapshot("mac", clock.Now, Repo("widget", clock.Now, unpushedDays: 0)));
        clock.Now = start.AddDays(3);
        await IngestAsync(factory, Snapshot("mac", clock.Now, Repo("widget", start, unpushedDays: 0)));
        var episode = Assert.Single(await DbAsync(factory, db => db.RiskEpisodes.ToListAsync()));
        Assert.Equal((start, start), (episode.Since, episode.HighSince!.Value));
        Assert.Equal(0, (await alerts.EvaluateAsync()).Active); // seen for 3 days, the threshold is 5

        clock.Now = start.AddDays(6);
        await IngestAsync(factory, Snapshot("mac", clock.Now, Repo("widget", start, unpushedDays: 0)));
        Assert.Equal(1, (await alerts.EvaluateAsync()).Active);
        Assert.Contains("Unpushed for 6 days.", factory.AlertRequests.Last().Body);

        // Pushed: the episode ends. At risk again later: it starts over.
        await IngestAsync(factory, Snapshot("mac", clock.Now, Repo("widget", clock.Now)));
        Assert.Equal(0, await DbAsync(factory, db => db.RiskEpisodes.CountAsync()));
        clock.Now = start.AddDays(7);
        await IngestAsync(factory, Snapshot("mac", clock.Now, Repo("widget", clock.Now, unpushedDays: 0)));
        Assert.Equal(clock.Now, (await DbAsync(factory, db => db.RiskEpisodes.SingleAsync())).Since);
        Assert.Equal(0, (await alerts.EvaluateAsync()).Active);
    }

    [Fact]
    public async Task The_pages_show_since_yesterday_the_charts_and_the_repo_trends()
    {
        var (factory, clock) = Hub();
        using var _ = factory;

        await IngestAsync(factory, Snapshot("mac", clock.Now, Sized(Repo("widget", clock.Now), 300 * Mb), Repo("gadget", clock.Now)));
        var client = factory.CreateClient();
        Assert.DoesNotContain("Since yesterday", await client.GetStringAsync("/"));

        clock.Now = clock.Now.AddDays(1);
        await IngestAsync(factory, Snapshot("mac", clock.Now, Sized(Repo("widget", clock.Now, unpushedDays: 0), 900 * Mb)));

        var overview = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        Assert.Contains("Since yesterday", overview);
        Assert.Contains("compared with yesterday", overview);
        Assert.Contains("Became at risk", overview);
        Assert.Contains("grew by 600 MB to 900 MB", overview);
        Assert.Contains("Removed clones", overview);

        var page = WebUtility.HtmlDecode(await client.GetStringAsync("/history"));
        Assert.Contains("<h1>History</h1>", page);
        Assert.Contains("Disk used by repos", page);
        Assert.Contains("<svg class=\"chart-svg\"", page);
        Assert.Contains("mac-host: 900 MB on", page);
        Assert.Contains("Growing fastest", page);
        Assert.Contains("+600 MB", page);
        Assert.Contains("href=\"history\"", page); // the nav link

        var repo = WebUtility.HtmlDecode(await client.GetStringAsync("/repo?key=github.com%2Facme%2Fwidget"));
        Assert.Contains("Trends", repo);
        Assert.Contains("class=\"spark-svg", repo);
        Assert.Contains("Size on mac-host: 300 MB on", repo);
    }

    [Fact]
    public async Task The_history_page_says_when_there_is_nothing_yet()
    {
        var (factory, _) = Hub();
        using var __ = factory;
        var page = await factory.CreateClient().GetStringAsync("/history?days=90");
        Assert.Contains("No history yet", page);
    }

    [Fact]
    public async Task The_mcp_history_tool_returns_a_metric_per_day()
    {
        var (factory, clock) = Hub();
        using var _ = factory;
        await IngestAsync(factory, Snapshot("mac", clock.Now, Sized(Repo("widget", clock.Now, unpushedDays: 1), 300 * Mb)));
        clock.Now = clock.Now.AddDays(1);
        await IngestAsync(factory, Snapshot("mac", clock.Now, Sized(Repo("widget", clock.Now, unpushedDays: 1), 400 * Mb)));

        var token = (await factory.Services.GetRequiredService<McpTokenService>().IssueAsync("t", McpScopes.Read, "me@example.com")).Plaintext;
        var http = factory.CreateClient();
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
            },
            http,
            ownsHttpClient: true));

        async Task<JsonElement> Call(Dictionary<string, object?> args)
        {
            var result = await client.CallToolAsync("history", args);
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
            Assert.False(result.IsError == true, text);
            return JsonDocument.Parse(text).RootElement;
        }

        var disk = await Call(new() { ["metric"] = "disk", ["days"] = 7 });
        var series = Assert.Single(disk.GetProperty("series").EnumerateArray());
        Assert.Equal("mac-host", series.GetProperty("machine").GetString());
        Assert.Equal([300.0 * Mb, 400.0 * Mb], series.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("value").GetDouble()));

        var unpushed = await Call(new() { ["metric"] = "unpushed", ["project"] = "widget", ["machine"] = "mac" });
        var tree = Assert.Single(unpushed.GetProperty("series").EnumerateArray());
        Assert.Equal("/dev/widget", tree.GetProperty("path").GetString());
        Assert.All(tree.GetProperty("points").EnumerateArray(), p => Assert.Equal(2, p.GetProperty("value").GetDouble()));

        var wrong = await client.CallToolAsync("history", new Dictionary<string, object?> { ["metric"] = "nonsense" });
        Assert.True(wrong.IsError);
    }

    [Fact]
    public async Task A_database_from_before_history_gains_its_tables_on_start()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DevDashDbContext>().UseSqlite(connection).Options;

        await using (var db = new DevDashDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Cache.Add(new CacheEntity { Key = "kept", Json = "{}" });
            await db.SaveChangesAsync();
            // What a 0.7 hub's database looks like.
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"HistoryMachineDays\"; DROP TABLE \"HistoryCloneDays\"; DROP TABLE \"RiskEpisodes\";");
        }

        await using (var db = new DevDashDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await Assert.ThrowsAsync<SqliteException>(() => db.HistoryCloneDays.CountAsync());

            var created = await SchemaUpgrade.CreateMissingTablesAsync(db);
            Assert.Contains("HistoryMachineDays", created);
            Assert.Contains("HistoryCloneDays", created);
            Assert.Contains("RiskEpisodes", created);
            Assert.Contains("IX_HistoryCloneDays_RepoKey", created);

            db.HistoryCloneDays.Add(new CloneDayEntity { MachineId = "m", Path = "/p", Day = new DateOnly(2026, 11, 20) });
            db.RiskEpisodes.Add(new RiskEpisodeEntity { MachineId = "m", Path = "/p", Since = Now });
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.HistoryCloneDays.CountAsync(d => d.Day >= new DateOnly(2026, 11, 1)));
            Assert.Equal("kept", (await db.Cache.SingleAsync()).Key);
            Assert.Empty(await SchemaUpgrade.CreateMissingTablesAsync(db));
        }
    }
}
