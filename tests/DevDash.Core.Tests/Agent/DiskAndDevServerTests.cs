using System.Diagnostics;
using DevDash.Agent;
using DevDash.Agent.Actions;
using DevDash.Agent.DevServers;
using DevDash.Agent.Disk;
using DevDash.Agent.Git;
using DevDash.Agent.Toolchains;
using DevDash.Contracts;
using DevDash.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevDash.Core.Tests.Agent;

/// <summary>
/// §21 on the agent: the parsers on captured output, the disk collector and the cleaner against a made-up machine
/// and <see cref="FakeTools"/>, and the dev server list and the stop against a fake process table. Nothing here
/// measures a real cache, cleans anything real, lists real processes or signals one.
/// </summary>
public sealed class DiskParserTests
{
    [Fact]
    public void Nuget_locals_are_read_by_kind_with_or_without_the_info_prefix()
    {
        const string current = "http-cache: /Users/me/.local/share/NuGet/http-cache\nglobal-packages: /Users/me/.nuget/packages/\ntemp: /var/folders/xx/T/NuGetScratch\nplugins-cache: /Users/me/.local/share/NuGet/plugin-cache\n";
        const string older = "info : http-cache: C:\\Users\\me\\AppData\\Local\\NuGet\\v3-cache\r\ninfo : global-packages: C:\\Users\\me\\.nuget\\packages\\\r\n";

        var locals = DiskParsers.NuGetLocals(current);
        Assert.Equal("/Users/me/.nuget/packages", locals["global-packages"]);
        Assert.Equal("/Users/me/.local/share/NuGet/http-cache", locals["http-cache"]);
        Assert.Equal(4, locals.Count);
        Assert.Equal(@"C:\Users\me\.nuget\packages", DiskParsers.NuGetLocals(older)["global-packages"]);
    }

    [Fact]
    public void A_single_path_answer_is_the_last_absolute_line()
    {
        Assert.Equal("/Users/me/.npm", DiskParsers.SinglePath("/Users/me/.npm\n"));
        Assert.Equal("/Users/me/Library/pnpm/store/v11", DiskParsers.SinglePath("WARN something\n/Users/me/Library/pnpm/store/v11/\n"));
        Assert.Equal(@"C:\Users\me\AppData\Local\npm-cache", DiskParsers.SinglePath("C:\\Users\\me\\AppData\\Local\\npm-cache\r\n"));
        Assert.Null(DiskParsers.SinglePath("undefined\n"));
        Assert.Null(DiskParsers.SinglePath(""));
    }

    [Fact]
    public void Docker_df_json_lines_are_read_with_its_decimal_sizes()
    {
        // Captured from `docker system df --format json` (Docker 29).
        const string df = """
            {"Active":"3","Reclaimable":"2.289GB (63%)","Size":"3.629GB","TotalCount":"17","Type":"Images"}
            {"Active":"3","Reclaimable":"0B (0%)","Size":"127kB","TotalCount":"3","Type":"Containers"}
            {"Active":"2","Reclaimable":"12.59GB (99%)","Size":"12.59GB","TotalCount":"37","Type":"Local Volumes"}
            {"Active":"0","Reclaimable":"2.516GB","Size":"4.463GB","TotalCount":"110","Type":"Build Cache"}
            """;

        var rows = DiskParsers.DockerDf(df);
        Assert.Equal(["Images", "Containers", "Local Volumes", "Build Cache"], rows.Select(r => r.Type));
        Assert.Equal(new DockerDfRow("Images", 17, 3, 3_629_000_000, 2_289_000_000), rows[0]);
        Assert.Equal(127_000, rows[1].Size);
        Assert.Equal(0, rows[1].Reclaimable);
        Assert.Equal(2_516_000_000, rows[3].Reclaimable);
        Assert.Empty(DiskParsers.DockerDf("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?"));
    }

    [Theory]
    [InlineData("0B", 0L)]
    [InlineData("417MB", 417_000_000L)]
    [InlineData("1.5kB", 1_500L)]
    [InlineData("2GiB", 2L * 1024 * 1024 * 1024)]
    [InlineData("12.59GB (99%)", 12_590_000_000L)]
    public void Docker_sizes(string text, long bytes) => Assert.Equal(bytes, DiskParsers.DockerSize(text));

    [Fact]
    public void Dangling_images_are_counted_and_summed()
    {
        const string output = """
            {"Containers":"0","CreatedAt":"2026-11-20 06:41:13 +0000 GMT","ID":"eaeb9cd264e3","Repository":"\u003cnone\u003e","Size":"417MB","Tag":"\u003cnone\u003e"}
            {"Containers":"0","CreatedAt":"2026-11-20 05:40:04 +0000 GMT","ID":"9b74c612cc67","Repository":"\u003cnone\u003e","Size":"416MB","Tag":"\u003cnone\u003e"}
            """;
        Assert.Equal((2, 833_000_000L), DiskParsers.DanglingImages(output));
        Assert.Equal((0, 0L), DiskParsers.DanglingImages(""));
    }
}

public sealed class ListenerParserTests
{
    // Captured from `lsof -nP -iTCP -sTCP:LISTEN -FpcnT` on macOS (T lines kept: the parser ignores them).
    private const string Lsof = """
        p1005
        crapportd
        f11
        n*:49153
        TST=LISTEN
        TQR=0
        f12
        n*:49153
        TST=LISTEN
        p4242
        cnode
        f23
        n127.0.0.1:5173
        TST=LISTEN
        f24
        n[::1]:5173
        p5151
        cdotnet
        f30
        n*:5001
        f31
        n*:5000
        """;

    [Fact]
    public void Lsof_field_output_gives_each_process_its_ports()
    {
        var listeners = ListenerParsers.Lsof(Lsof);
        Assert.Equal([(1005, "rapportd", 49153), (1005, "rapportd", 49153), (4242, "node", 5173), (4242, "node", 5173), (5151, "dotnet", 5001), (5151, "dotnet", 5000)],
            listeners.Select(l => (l.Pid, l.Name!, l.Port)));
    }

    [Fact]
    public void Lsof_cwd_output_gives_each_process_its_folder()
    {
        var cwd = ListenerParsers.LsofCwd("p4242\nfcwd\nn/Users/me/Developer/akin/web\np1005\nfcwd\nn/\n");
        Assert.Equal("/Users/me/Developer/akin/web", cwd[4242]);
        Assert.Equal("/", cwd[1005]);
    }

