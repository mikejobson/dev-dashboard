using System.Net;
using System.Net.Http.Json;
using DevDash.Contracts;
using DevDash.Hub.Data;
using DevDash.Hub.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Hub.Tests;

/// <summary>§21 on the hub: the Disk page, the dev server lists and their clashes, and the queue's checks for the two new actions.</summary>
public sealed class DiskAndDevServerHubTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static DiskItem Item(DiskCacheId id, long bytes, bool cleanable = true, long? reclaimable = null, string? path = null) => new()
    {
        Id = id.ToString(),
        Path = path ?? (id.ToString().StartsWith("Docker", StringComparison.Ordinal) ? null : "/Users/me/" + id),
        Bytes = bytes,
        ReclaimableBytes = reclaimable,
        Cleanable = cleanable,
        MeasuredAt = Now.AddHours(-1),
        LastModified = Now.AddDays(-3),
    };

    private static readonly DiskReport Report = new()
    {
        CollectedAt = Now.AddHours(-1),
        Items =
        [
            Item(DiskCacheId.NuGetGlobalPackages, 8_000_000_000),
            Item(DiskCacheId.PnpmStore, 900_000_000),
            Item(DiskCacheId.DockerImages, 3_600_000_000, reclaimable: 400_000_000),
            Item(DiskCacheId.DockerVolumes, 12_000_000_000, cleanable: false),
            Item(DiskCacheId.DotnetSdks, 1_000_000_000, cleanable: false) with
            {
                Parts = [new DiskPart { Name = "10.0.100", Bytes = 300 }, new DiskPart { Name = "9.0.100", Bytes = 300 }, new DiskPart { Name = "8.0.404", Bytes = 400 }],
            },
            new DiskItem { Id = "SomethingNew", Bytes = 5, MeasuredAt = Now },
        ],
    };

    private static DevServer Server(int pid, string repo, int port, double daysAgo = 3, string name = "dotnet") => new()
    {
        Pid = pid, Name = name, Command = $"{name} run --urls http://localhost:{port}", StartedAt = Now.AddDays(-daysAgo), Cwd = repo, Ports = [port], RepoPath = repo,
    };

    /// <summary>mac (0.8.0, actions on): the disk report, widget on :5001 for three days, a stray node on :3000. desk (0.8.0): akin on :5001 for ten days.</summary>
    private static MachineSnapshot Mac()
    {
        var snapshot = HubFactory.Snapshot("mac", Now, actions: true, version: "0.8.0", refresh: true);
        var widget = snapshot.Entries[0];
        return snapshot with
        {
            Machine = snapshot.Machine with
            {
                AcceptsCleanCache = true,
                AcceptsStopDevServer = true,
                Disk = Report,
                DevServers = new DevServerReport
                {
                    CollectedAt = Now,
                    Servers = [Server(4242, "/dev/widget", 5001), new DevServer { Pid = 77, Name = "node", Command = "node /tmp/x.js", StartedAt = Now.AddHours(-2), Cwd = "/tmp", Ports = [3000] }],
                },
            },
            Entries =
            [
                widget with
                {
                    Pins = [new ToolchainPin { Tool = ToolNames.Dotnet, Version = "8.0.100", Source = "global.json", RollForward = "latestFeature" }],
                    Size = new SizeInfo { TotalBytes = 900, ReclaimableBytes = 700, ReclaimableDirs = [new ReclaimableDir { Path = "/dev/widget/node_modules", Bytes = 700 }], MeasuredAt = Now },
                },
                .. snapshot.Entries.Skip(1),
            ],
        };
    }

    private static MachineSnapshot Desk()
    {
        var snapshot = HubFactory.Snapshot("desk", Now, actions: true, version: "0.8.0");
        var akin = snapshot.Entries[0] with
        {
            Name = "akin", Path = "/dev/akin",
            Git = snapshot.Entries[0].Git! with { Remotes = [new RemoteInfo { Name = "origin", Url = "https://github.com/acme/akin.git", Normalized = "github.com/acme/akin" }], CommonDir = "/dev/akin/.git" },
        };
        return snapshot with
        {
            Machine = snapshot.Machine with { AcceptsStopDevServer = true, DevServers = new DevServerReport { CollectedAt = Now, Servers = [Server(9000, "/dev/akin", 5001, daysAgo: 10)] } },
            Entries = [akin],
        };
    }

    private static async Task<HubFactory> SeededAsync(params MachineSnapshot[] snapshots)
    {
        var factory = new HubFactory();
        var store = factory.Services.GetRequiredService<SnapshotStore>();
        foreach (var snapshot in snapshots.Length == 0 ? new[] { Mac(), Desk() } : snapshots)
        {
            await store.IngestAsync(snapshot);
        }

        return factory;
    }

    private static async Task<string> GetAsync(HubFactory factory, string url)
    {
        var response = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private static MachineView View(MachineSnapshot snapshot) => new(snapshot, Now, Now);

    [Fact]
    public void The_disk_view_knows_what_each_clean_frees_and_which_sdks_nothing_uses()
    {
        var disk = DiskView.For(View(Mac()));

        Assert.True(disk.Reported);
        var rows = disk.Rows.ToDictionary(r => r.Item.Id);
        Assert.Equal(8_000_000_000, rows["NuGetGlobalPackages"].Reclaimable); // cleared: all of it
        Assert.Null(rows["PnpmStore"].Reclaimable);                            // pruned: unknown
        Assert.Equal(400_000_000, rows["DockerImages"].Reclaimable);           // what Docker says the dangling ones are
        Assert.False(rows["DockerVolumes"].CanClean);
        Assert.Null(rows["DockerVolumes"].Action);
        Assert.Null(rows["SomethingNew"].Info);
        Assert.Equal("SomethingNew", rows["SomethingNew"].Title);
        Assert.Equal(8_400_000_000, disk.Reclaimable);
        Assert.Equal(AgentAction.CleanCache(DiskCacheId.NuGetGlobalPackages), rows["NuGetGlobalPackages"].Action);

        // widget's global.json (8.0.100, latestFeature) resolves to 8.0.404; the newest serves everything unpinned; 9.0.100 serves nothing.
        Assert.Equal([("10.0.100", false), ("9.0.100", true), ("8.0.404", false)], disk.Sdks.Select(s => (s.Version, s.Unused)));
        Assert.Equal(["widget"], disk.Sdks.Single(s => s.Version == "8.0.404").UsedBy);

        Assert.Equal(700, disk.BuildOutputTotal);
        Assert.Equal("github.com/acme/widget", Assert.Single(disk.BuildOutput).RepoKey);
    }

    [Fact]
    public async Task The_disk_page_lists_caches_with_clean_buttons_only_where_there_is_a_recipe()
    {
        var old = HubFactory.Snapshot("old", Now, actions: true, version: "0.7.0");
        using var factory = await SeededAsync(Mac(), old);

        var html = await GetAsync(factory, "/disk");

        Assert.Contains("NuGet packages", html);
        Assert.Contains("Docker volumes", html);
        Assert.Contains("Report only: volumes hold data", html);
        Assert.Contains("not regenerable", html);
        Assert.Contains("reclaimable", html);
        Assert.Equal(3, html.Split(">Clean</button>").Length - 1); // NuGet, pnpm, Docker images; never volumes or SDKs
        Assert.Contains("unused", html);
        Assert.Contains("Build output in repos", html);
        Assert.Contains("Update the agent on old-host to 0.8.0 or newer", html);
        Assert.Contains("href=\"disk\"", await GetAsync(factory, "/"));
        Assert.Contains(Palette.Pages, p => p.Url == "disk");
    }

    [Fact]
    public void Servers_sharing_a_port_across_repos_clash_and_long_running_ones_are_old()
    {
        var rows = DevServerView.Rows([View(Mac()), View(Desk())], Now);

        var widget = rows.Single(r => r.RepoName == "widget");
        var akin = rows.Single(r => r.RepoName == "akin");
        var stray = rows.Single(r => r.RepoKey is null);
        Assert.Equal(["akin"], widget.Clashes.Select(c => c.RepoName));
        Assert.Contains("port 5001 is also used by akin on desk-host", DevServerView.ClashText(widget));
        Assert.Empty(stray.Clashes);
        Assert.False(widget.Old);
        Assert.True(akin.Old);
        Assert.Equal("widget — dotnet on :5001, 3 days", DevServerView.Line(widget, Now));
        Assert.Equal("github.com/acme/widget", widget.RepoKey);
        Assert.Null(stray.StopAction); // not in a repo: never stoppable from here
        Assert.Equal(AgentAction.StopDevServer("/dev/widget", 4242, widget.Server.StartedAt!.Value, widget.Server.Command!), widget.StopAction);

        // The same repo on its usual port on two machines is not a clash.
        var twin = Desk() with { Machine = Desk().Machine with { DevServers = new DevServerReport { Servers = [Server(9000, "/dev/akin", 5001)] } } };
        var widgetTwin = twin with { Entries = [Mac().Entries[0]], Machine = twin.Machine with { DevServers = new DevServerReport { Servers = [Server(9000, "/dev/widget", 5001)] } } };
        Assert.All(DevServerView.Rows([View(Mac()), View(widgetTwin)], Now).Where(r => r.RepoKey is not null), r => Assert.Empty(r.Clashes));
    }

    [Fact]
    public async Task Overview_repo_and_machines_pages_show_whats_running()
    {
        using var factory = await SeededAsync();

        var overview = await GetAsync(factory, "/");
        Assert.Contains("Running", overview);
        Assert.Contains("widget — dotnet on :5001, 3 days", overview);
        Assert.Contains("port clash", overview);
        Assert.DoesNotContain("node /tmp/x.js", overview); // tools outside repos only on the Machines page

        var repo = await GetAsync(factory, "/repo?key=github.com%2Facme%2Fwidget");
        Assert.Contains("id=\"running\"", repo);
        Assert.Contains("dotnet run --urls http://localhost:5001", repo);
        Assert.Contains(">Stop</button>", repo);

        var machines = await GetAsync(factory, "/machines");
        Assert.Contains("Dev servers", machines);
        Assert.Contains("node /tmp/x.js", machines);
        Assert.Contains("outside your repos", machines);
    }

    [Fact]
    public async Task Cleaning_is_queued_only_for_a_cache_the_machine_reported_as_cleanable()
    {
        using var factory = await SeededAsync();
        var queue = factory.Services.GetRequiredService<ActionQueue>();

        var ok = await queue.QueueAsync("mac", AgentAction.CleanCache(DiskCacheId.NuGetGlobalPackages), "me@example.com");
        Assert.True(ok.Ok, ok.Error);
        Assert.Equal("dotnet nuget locals global-packages --clear", ok.Action!.CommandLine);
        Assert.Equal("", ok.Action.RepoPath);

        Assert.Contains("does not report Docker volumes", (await queue.QueueAsync("mac", AgentAction.CleanCache(DiskCacheId.DockerVolumes), "me")).Error);
        Assert.Contains("does not report npm cache", (await queue.QueueAsync("mac", AgentAction.CleanCache(DiskCacheId.NpmCache), "me")).Error);
        Assert.Contains("not a cache", (await queue.QueueAsync("mac", new AgentAction { Kind = AgentActionKind.CleanCache, CacheId = "Everything" }, "me")).Error);
    }

    [Fact]
    public async Task An_agent_before_0_8_0_is_never_sent_the_new_kinds()
    {
        var old = HubFactory.Snapshot("old", Now, actions: true, version: "0.7.0");
        old = old with { Machine = old.Machine with { Disk = Report, DevServers = new DevServerReport { Servers = [Server(4242, "/dev/widget", 5001)] } } };
        using var factory = await SeededAsync(old);
        var queue = factory.Services.GetRequiredService<ActionQueue>();

        Assert.Contains("0.8.0", (await queue.QueueAsync("old", AgentAction.CleanCache(DiskCacheId.NuGetGlobalPackages), "me")).Error);
        Assert.Contains("0.8.0", (await queue.QueueAsync("old", AgentAction.StopDevServer("/dev/widget", 4242, Now.AddDays(-3), "dotnet run --urls http://localhost:5001"), "me")).Error);
    }

    [Fact]
    public async Task A_stop_is_queued_only_for_the_server_as_it_was_reported_and_reaches_the_agent_intact()
    {
        using var factory = new HubFactory();
        var agent = factory.CreateAgentClient(await factory.IssueKeyAsync("mac"));
        (await agent.PostAsync("/api/v1/snapshots", JsonContent.Create(Mac(), options: SnapshotSchema.JsonOptions))).EnsureSuccessStatusCode();
        var queue = factory.Services.GetRequiredService<ActionQueue>();
        var server = Mac().Machine.DevServers!.Servers[0];

        Assert.Contains("no longer reports that server", (await queue.QueueAsync("mac", AgentAction.StopDevServer("/dev/widget", 4242, server.StartedAt!.Value.AddMinutes(-5), server.Command!), "me")).Error);
        Assert.Contains("no longer reports that server", (await queue.QueueAsync("mac", AgentAction.StopDevServer("/dev/widget", 4243, server.StartedAt.Value, server.Command!), "me")).Error);
        Assert.Contains("no longer reports a repository", (await queue.QueueAsync("mac", AgentAction.StopDevServer("/tmp", 77, Now.AddHours(-2), "node /tmp/x.js"), "me")).Error);

        var stop = AgentAction.StopDevServer("/dev/widget", 4242, server.StartedAt.Value, server.Command!);
        var queued = await queue.QueueAsync("mac", stop, "me@example.com");
        Assert.True(queued.Ok, queued.Error);
        Assert.Equal("kill -TERM 4242", queued.Action!.CommandLine);

        var claim = await agent.PostAsync("/api/v1/actions/claim", null);
        claim.EnsureSuccessStatusCode();
        var handed = Assert.Single((await claim.Content.ReadFromJsonAsync<ActionClaimResponse>(SnapshotSchema.JsonOptions))!.Actions).Action;
        Assert.Equal(AgentActions.Key(stop), AgentActions.Key(handed));
        Assert.Equal(AgentActionStatus.Claimed, (await queue.RecentAsync(refreshes: false, 10)).Single().Status);
    }
}