    [Fact]
    public void Ps_lstart_is_read_in_the_machines_zone_and_the_command_kept_whole()
    {
        // Captured from `LC_ALL=C ps -o pid=,lstart=,command= -p 1673,6269`, plus a one-digit day.
        const string ps = """
             1673 Mon Nov 16 07:32:33 2026     /opt/homebrew/opt/syncthing/bin/syncthing --no-browser --no-restart
             6269 Thu Nov 19 15:07:45 2026     /Applications/Docker.app/Contents/MacOS/com.docker.backend services
             4242 Sun Nov  1 09:00:00 2026     node /Users/me/Developer/akin/node_modules/.bin/vite --port 5173
            """;
        var rows = ListenerParsers.Ps(ps, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 11, 16, 7, 32, 33, TimeSpan.Zero), rows[1673].Started);
        Assert.Equal("/opt/homebrew/opt/syncthing/bin/syncthing --no-browser --no-restart", rows[1673].Command);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), rows[4242].Started);
        Assert.Equal("node /Users/me/Developer/akin/node_modules/.bin/vite --port 5173", rows[4242].Command);

        var zone = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 5, 32, 33, TimeSpan.Zero), ListenerParsers.Ps(ps, zone)[1673].Started);
    }

    [Fact]
    public void Proc_net_tcp_gives_listening_inodes_and_ports_only()
    {
        const string tcp = """
              sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 34567 1 0000000000000000 100 0 0 10 0
               1: 0100007F:A4C2 0100007F:1F90 01 00000000:00000000 00:00000000 00000000  1000        0 34999 1 0000000000000000 20 4 30 10 -1
               2: 00000000:0016 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 0 1 0000000000000000 100 0 0 10 0
            """;
        const string tcp6 = """
              sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 00000000000000000000000000000000:1389 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 45678 1 0000000000000000 100 0 0 10 0
            """;

        Assert.Equal(new Dictionary<long, int> { [34567] = 8080 }, ListenerParsers.ProcNetTcp(tcp));
        Assert.Equal(new Dictionary<long, int> { [45678] = 5001 }, ListenerParsers.ProcNetTcp(tcp6));
        Assert.Equal(34567, ListenerParsers.SocketInode("socket:[34567]"));
        Assert.Null(ListenerParsers.SocketInode("pipe:[123]"));
        Assert.Null(ListenerParsers.SocketInode("/dev/null"));
    }

    [Fact]
    public void Proc_stat_start_counts_fields_from_the_last_parenthesis()
    {
        Assert.Equal(1_763_600_000, ListenerParsers.BootTime("cpu  1 2 3\nbtime 1763600000\nprocesses 99\n"));
        const string stat = "4242 (node (dev) server) S 1 4242 4242 0 -1 4194304 1234 0 0 0 10 5 0 0 20 0 11 0 123456 1000 50 18446744073709551615";
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_763_600_000).AddSeconds(1234.56), ListenerParsers.ProcStart(stat, 1_763_600_000));
        Assert.Null(ListenerParsers.ProcStart("garbage", 1));
        Assert.Equal(["node", "server.js", "--port", "3000"], ListenerParsers.ProcCmdline("node\0server.js\0--port\0" + "3000\0"));
    }

    [Fact]
    public void Netstat_rows_count_when_the_remote_is_the_any_address_whatever_the_state_is_called()
    {
        const string netstat = """

            Active Connections

              Proto  Local Address          Foreign Address        State           PID
              TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1040
              TCP    127.0.0.1:5001         0.0.0.0:0              ABHÖREN         4242
              TCP    192.168.1.5:50000      20.1.2.3:443           ESTABLISHED     5000
              TCP    [::]:5001              [::]:0                 LISTENING       4242
            """;
        Assert.Equal([(1040, 135), (4242, 5001), (4242, 5001)], ListenerParsers.Netstat(netstat).Select(l => (l.Pid, l.Port)));
    }

    [Fact]
    public void The_windows_script_output_is_read_with_lists_or_single_objects()
    {
        const string json = """
            {"Listen":[{"Port":5001,"Pid":4242},{"Port":5002,"Pid":4242},{"Port":135,"Pid":1040}],
             "Processes":[{"Pid":4242,"Name":"dotnet.exe","CommandLine":"\"C:\\Program Files\\dotnet\\dotnet.exe\" run --project C:\\Users\\me\\Developer\\akin","Path":"C:\\Program Files\\dotnet\\dotnet.exe","Started":"2026-11-20T09:00:00.0000000Z"},
                          {"Pid":1040,"Name":"svchost.exe","CommandLine":null,"Path":null,"Started":null}]}
            """;
        var processes = ListenerParsers.WindowsJson(json);
        var dotnet = Assert.Single(processes, p => p.Pid == 4242);
        Assert.Equal("dotnet", dotnet.Name);
        Assert.Equal([5001, 5002], dotnet.Ports);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero), dotnet.StartedAt);
        Assert.Null(dotnet.Cwd);
        Assert.Contains(@"C:\Users\me\Developer\akin", dotnet.Args);
        Assert.Null(processes.Single(p => p.Pid == 1040).StartedAt);

        var single = ListenerParsers.WindowsJson("""{"Listen":{"Port":3000,"Pid":7},"Processes":{"Pid":7,"Name":"node.exe","CommandLine":"node server.js","Started":"2026-11-20T09:00:00Z"}}""");
        Assert.Equal([3000], Assert.Single(single).Ports);
        Assert.Equal(7, ListenerParsers.WindowsProcessJson("""[{"Pid":7,"Name":"node.exe","CommandLine":"node server.js"}]""")!.Pid);
        Assert.Null(ListenerParsers.WindowsProcessJson(""));
        Assert.Empty(ListenerParsers.WindowsJson("not json"));
    }
}

public sealed class DevServerTextTests
{
    [Fact]
    public void Secrets_on_a_command_line_are_masked()
    {
        Assert.Equal("node server.js --token *** --port 3000", DevServerText.Command(["node", "server.js", "--token", "abc123", "--port", "3000"]));
        Assert.Equal("app --password=*** --db-password=*** API_KEY=***", DevServerText.Command(["app", "--password=hunter2", "--db-password=x", "API_KEY=sk-1"]));
        Assert.Equal("git clone https://me:***@example.com/r.git", DevServerText.Command("git clone https://me:s3cret@example.com/r.git"));
        Assert.Equal("dotnet run --project /dev/akin", DevServerText.Command("dotnet   run --project /dev/akin"));
    }

    [Fact]
    public void A_long_command_line_is_cut_and_the_same_on_both_sides()
    {
        var args = Enumerable.Range(0, 200).Select(i => "--flag" + i).ToList();
        var text = DevServerText.Command(args);
        Assert.Equal(DevServerText.MaxCommandChars, text.Length);
        Assert.EndsWith("…", text);
        Assert.Equal(text, DevServerText.Command(string.Join(' ', args)));
    }

    [Fact]
    public void Start_times_within_two_seconds_are_the_same_process()
    {
        var at = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        Assert.True(DevServerText.SameStart(at, at.AddMilliseconds(900)));
        Assert.True(DevServerText.SameStart(at, at.AddSeconds(-2)));
        Assert.False(DevServerText.SameStart(at, at.AddSeconds(3)));
    }
}

/// <summary>Stands in for the operating system's process table.</summary>
internal sealed class FakeProcesses : IProcessProbe, IProcessSignal
{
    public List<ListeningProcess> Processes { get; } = [];

    public List<int> Signalled { get; } = [];

    /// <summary>Whether a signalled process goes away.</summary>
    public bool ExitOnSignal { get; set; } = true;

    public string? Problem { get; set; }

    public Task<ListenResult> ListAsync(CancellationToken ct) => Task.FromResult(new ListenResult(Processes.Where(p => p.Ports.Count > 0).ToList(), Problem));

    public Task<ListeningProcess?> InspectAsync(int pid, CancellationToken ct) => Task.FromResult(Processes.FirstOrDefault(p => p.Pid == pid));

    public string Name => "SIGTERM";

    public string? Terminate(int pid)
    {
        Signalled.Add(pid);
        if (ExitOnSignal)
        {
            Processes.RemoveAll(p => p.Pid == pid);
        }

        return null;
    }
}

public sealed class DevServerCollectorTests
{
    private static readonly DateTimeOffset Started = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static DevEntry Repo(string path, params DevEntry[] children) => new()
    {
        Root = "/dev", Name = Path.GetFileName(path), Path = path, Kind = children.Length > 0 ? EntryKind.WorktreeContainer : EntryKind.GitRepo, Children = children,
    };

    private static ListeningProcess Process(int pid, string name, string? cwd, string args, params int[] ports) =>
        new(pid, name, args.Split(' ', StringSplitOptions.RemoveEmptyEntries), null, cwd, Started, ports);

    [Fact]
    public void A_process_is_matched_to_the_deepest_working_tree_by_its_folder_then_by_paths_on_its_command_line()
    {
        string[] repos = ["/dev/akin", "/dev/akin.worktrees/feature", "/dev/widget"];

        Assert.Equal("/dev/akin", DevServerCollector.RepoFor(Process(1, "node", "/dev/akin/web", "node vite"), repos, caseSensitive: true));
        Assert.Equal("/dev/akin.worktrees/feature", DevServerCollector.RepoFor(Process(2, "node", "/dev/akin.worktrees/feature", "node"), repos, caseSensitive: true));
        Assert.Equal("/dev/widget", DevServerCollector.RepoFor(Process(3, "dotnet", "/", "dotnet run --project=/dev/widget/src/Api"), repos, caseSensitive: true));
        Assert.Equal("/dev/widget", DevServerCollector.RepoFor(Process(4, "Api", null, "\"/dev/widget/bin/Debug/Api\""), repos, caseSensitive: true));
        Assert.Null(DevServerCollector.RepoFor(Process(5, "node", "/dev/akinX", "node /dev/akinX/server.js"), repos, caseSensitive: true));
        Assert.Null(DevServerCollector.RepoFor(Process(6, "node", "/DEV/AKIN", "node"), repos, caseSensitive: true));
        Assert.Equal("/dev/akin", DevServerCollector.RepoFor(Process(6, "node", "/DEV/AKIN", "node"), repos, caseSensitive: false));
        Assert.Equal(@"C:\dev\akin", DevServerCollector.RepoFor(new ListeningProcess(7, "dotnet", ["dotnet", "run", "--project", @"C:\dev\akin\src"], null, null, Started, [5001]), [@"C:\dev\akin"], caseSensitive: false));
    }

    [Fact]
    public void Coding_agents_and_editors_working_in_a_repo_are_not_dev_servers()
    {
        // Claude Code's native binary is named after its version; it runs in the repo and listens on localhost.
        var claude = new ListeningProcess(60890, "2.1.329", ["claude", "--output-format", "stream-json"], "/Users/x/.local/share/claude/versions/2.1.329", "/dev/widget", Started, [51485]);
        Assert.True(DevServerCollector.IsIgnored(claude));
        Assert.True(DevServerCollector.IsIgnored(new ListeningProcess(1, "node", ["node", "/usr/lib/node_modules/@anthropic-ai/claude-code/cli.js"], null, "/dev/widget", Started, [4000])));
        Assert.True(DevServerCollector.IsIgnored(new ListeningProcess(2, "codex", ["codex"], null, "/dev/widget", Started, [4001])));
        Assert.True(DevServerCollector.IsIgnored(new ListeningProcess(3, "code-6904", ["code-6904"], null, "/dev/widget", Started, [58038])));
        Assert.True(DevServerCollector.IsIgnored(new ListeningProcess(4, "Codex.exe", [@"C:\tools\codex.exe"], null, null, Started, [4002])));
        Assert.True(DevServerCollector.IsIgnored(new ListeningProcess(5, "mytool", ["mytool"], null, null, Started, [1]), new HashSet<string>(["mytool"])));

        Assert.False(DevServerCollector.IsIgnored(new ListeningProcess(6, "node", ["node", "server.js"], null, "/dev/widget", Started, [5173])));
        Assert.False(DevServerCollector.IsIgnored(new ListeningProcess(7, "dotnet", ["dotnet", "run", "--project", "/dev/widget/src/codex-api"], null, "/dev/widget", Started, [5001])));

        Assert.Equal("claude", DevServerCollector.DisplayName(claude));
        Assert.Equal("node", DevServerCollector.DisplayName(new ListeningProcess(6, "node", ["node", "server.js"], null, null, Started, [5173])));
    }

    [Fact]
    public async Task Only_processes_in_repos_and_known_dev_tools_are_reported()
    {
        var fake = new FakeProcesses();
        fake.Processes.AddRange(
        [
            Process(1005, "rapportd", "/", "/usr/libexec/rapportd", 49153),
            Process(1111, "ControlCenter", "/", "/System/Library/CoreServices/ControlCenter.app/Contents/MacOS/ControlCenter", 5000, 7000),
            Process(4242, "node", "/dev/akin/web", "node /dev/akin/web/node_modules/.bin/vite --port 5173", 5173),
            Process(5151, "Akin", "/dev/akin", "/dev/akin/bin/Debug/net10.0/Akin --token hunter2", 5001),
            Process(6000, "python3", "/tmp", "python3 -m http.server 8000", 8000),
            Process(7000, "python3.12", "/tmp", "/usr/bin/python3.12 -m http.server 8001", 8001),
            Process(Environment.ProcessId, "testhost", "/dev/akin", "testhost", 9999),
        ]);
        var options = new AgentOptions();
        var collector = new DevServerCollector(fake, new TestOptions(options), NullLogger<DevServerCollector>.Instance, new TestClock(Started.AddDays(3)));

        var report = await collector.CollectAsync([Repo("/dev/akin")], CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(Started.AddDays(3), report.CollectedAt);
        // In repos first (by port), then the tools outside them.
        Assert.Equal([5151, 4242, 6000, 7000], report.Servers.Select(s => s.Pid));
        Assert.Equal(["/dev/akin", "/dev/akin", null, null], report.Servers.Select(s => s.RepoPath));
        Assert.Equal("/dev/akin/bin/Debug/net10.0/Akin --token ***", report.Servers[0].Command);
        Assert.Equal([5001], report.Servers[0].Ports);
        Assert.Equal(Started, report.Servers[0].StartedAt);

        options.DevServers.MaxServers = 1;
        Assert.Equal([5151], (await collector.CollectAsync([Repo("/dev/akin")], CancellationToken.None))!.Servers.Select(s => s.Pid));

        options.DevServers.Enabled = false;
        Assert.Null(await collector.CollectAsync([Repo("/dev/akin")], CancellationToken.None));
    }

    [Fact]
    public async Task Every_push_carries_the_kept_disk_report_and_a_fresh_server_list()
    {
        var dir = Directory.CreateTempSubdirectory("devdash-facts-").FullName;
        try
        {
            var file = Path.Combine(dir, "agent-disk.json");
            var kept = new DiskReport { CollectedAt = Started, Items = [new DiskItem { Id = "NpmCache", Bytes = 42, Cleanable = true }, new DiskItem { Id = "DockerImages", Bytes = 7 }] };
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(kept, SnapshotSchema.JsonOptions));
            var options = new AgentOptions();
            var monitor = new TestOptions(options);
            var fake = new FakeProcesses();
            fake.Processes.Add(Process(4242, "node", "/dev/akin", "node vite", 5173));
            var disk = new DiskService(new DiskCollector(new FakeTools(), new ToolLocator(new ToolEnvironment { Home = dir })), monitor, NullLogger<DiskService>.Instance, file);
            var git = new GitRunner();
            var scanner = new DevDash.Agent.Scanning.MachineScanner(
                new DevDash.Agent.Scanning.RepoScanner(git, new FetchService(git, monitor, NullLogger<FetchService>.Instance), monitor),
                new DevDash.Agent.Scanning.SizeCache(Path.Combine(dir, "sizes.json")), monitor, NullLogger<DevDash.Agent.Scanning.MachineScanner>.Instance,
                disk: disk, devServers: new DevServerCollector(fake, monitor, NullLogger<DevServerCollector>.Instance));

            var machine = await scanner.WithMachineFactsAsync(new MachineInfo { MachineId = "m", Hostname = "h" }, [Repo("/dev/akin")], CancellationToken.None);

            Assert.Equal([42L, 7L], machine.Disk!.Items.Select(i => i.Bytes!.Value));
            Assert.Equal("/dev/akin", Assert.Single(machine.DevServers!.Servers).RepoPath);

            options.Disk.Skip.Add("DockerImages");
            Assert.Equal(["NpmCache"], (await scanner.WithMachineFactsAsync(machine, [], CancellationToken.None)).Disk!.Items.Select(i => i.Id));
            options.Disk.Enabled = false;
            options.DevServers.Enabled = false;
            var off = await scanner.WithMachineFactsAsync(machine, [], CancellationToken.None);
            Assert.Null(off.Disk);
            Assert.Null(off.DevServers);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_listing_problem_is_reported_not_thrown()
    {
        var fake = new FakeProcesses { Problem = "lsof could not be run" };
        var report = await new DevServerCollector(fake, new TestOptions(new AgentOptions()), NullLogger<DevServerCollector>.Instance).CollectAsync([], CancellationToken.None);
        Assert.Equal("lsof could not be run", report!.Problem);
        Assert.Empty(report.Servers);
    }

    [Fact]
    public async Task The_mac_probe_joins_lsof_and_ps()
    {
        var tools = new FakeTools()
            .On("lsof", "-nP -iTCP", "p4242\ncnode\nf23\nn127.0.0.1:5173\np5151\ncAkin\nf30\nn*:5001\nf31\nn*:5000\n")
            .On("ps", "-o pid=,lstart=,command= -p 4242,5151", " 4242 Tue Nov 17 09:00:00 2026     node /dev/akin/node_modules/.bin/vite\n 5151 Tue Nov 17 10:00:00 2026     /dev/akin/bin/Akin\n")
            .On("lsof", "-a -p 4242,5151 -d cwd -Fpn", "p4242\nfcwd\nn/dev/akin\np5151\nfcwd\nn/dev/akin\n")
            .On("ps", "-o pid=,lstart=,command= -p 4242", " 4242 Tue Nov 17 09:00:00 2026     node /dev/akin/node_modules/.bin/vite\n")
            .On("lsof", "-a -p 4242 -d cwd -Fpn", "p4242\nfcwd\nn/dev/akin\n");
        var probe = new MacProbe(tools, TimeZoneInfo.Utc);

        var listed = await probe.ListAsync(CancellationToken.None);

        Assert.Null(listed.Problem);
        Assert.Equal([(4242, "node", "/dev/akin", "5173"), (5151, "Akin", "/dev/akin", "5000,5001")],
            listed.Processes.Select(p => (p.Pid, p.Name, p.Cwd!, string.Join(',', p.Ports))));
        Assert.Equal(Started, listed.Processes[0].StartedAt);
        Assert.All(tools.Calls, c => Assert.Equal("C", c.Environment["LC_ALL"]));

        var one = await probe.InspectAsync(4242, CancellationToken.None);
        Assert.Equal("node /dev/akin/node_modules/.bin/vite", DevServerText.Command(one!.Args));
        Assert.Null(await probe.InspectAsync(99, CancellationToken.None));
    }

    [Fact]
    public async Task The_mac_probe_says_when_lsof_cannot_be_run_and_lists_nothing_when_nothing_listens()
    {
        var missing = await new MacProbe(new FakeTools().On(_ => true, _ => ToolResult.NotStarted("no such file")), TimeZoneInfo.Utc).ListAsync(CancellationToken.None);
        Assert.Contains("lsof could not be run", missing.Problem);

        var none = await new MacProbe(new FakeTools().On("lsof", "-nP", "", exit: 1), TimeZoneInfo.Utc).ListAsync(CancellationToken.None);
        Assert.Null(none.Problem);
        Assert.Empty(none.Processes);
    }

    private sealed class FakeProc : IProcFs
    {
        public Dictionary<string, string> Files { get; } = [];
        public Dictionary<string, string> Links { get; } = [];

        public string? ReadText(string path) => Files.GetValueOrDefault(path);

        public IEnumerable<string> List(string path)
        {
            var prefix = path.Length == 0 ? "" : path + "/";
            return Files.Keys.Concat(Links.Keys).Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => k[prefix.Length..].Split('/')[0]).Distinct().ToList();
        }

        public string? ReadLink(string path) => Links.GetValueOrDefault(path);
    }

    [Fact]
    public async Task The_linux_probe_matches_sockets_to_processes_through_their_file_descriptors()
    {
        var proc = new FakeProc();
        proc.Files["net/tcp"] = "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n   0: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000  1000        0 34567 1 0 100 0 0 10 0\n";
        proc.Files["net/tcp6"] = "  sl  local_address remote_address st\n";
        proc.Files["stat"] = "cpu 1\nbtime 1763600000\n";
        proc.Files["4242/stat"] = "4242 (node) S 1 4242 4242 0 -1 4194304 1234 0 0 0 10 5 0 0 20 0 11 0 100 1000";
        proc.Files["4242/cmdline"] = "node\0server.js\0";
        proc.Files["4242/comm"] = "node\n";
        proc.Links["4242/fd/0"] = "/dev/null";
        proc.Links["4242/fd/21"] = "socket:[34567]";
        proc.Links["4242/cwd"] = "/home/me/dev/akin";
        proc.Links["4242/exe"] = "/usr/bin/node";
        proc.Files["5000/stat"] = "5000 (bash) S 1 5000 5000 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 50 1000";
        proc.Links["5000/fd/0"] = "socket:[11111]";

        var listed = await new LinuxProbe(proc).ListAsync(CancellationToken.None);

        var node = Assert.Single(listed.Processes);
        Assert.Equal((4242, "node", "/home/me/dev/akin", "/usr/bin/node"), (node.Pid, node.Name, node.Cwd!, node.Executable!));
        Assert.Equal([8080], node.Ports);
        Assert.Equal(["node", "server.js"], node.Args);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1763600000).AddSeconds(1), node.StartedAt);
        var inspected = await new LinuxProbe(proc).InspectAsync(4242, CancellationToken.None);
        Assert.Equal((node.Pid, node.StartedAt, node.Cwd), (inspected!.Pid, inspected.StartedAt, inspected.Cwd));
        Assert.Equal(node.Args, inspected.Args);
        Assert.Empty(inspected.Ports);
        Assert.Null(await new LinuxProbe(proc).InspectAsync(9, CancellationToken.None));
    }
}

public sealed class DiskCollectorTests : IDisposable
{
    private readonly FakeMachine _machine = new();

    public void Dispose() => _machine.Dispose();

    private string Fill(long bytes, params string[] parts)
    {
        var file = Path.Combine([_machine.Root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[bytes]);
        return file;
    }

    private const string Df = """
        {"Active":"3","Reclaimable":"2.289GB (63%)","Size":"3.629GB","TotalCount":"17","Type":"Images"}
        {"Active":"2","Reclaimable":"12.59GB (99%)","Size":"12.59GB","TotalCount":"37","Type":"Local Volumes"}
        {"Active":"0","Reclaimable":"2.516GB","Size":"4.463GB","TotalCount":"110","Type":"Build Cache"}
        """;

    /// <summary>A Mac with dotnet, npm and docker on the PATH, NuGet and npm caches, Xcode folders and two SDKs.</summary>
    private (DiskCollector Collector, FakeTools Tools) Mac(bool mac = true)
    {
        var bin = _machine.Dir("bin");
        _machine.Tool("bin", "dotnet");
        _machine.Tool("bin", "npm");
        _machine.Tool("bin", "docker");
        var packages = Path.Combine(_machine.Root, "nuget", "packages");
        var http = Path.Combine(_machine.Root, "nuget", "http-cache");
        Fill(1000, "nuget", "packages", "a", "1.0.0", "a.nupkg");
        Fill(24, "nuget", "packages", "b", "2.0.0", "b.nupkg");
        Fill(500, "nuget", "http-cache", "x.dat");
        Fill(300, "home", ".npm", "_cacache", "index");
        Fill(200, "home", "Library", "Developer", "Xcode", "DerivedData", "Akin-abc", "Build", "x.o");
        Fill(100, "home", "Library", "Developer", "Xcode", "iOS DeviceSupport", "17.0 (21A1)", "symbols");
        Fill(50, "bin", "sdk", "10.0.100", "dotnet.dll");
        Fill(70, "bin", "sdk", "8.0.404", "dotnet.dll");
        _machine.Dir("home", "Library", "Caches", "Yarn");
        var tools = new FakeTools()
            .On("dotnet", "nuget locals all --list", $"http-cache: {http}\nglobal-packages: {packages}{Path.DirectorySeparatorChar}\ntemp: /tmp/NuGetScratch\n")
            .On("npm", "config get cache", Path.Combine(_machine.Home, ".npm") + "\n")
            .On("docker", "system df", Df)
            .On("docker", "image ls --filter dangling=true", """{"ID":"a","Size":"417MB"}""");
        var collector = new DiskCollector(tools, new ToolLocator(_machine.Environment(mac: mac, path: [bin])), new TestClock(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero)));
        return (collector, tools);
    }

    [Fact]
    public async Task Every_cache_is_found_from_its_own_tool_or_place_and_measured()
    {
        var (collector, tools) = Mac();

        var report = await collector.CollectAsync([], TimeSpan.FromSeconds(30), CancellationToken.None);

        var byId = report.Items.ToDictionary(i => i.Id);
        Assert.Equal(
            [nameof(DiskCacheId.NuGetGlobalPackages), nameof(DiskCacheId.NuGetHttpCache), nameof(DiskCacheId.NpmCache), nameof(DiskCacheId.XcodeDerivedData),
             nameof(DiskCacheId.XcodeDeviceSupport), nameof(DiskCacheId.DockerImages), nameof(DiskCacheId.DockerBuildCache), nameof(DiskCacheId.DockerVolumes), nameof(DiskCacheId.DotnetSdks)],
            report.Items.Select(i => i.Id));
        Assert.Equal((1024L, true), (byId["NuGetGlobalPackages"].Bytes!.Value, byId["NuGetGlobalPackages"].Cleanable));
        Assert.Equal(Path.Combine(_machine.Root, "nuget", "packages"), byId["NuGetGlobalPackages"].Path);
        Assert.Equal(500, byId["NuGetHttpCache"].Bytes);
        Assert.Equal((300L, true), (byId["NpmCache"].Bytes!.Value, byId["NpmCache"].Cleanable));
        Assert.Equal((200L, true), (byId["XcodeDerivedData"].Bytes!.Value, byId["XcodeDerivedData"].Cleanable));
        Assert.NotNull(byId["XcodeDerivedData"].LastModified);
        Assert.Equal("17.0 (21A1)", Assert.Single(byId["XcodeDeviceSupport"].Parts).Name);
        Assert.False(byId["XcodeDeviceSupport"].Cleanable);
        Assert.Equal((3_629_000_000L, 417_000_000L, 17), (byId["DockerImages"].Bytes!.Value, byId["DockerImages"].ReclaimableBytes!.Value, byId["DockerImages"].Count!.Value));
        Assert.Equal(2_516_000_000, byId["DockerBuildCache"].ReclaimableBytes);
        Assert.False(byId["DockerVolumes"].Cleanable);
        Assert.Equal(["10.0.100", "8.0.404"], byId["DotnetSdks"].Parts.Select(p => p.Name));
        Assert.Equal(120, byId["DotnetSdks"].Bytes);
        Assert.DoesNotContain("YarnCache", byId.Keys); // empty: not worth a row

        // Read-only commands only.
        string[] writes = ["--clear", "clean", "prune", "rm "];
        Assert.DoesNotContain(tools.Lines, line => writes.Any(word => line.Contains(word, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Xcode_is_only_looked_for_on_a_mac_and_skipped_ids_are_left_out()
    {
        var (collector, _) = Mac(mac: false);
        var report = await collector.CollectAsync([nameof(DiskCacheId.DockerImages), nameof(DiskCacheId.DockerVolumes), nameof(DiskCacheId.DockerBuildCache)], TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.DoesNotContain(report.Items, i => i.Id.StartsWith("Xcode", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Items, i => i.Id.StartsWith("Docker", StringComparison.Ordinal));
        Assert.Contains(report.Items, i => i.Id == nameof(DiskCacheId.NpmCache));
    }

    [Fact]
    public async Task Docker_that_does_not_answer_is_one_row_with_a_problem()
    {
        _machine.Tool("bin", "docker");
        var tools = new FakeTools().On("docker", "system df", "", exit: 1, stderr: "Cannot connect to the Docker daemon");
        var collector = new DiskCollector(tools, new ToolLocator(_machine.Environment(path: [Path.Combine(_machine.Root, "bin")])));

        var item = await collector.CollectItemAsync(DiskCacheId.DockerBuildCache, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Null(item); // the problem row is the images row
        var images = await collector.CollectItemAsync(DiskCacheId.DockerImages, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Contains("did not answer", images!.Problem);
        Assert.False(images.Cleanable);
    }

    [Fact]
    public void A_measurement_stopped_by_its_time_limit_says_it_is_partial_and_links_are_not_followed()
    {
        Fill(10, "walk", "a.bin");
        Fill(20, "walk", "sub", "b.bin");
        var walk = Path.Combine(_machine.Root, "walk");
        if (!OperatingSystem.IsWindows())
        {
            Fill(5000, "elsewhere", "big.bin");
            Directory.CreateSymbolicLink(Path.Combine(walk, "link"), Path.Combine(_machine.Root, "elsewhere"));
        }

        var full = FolderMeasure.Measure(walk, CancellationToken.None);
        Assert.Equal((30L, true), (full.Bytes, full.Complete));

        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        Assert.False(FolderMeasure.Measure(walk, stopped.Token).Complete);
    }
}

public sealed class DiskCleanerTests : IDisposable
{
    private readonly FakeMachine _machine = new();
    private readonly AgentOptions _options = new() { Actions = new ActionsOptions { Enabled = true } };

    public void Dispose() => _machine.Dispose();

    private sealed class RecordingTrash : ITrash
    {
        public List<string> Moved { get; } = [];

        public string Name => "the Trash";

        public TrashResult Move(string path)
        {
            Moved.Add(path);
            Directory.Delete(path, recursive: true);
            return TrashResult.Moved("/trash/" + Path.GetFileName(path));
        }
    }

    private (DiskCleaner Cleaner, FakeTools Tools, RecordingTrash Trash, DiskService Disk) Build(bool mac = true)
    {
        var bin = _machine.Dir("bin");
        _machine.Tool("bin", "dotnet");
        _machine.Tool("bin", "docker");
        var http = _machine.Dir("nuget", "http-cache");
        File.WriteAllBytes(Path.Combine(http, "x.dat"), new byte[10]);
        var tools = new FakeTools()
            .On("dotnet", "nuget locals all --list", $"http-cache: {http}\nglobal-packages: {Path.Combine(_machine.Root, "nuget", "nope")}\n")
            .On("dotnet", "nuget locals http-cache --clear", "Clearing NuGet HTTP cache: " + http + "\nLocal resources cleared.\n")
            .On("docker", "system df", """{"Size":"1GB","Reclaimable":"500MB","TotalCount":"3","Type":"Build Cache"}""")
            .On("docker", "builder prune -f", "Total reclaimed space: 500MB\n");
        var monitor = new TestOptions(_options);
        var collector = new DiskCollector(tools, new ToolLocator(_machine.Environment(mac: mac, path: [bin])));
        var disk = new DiskService(collector, monitor, NullLogger<DiskService>.Instance, Path.Combine(_machine.Root, "agent-disk.json"));
        var trash = new RecordingTrash();
        return (new DiskCleaner(collector, disk, tools, monitor, NullLogger<DiskCleaner>.Instance, trash), tools, trash, disk);
    }

    private static Task<ActionOutcome> Run(DiskCleaner cleaner, AgentAction action) => cleaner.RunAsync(action, Stopwatch.StartNew(), CancellationToken.None);

    [Fact]
    public async Task A_cache_is_found_again_and_cleaned_with_the_agents_own_recipe()
    {
        var (cleaner, tools, _, disk) = Build();

        var outcome = await Run(cleaner, AgentAction.CleanCache(DiskCacheId.NuGetHttpCache));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.Contains("dotnet nuget locals http-cache --clear", tools.Lines);
        Assert.Contains("Local resources cleared.", outcome.Output);
        // Measured again at once: the report carries the item as it is now.
        Assert.NotNull(disk.Current);

        var docker = await Run(cleaner, AgentAction.CleanCache(DiskCacheId.DockerBuildCache));
        Assert.True(docker.Succeeded, docker.Output);
        Assert.Contains("docker builder prune -f", tools.Lines);
        Assert.DoesNotContain(tools.Lines, l => l.Contains("-a", StringComparison.Ordinal) || l.Contains("volume", StringComparison.Ordinal) || l.Contains("system prune", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Derived_data_is_emptied_into_the_trash_one_folder_at_a_time()
    {
        var (cleaner, tools, trash, _) = Build();
        var derived = _machine.Dir("home", "Library", "Developer", "Xcode", "DerivedData");
        Directory.CreateDirectory(Path.Combine(derived, "Akin-abc"));
        Directory.CreateDirectory(Path.Combine(derived, "Widget-def"));

        var outcome = await Run(cleaner, AgentAction.CleanCache(DiskCacheId.XcodeDerivedData));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.Equal(["Akin-abc", "Widget-def"], trash.Moved.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(Directory.Exists(derived)); // the folder itself stays
        Assert.DoesNotContain(tools.Lines, l => l.StartsWith("rm", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DiskCacheId.DockerVolumes, "reported only")]
    [InlineData(DiskCacheId.XcodeArchives, "reported only")]
    [InlineData(DiskCacheId.DotnetSdks, "reported only")]
    [InlineData(DiskCacheId.NuGetGlobalPackages, "not found")]
    [InlineData(DiskCacheId.NpmCache, "not found")]
    public async Task Report_only_and_missing_caches_are_refused_before_anything_runs(DiskCacheId id, string why)
    {
        var (cleaner, tools, trash, _) = Build();

        var outcome = await Run(cleaner, AgentAction.CleanCache(id));

        Assert.True(outcome.Refused);
        Assert.Contains(why, outcome.Output);
        Assert.DoesNotContain(tools.Lines, l => l.Contains("--clear", StringComparison.Ordinal) || l.Contains("clean", StringComparison.Ordinal) || l.Contains("prune", StringComparison.Ordinal));
        Assert.Empty(trash.Moved);
    }

    [Fact]
    public async Task An_unknown_id_a_mac_cache_elsewhere_and_a_machine_with_the_report_off_are_refused()
    {
        var (cleaner, _, _, _) = Build(mac: false);

        Assert.Contains("not a cache the agent knows", (await Run(cleaner, new AgentAction { Kind = AgentActionKind.CleanCache, CacheId = "rm -rf ~" })).Output);
        Assert.Contains("not a cache the agent knows", (await Run(cleaner, new AgentAction { Kind = AgentActionKind.CleanCache, CacheId = "npmcache" })).Output);
        Assert.Contains("macOS", (await Run(cleaner, AgentAction.CleanCache(DiskCacheId.XcodeDerivedData))).Output);

        _options.Disk.Skip.Add(nameof(DiskCacheId.NuGetHttpCache));
        Assert.Contains("left out", (await Run(cleaner, AgentAction.CleanCache(DiskCacheId.NuGetHttpCache))).Output);
        _options.Disk.Enabled = false;
        Assert.Contains("switched off", (await Run(cleaner, AgentAction.CleanCache(DiskCacheId.NuGetHttpCache))).Output);
    }

    [Fact]
    public async Task With_actions_off_the_executor_refuses_before_the_cleaner_is_asked()
    {
        var (cleaner, tools, _, _) = Build();
        _options.Actions.Enabled = false;
        var git = new GitRunner();
        var monitor = new TestOptions(_options);
        var executor = new ActionExecutor(git, new ActionValidator(git, monitor), monitor, NullLogger<ActionExecutor>.Instance, caches: cleaner);

        var outcome = await executor.ExecuteAsync(AgentAction.CleanCache(DiskCacheId.NuGetHttpCache), CancellationToken.None);

        Assert.True(outcome.Refused);
        Assert.Contains("not enabled", outcome.Output);
        Assert.Empty(tools.Calls);

        _options.Actions.Enabled = true;
        Assert.True((await executor.ExecuteAsync(AgentAction.CleanCache(DiskCacheId.NuGetHttpCache), CancellationToken.None)).Succeeded);
    }
}

public sealed class DevServerStopTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Directory.CreateTempSubdirectory("devdash-servers-").FullName;
    private readonly string _dev;
    private readonly string _repo;
    private readonly AgentOptions _options;
    private readonly FakeProcesses _processes = new();

    public DevServerStopTests()
    {
        _dev = Directory.CreateDirectory(Path.Combine(_root, "Developer")).FullName;
        _repo = Directory.CreateDirectory(Path.Combine(_dev, "akin")).FullName;
        Git(_repo, "init", "-q");
        _options = new AgentOptions { Roots = [_dev], Actions = new ActionsOptions { Enabled = true }, DevServers = new DevServerOptions { StopWaitSeconds = 0 } };
        _processes.Processes.Add(new ListeningProcess(4242, "node", ["node", "server.js", "--token", "abc"], null, Path.Combine(_repo, "web"), Started, [5173]));
        _processes.Processes.Add(new ListeningProcess(5000, "node", ["node", "elsewhere.js"], null, _root, Started, [3000]));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
    }

    private Task<ActionOutcome> Stop(AgentAction action)
    {
        var git = new GitRunner();
        var monitor = new TestOptions(_options);
        var stopper = new DevServerStopper(_processes, _processes, new ActionValidator(git, monitor), monitor, NullLogger<DevServerStopper>.Instance);
        return new ActionExecutor(git, new ActionValidator(git, monitor), monitor, NullLogger<ActionExecutor>.Instance, servers: stopper).ExecuteAsync(action, CancellationToken.None);
    }

    private AgentAction Shown(int pid = 4242, DateTimeOffset? started = null, string command = "node server.js --token ***", string? repo = null) =>
        AgentAction.StopDevServer(repo ?? _repo, pid, started ?? Started, command);

    [Fact]
    public async Task The_process_that_was_shown_is_sent_sigterm_and_its_repo_rescanned()
    {
        var outcome = await Stop(Shown());

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.Equal([4242], _processes.Signalled);
        Assert.Contains("SIGTERM", outcome.Output);
        Assert.Contains("stopped", outcome.Output);
        Assert.Equal([_repo], outcome.AffectedPaths);
    }

    [Fact]
    public async Task One_that_ignores_sigterm_is_reported_as_still_running_and_nothing_stronger_is_sent()
    {
        _processes.ExitOnSignal = false;

        var outcome = await Stop(Shown());

        Assert.True(outcome.Succeeded);
        Assert.Contains("still running", outcome.Output);
        Assert.Equal([4242], _processes.Signalled);
    }

    [Fact]
    public async Task A_recycled_pid_is_refused()
    {
        var outcome = await Stop(Shown(started: Started.AddMinutes(-30)));

        Assert.True(outcome.Refused);
        Assert.Contains("reused", outcome.Output);
        Assert.Empty(_processes.Signalled);
    }

    [Fact]
    public async Task A_different_command_a_gone_process_and_one_working_outside_the_repo_are_refused()
    {
        Assert.Contains("different command", (await Stop(Shown(command: "node other.js"))).Output);
        Assert.Contains("no process 77", (await Stop(Shown(pid: 77))).Output);
        Assert.Contains("not inside", (await Stop(Shown(pid: 5000, command: "node elsewhere.js"))).Output);
        Assert.Empty(_processes.Signalled);
    }

    [Fact]
    public async Task A_repo_outside_the_scan_roots_or_the_agent_itself_is_refused()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        Git(outside, "init", "-q");
        Assert.Contains("outside this agent's scan roots", (await Stop(Shown(repo: outside))).Output);

        _processes.Processes.Add(new ListeningProcess(Environment.ProcessId, "testhost", ["testhost"], null, _repo, Started, [1]));
        Assert.Contains("the agent itself", (await Stop(Shown(pid: Environment.ProcessId, command: "testhost"))).Output);
        Assert.Empty(_processes.Signalled);
    }

    [Fact]
    public async Task With_actions_or_the_list_off_nothing_is_signalled()
    {
        _options.DevServers.Enabled = false;
        Assert.Contains("switched off", (await Stop(Shown())).Output);
        _options.DevServers.Enabled = true;
        _options.Actions.Enabled = false;
        Assert.Contains("not enabled", (await Stop(Shown())).Output);
        Assert.Empty(_processes.Signalled);
    }
}

public sealed class DiskAndDevServerContractTests
{
    private static MachineInfo Machine(string version, bool actions = true, bool clean = true, bool stop = true) => new()
    {
        MachineId = "mac", Hostname = "mac-host", AgentVersion = version, ActionsEnabled = actions, AcceptsCleanCache = clean, AcceptsStopDevServer = stop,
    };

    [Fact]
    public void The_new_kinds_carry_only_their_own_fields()
    {
        var at = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        Assert.Null(AgentActions.Validate(AgentAction.CleanCache(DiskCacheId.NpmCache)));
        Assert.Null(AgentActions.Validate(AgentAction.StopDevServer("/dev/akin", 4242, at, "node server.js")));

        Assert.Contains("does not take a repository path", AgentActions.Validate(AgentAction.CleanCache(DiskCacheId.NpmCache) with { RepoPath = "/dev/akin" }));
        Assert.Contains("do not match", AgentActions.Validate(AgentAction.CleanCache(DiskCacheId.NpmCache) with { Pid = 3 }));
        Assert.Contains("do not match", AgentActions.Validate(AgentAction.Fetch("/dev/akin") with { CacheId = "NpmCache" }));
        Assert.Contains("not a cache", AgentActions.Validate(new AgentAction { Kind = AgentActionKind.CleanCache, CacheId = "../../etc" }));
        Assert.Contains("not a cache", AgentActions.Validate(new AgentAction { Kind = AgentActionKind.CleanCache, CacheId = "7" }));
        Assert.Contains("out of range", AgentActions.Validate(AgentAction.StopDevServer("/dev/akin", 1, at, "x")));
        Assert.Contains("must be absolute", AgentActions.Validate(AgentAction.StopDevServer("akin", 42, at, "x")));
        Assert.Contains("not one line", AgentActions.Validate(AgentAction.StopDevServer("/dev/akin", 42, at, "a\nb")));
        Assert.Contains("do not match", AgentActions.Validate(AgentAction.StopDevServer("/dev/akin", 42, at, "x") with { ExpectedStartedAt = null }));
    }

    [Fact]
    public void They_need_actions_on_and_an_agent_that_says_it_takes_them()
    {
        Assert.True(AgentActions.Accepts(Machine("0.8.0"), AgentActionKind.CleanCache));
        Assert.True(AgentActions.Accepts(Machine("0.8.0"), AgentActionKind.StopDevServer));
        Assert.False(AgentActions.Accepts(Machine("0.8.0", actions: false), AgentActionKind.CleanCache));

        Assert.Contains("to 0.8.0 or newer", AgentActions.WhyNotAccepted(Machine("0.7.0", clean: false, stop: false), AgentActionKind.CleanCache));
        Assert.Contains("disk report switched off", AgentActions.WhyNotAccepted(Machine("0.8.0", clean: false), AgentActionKind.CleanCache));
        Assert.Contains("dev server list switched off", AgentActions.WhyNotAccepted(Machine("0.8.0", stop: false), AgentActionKind.StopDevServer));
        Assert.Contains("--enable-actions", AgentActions.WhyNotAccepted(Machine("0.8.0", actions: false), AgentActionKind.StopDevServer));
    }

    [Fact]
    public void The_dialog_shows_the_recipe_the_agent_runs()
    {
        Assert.Equal("dotnet nuget locals http-cache --clear", AgentActions.CommandLine(AgentAction.CleanCache(DiskCacheId.NuGetHttpCache)));
        Assert.Equal("docker image prune -f", AgentActions.CommandLine(AgentAction.CleanCache(DiskCacheId.DockerImages)));
        Assert.Equal("npm cache clean --force", AgentActions.CommandLine(AgentAction.CleanCache(DiskCacheId.NpmCache)));
        Assert.StartsWith("move the contents of", AgentActions.CommandLine(AgentAction.CleanCache(DiskCacheId.XcodeDerivedData)));
        Assert.Equal("kill -TERM 4242", AgentActions.CommandLine(AgentAction.StopDevServer("/dev/akin", 4242, DateTimeOffset.UnixEpoch, "node")));
        Assert.Contains("never SIGKILL", AgentActions.Safeguard(AgentAction.StopDevServer("/dev/akin", 4242, DateTimeOffset.UnixEpoch, "node")));
        Assert.Equal("Clean npm cache", AgentActions.Summary(AgentAction.CleanCache(DiskCacheId.NpmCache)));

        // Nothing that holds data or cannot be made again has a recipe.
        Assert.All(new[] { DiskCacheId.DockerVolumes, DiskCacheId.XcodeArchives, DiskCacheId.DotnetSdks, DiskCacheId.XcodeDeviceSupport, DiskCacheId.GradleCache, DiskCacheId.MavenRepository, DiskCacheId.YarnCache },
            id => Assert.False(DiskCaches.HasRecipe(id)));
        Assert.All(Enum.GetValues<DiskCacheId>().Where(DiskCaches.HasRecipe), id => Assert.True(DiskCaches.Get(id).Regenerable));
        Assert.DoesNotContain(Enum.GetValues<DiskCacheId>().Select(id => DiskCaches.CommandLine(id)), c => c.Contains(" -a", StringComparison.Ordinal) || c.Contains("volume", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_is_on_by_default_and_has_its_own_off_switch()
    {
        var options = new AgentOptions();
        Assert.True(options.Disk.Enabled && options.DevServers.Enabled);
        Assert.Equal(6, options.Disk.IntervalHours);
        Assert.Equal("false", AgentCli.Parse(["run", "--no-disk"]).Overrides["Agent:Disk:Enabled"]);
        Assert.Equal("false", AgentCli.Parse(["run", "--no-dev-servers"]).Overrides["Agent:DevServers:Enabled"]);
    }

    [Fact]
    public void A_snapshot_from_a_newer_agent_with_an_unknown_cache_id_still_reads()
    {
        var json = """{"machine":{"machineId":"m","hostname":"h","disk":{"collectedAt":"2026-11-20T09:00:00Z","items":[{"id":"SomethingNew","bytes":5}]}}}""";
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<MachineSnapshot>(json, SnapshotSchema.JsonOptions)!;
        Assert.Equal("SomethingNew", snapshot.Machine.Disk!.Items[0].Id);
        Assert.Null(DiskCaches.Find("SomethingNew"));
        Assert.Equal(1, SnapshotSchema.Version);
    }
}
