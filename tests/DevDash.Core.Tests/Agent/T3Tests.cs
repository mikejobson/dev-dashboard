using System.Diagnostics;
using DevDash.Agent;
using DevDash.Agent.Actions;
using DevDash.Agent.Git;
using DevDash.Agent.Scanning;
using DevDash.Agent.T3Code;
using DevDash.Agent.Toolchains;
using DevDash.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDash.Core.Tests.Agent;

/// <summary>
/// The agent's side of T3 Code: default roots, reading the CLI's JSON, the "whose project is it" rule, and
/// the two actions. T3 Code itself is never run: the CLI is <see cref="FakeTools"/>, or (on macOS and Linux)
/// a small shell script in a temporary folder. Nothing here can start a real thread or open a window.
/// </summary>
public sealed class T3Tests : IDisposable
{
    private const string ThreadId = "8a1d3c52-7f0e-4b6a-9c41-2d5e6f708192";
    private const string Link = "https://app.t3.codes/threads/env-1/" + ThreadId;

    private readonly string _root = Directory.CreateTempSubdirectory("devdash-t3-").FullName;
    private readonly string _home;
    private readonly string _dev;
    private readonly string _cli;

    public T3Tests()
    {
        _home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        _dev = Directory.CreateDirectory(Path.Combine(_home, "Developer")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(_home, ".t3", "bin")).FullName;
        _cli = Path.Combine(bin, OperatingSystem.IsWindows() ? "t3.exe" : "t3");
        File.WriteAllText(_cli, ""); // FakeTools answers for it; the locator only needs it to exist
    }

    private ToolEnvironment Env => new() { Home = _home, IsWindows = OperatingSystem.IsWindows(), IsMacOS = OperatingSystem.IsMacOS() };

    private AgentOptions Options(bool actions = true) => new()
    {
        Roots = [_dev],
        MachineId = "test-machine",
        AutoFetch = new AutoFetchOptions { Enabled = false },
        Actions = new ActionsOptions { Enabled = actions },
        T3 = new T3Options { Home = Path.Combine(_home, ".t3") },
    };

    private T3Service Service(FakeTools tools, AgentOptions options) =>
        new(tools, new StaticOptions(options), NullLogger<T3Service>.Instance, environment: Env);

    private static FakeTools Tools() => new FakeTools().On("t3", "--version", "t3 v0.1.9\n");

    // ---- roots ----------------------------------------------------------------------------------

    [Fact]
    public void The_shipped_defaults_configure_no_roots_so_the_T3_default_applies()
    {
        // A "Roots" entry in the embedded appsettings.json counts as configured and hides ~/.t3/projects.
        using var defaults = typeof(AgentOptions).Assembly.GetManifestResourceStream("appsettings.json")!;
        using var json = System.Text.Json.JsonDocument.Parse(defaults);
        Assert.False(json.RootElement.GetProperty("Agent").TryGetProperty("Roots", out _));
    }

    [Fact]
    public void Default_roots_are_Developer_plus_T3_projects_when_T3_has_made_that_folder()
    {
        var options = new AgentOptions();
        Assert.Equal([Path.Combine(_home, "Developer")], options.ResolveRoots(_home));

        var projects = Directory.CreateDirectory(Path.Combine(_home, ".t3", "projects")).FullName;
        Assert.Equal([Path.Combine(_home, "Developer"), projects], options.ResolveRoots(_home));

        // Configured roots are used exactly as configured, T3 or not.
        Assert.Equal([Path.Combine(_root, "elsewhere")], new AgentOptions { Roots = [Path.Combine(_root, "elsewhere")] }.ResolveRoots(_home));

        // --no-t3, or T3:ScanProjects off, leaves T3's folder out of the defaults.
        Assert.Single(new AgentOptions { T3 = new T3Options { Enabled = false } }.ResolveRoots(_home));
        Assert.Single(new AgentOptions { T3 = new T3Options { ScanProjects = false } }.ResolveRoots(_home));
        Assert.Equal("false", AgentCli.Parse(["run", "--no-t3"]).Overrides["Agent:T3:Enabled"]);

        // A configured T3 home moves the projects folder with it.
        var other = Directory.CreateDirectory(Path.Combine(_root, "t3home", "projects")).FullName;
        Assert.Equal(other, new AgentOptions { T3 = new T3Options { Home = Path.Combine(_root, "t3home") } }.ResolveRoots(_home)[1]);
    }

    [Fact]
    public void The_CLI_is_found_where_T3_puts_it_then_on_PATH_and_a_configured_path_is_the_only_one_tried()
    {
        var t3Home = Path.Combine(_home, ".t3");
        Assert.Equal(_cli, T3Locator.Find(null, t3Home, Env));
        Assert.Null(T3Locator.Find(Path.Combine(_root, "missing", "t3"), t3Home, Env));

        var onPath = Directory.CreateDirectory(Path.Combine(_root, "path")).FullName;
        File.WriteAllText(Path.Combine(onPath, "t3"), "");
        Assert.Equal(Path.Combine(onPath, "t3"), T3Locator.Find(null, Path.Combine(_root, "no-t3"), new ToolEnvironment { Home = _home, PathEntries = ["relative", onPath] }));

        // Windows: an .exe before a .cmd, which goes through cmd.exe.
        var win = Directory.CreateDirectory(Path.Combine(_root, "win", "bin")).FullName;
        File.WriteAllText(Path.Combine(win, "t3.cmd"), "");
        var windows = new ToolEnvironment { Home = _home, IsWindows = true };
        Assert.Equal(Path.Combine(win, "t3.cmd"), T3Locator.Find(null, Path.GetDirectoryName(win)!, windows));
        File.WriteAllText(Path.Combine(win, "t3.exe"), "");
        Assert.Equal(Path.Combine(win, "t3.exe"), T3Locator.Find(null, Path.GetDirectoryName(win)!, windows));
        Assert.True(T3Locator.IsBatchFile("C:\\Users\\me\\.t3\\bin\\t3.CMD"));
        Assert.True(T3Locator.HasBatchMetacharacters("fix & ship"));
        Assert.False(T3Locator.HasBatchMetacharacters("issue/12-fix-login"));
    }

    [Fact]
    public void Worktrees_under_T3s_folder_are_marked_as_T3s_and_no_others()
    {
        var worktrees = Path.Combine(_home, ".t3", "worktrees");
        IReadOnlyList<WorktreeInfo> list =
        [
            new() { Path = Path.Combine(_dev, "widget"), Branch = "main" },
            new() { Path = Path.Combine(worktrees, "widget", "t3-1a2b3c4d"), Branch = "t3/fix" },
            new() { Path = Path.Combine(_dev, "widget.worktrees", "spike"), Branch = "spike" },
            new() { Path = worktrees + "-not-really", Branch = "x" },
        ];

        var marked = T3Locator.MarkOwned(list, worktrees);

        Assert.Equal([null, true, null, null], marked.Select(w => w.T3Owned));
    }

    // ---- reading the CLI ------------------------------------------------------------------------

    private static string ListJson(string projectId, params (string Id, string Updated, bool Archived)[] threads) =>
        "[" + string.Join(",", threads.Select(t => $$"""
            {"threadId":"{{t.Id}}","projectId":"{{projectId}}","title":"Thread {{t.Id}}","link":"https://app.t3.codes/threads/env-1/{{t.Id}}",
             "createdBy":"user","creationSource":"cli","status":"running","providerInstanceId":"claudeAgent","model":"claude-opus-5-5",
             "runtimeMode":"full-access","linkedPullRequest":null,"pullRequests":[],"settled":false,"settledAt":null,"snoozed":false,
             {{(t.Archived ? "\"archived\":true," : "")}}"itemCount":3,"createdAt":"2026-11-18T09:00:00.000Z","updatedAt":"{{t.Updated}}"}
            """)) + "]";

    private static string ShowJson(string id, int pending, string branch = "t3/fix-login", bool archived = false) => $$"""
        {"thread":{"threadId":"{{id}}","projectId":"p","title":"Thread {{id}}","link":"https://app.t3.codes/threads/env-1/{{id}}","status":"running",
         "branch":"{{branch}}","worktreePath":"/home/me/.t3/worktrees/widget/t3-1a2b","pendingRequestCount":{{pending}},"archived":{{(archived ? "true" : "false")}},
         "runCount":2,"settled":false,"pullRequests":[],"createdAt":"2026-11-18T09:00:00.000Z","updatedAt":"2026-11-19T09:00:00.000Z"},"messages":[]}
        """;

    [Fact]
    public void Thread_list_and_show_are_read_by_their_real_field_names()
    {
        var list = T3Parser.ParseThreadList("""
            [{"threadId":"t-1","projectId":"p-1","title":"Fix login","link":"https://app.t3.codes/threads/e/t-1","status":"completed",
              "createdBy":"user","creationSource":"web","providerInstanceId":"claudeAgent","model":"claude-opus-5-5","runtimeMode":"full-access",
              "linkedPullRequest":{"number":42,"url":"https://github.com/acme/widget/pull/42","state":"OPEN"},
              "pullRequests":["https://github.com/acme/widget/pull/42","https://github.com/acme/widget/pull/43"],
              "settled":true,"settledAt":"2026-11-19T10:00:00Z","snoozed":false,"itemCount":12,
              "createdAt":"2026-11-17T15:50:40.452Z","updatedAt":"2026-11-20T10:16:09.348Z"},
             {"threadId":"t-2","projectId":"p-1","title":"Old","status":"idle","archived":true,"updatedAt":"2026-01-01T00:00:00Z"},
             {"projectId":"p-1","title":"no id, skipped"}]
            """);

        Assert.NotNull(list);
        Assert.Equal(2, list.Count);
        var first = list[0];
        Assert.Equal(("t-1", "p-1", false), (first.Thread.Id, first.ProjectId, first.Archived));
        Assert.Equal("Fix login", first.Thread.Title);
        Assert.Equal("https://app.t3.codes/threads/e/t-1", first.Thread.Link);
        Assert.True(first.Thread.Settled);
        Assert.Equal(T3ThreadState.Settled, first.Thread.State);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 10, 16, 9, 348, TimeSpan.Zero), first.Thread.UpdatedAt);
        Assert.Equal([42, 43], first.Thread.PullRequests.Select(p => p.Number));
        Assert.Equal("OPEN", first.Thread.PullRequests[0].State);
        Assert.True(list[1].Archived);

        var shown = T3Parser.ParseThreadShow(ShowJson("t-1", pending: 2));
        Assert.NotNull(shown);
        Assert.Equal(("t3/fix-login", "/home/me/.t3/worktrees/widget/t3-1a2b", 2), (shown.Thread.Branch, shown.Thread.WorktreePath, shown.Thread.PendingRequests));
        Assert.Equal(T3ThreadState.NeedsInput, shown.Thread.State);

        Assert.Null(T3Parser.ParseThreadList("No T3 Code project matches '/tmp'."));
        Assert.Null(T3Parser.ParseThreadList("{\"error\":\"x\"}"));
        Assert.Equal("0.1.9", T3Parser.ParseVersion("t3 v0.1.9\n"));

        // A link is only kept when it is https.
        Assert.Null(T3Parser.ParseThreadList("""[{"threadId":"x","link":"javascript:alert(1)"}]""")![0].Thread.Link);
    }

    [Theory]
    [InlineData("{\"threadId\":\"" + ThreadId + "\",\"link\":\"" + Link + "\"}", ThreadId, Link)]
    [InlineData("{\"thread\":{\"id\":\"" + ThreadId + "\",\"link\":\"" + Link + "\"}}", ThreadId, Link)]
    [InlineData("{\"id\":\"" + ThreadId + "\"}", ThreadId, null)]
    [InlineData("\"" + ThreadId + "\"", ThreadId, null)]
    [InlineData("Started thread " + ThreadId + "\n" + Link + "\n", ThreadId, Link)]
    public void The_new_threads_id_and_link_are_read_from_whatever_shape_start_prints(string output, string id, string? link)
    {
        var started = T3Parser.ParseStarted(output);
        Assert.NotNull(started);
        Assert.Equal((id, link), (started.ThreadId, started.Link));
    }

    [Fact]
    public void Start_output_without_an_id_is_no_thread()
    {
        Assert.Null(T3Parser.ParseStarted(""));
        Assert.Null(T3Parser.ParseStarted("{\"ok\":true}"));
        Assert.Null(T3Parser.ParseStarted("done"));
    }

    // ---- whose project ---------------------------------------------------------------------------

    private static DevEntry Repo(string path, bool linked = false) => new()
    {
        Root = Path.GetDirectoryName(path)!,
        Name = Path.GetFileName(path),
        Path = path,
        Kind = EntryKind.GitRepo,
        Git = new GitState { Branch = "main", IsLinkedWorktree = linked },
    };

    [Fact]
    public async Task A_project_two_repos_resolve_to_is_neither_repos_and_only_a_one_to_one_project_counts()
    {
        var shared = Path.Combine(_dev, "alpha");
        var other = Path.Combine(_dev, "beta");
        var own = Path.Combine(_dev, "gamma");
        // Which project each path resolves to, as `thread list --project <path>` would say. alpha and beta are both
        // inside a project rooted above them (the ~/Developer case).
        var projects = new Dictionary<string, (string Project, string Thread)> { [shared] = ("p-developer", "t-a"), [other] = ("p-developer", "t-a"), [own] = ("p-gamma", "t-g") };
        var tools = Tools()
            .On(c => c.Arguments is ["thread", "list", ..], c => projects.TryGetValue(c.Arguments[3], out var p)
                ? new ToolResult(0, ListJson(p.Project, (p.Thread, "2026-11-19T09:00:00Z", false)), "", false)
                : new ToolResult(1, "", "No T3 Code project matches '/x'. Pass a project id or a path inside a project.", false))
            .On(c => c.Arguments is ["thread", "show", ..], c => new ToolResult(0, ShowJson(c.Arguments[2], pending: 0), "", false));
        var t3 = Service(tools, Options());

        var entries = await t3.EnrichAsync([Repo(shared), Repo(other), Repo(own), Repo(Path.Combine(_dev, "loose"))], fullScan: true, CancellationToken.None);

        Assert.Null(entries[0].T3);
        Assert.Null(entries[1].T3);
        Assert.Equal("p-gamma", entries[2].T3?.ProjectId);
        Assert.Equal(["t-g"], entries[2].T3!.Threads.Select(t => t.Id));
        Assert.Equal("t3/fix-login", entries[2].T3!.Threads[0].Branch); // from thread show
        Assert.Null(entries[3].T3); // "No T3 Code project matches" is an answer, not a failure
        Assert.Null(t3.Current?.Problem);

        // Rescanning one entry on its own does not make the shared project its own…
        Assert.Null((await t3.EnrichAsync([Repo(shared)], fullScan: false, CancellationToken.None))[0].T3);

        // …and a new repo that resolves to gamma's project makes it shared, not the new repo's.
        var inside = Path.Combine(_dev, "gamma-copy");
        projects[inside] = ("p-gamma", "t-g");
        Assert.Null((await t3.EnrichAsync([Repo(inside)], fullScan: false, CancellationToken.None))[0].T3);
    }

    [Fact]
    public async Task Thread_show_is_only_asked_again_when_a_threads_updatedAt_moves_and_archived_threads_are_left_out()
    {
        var repo = Path.Combine(_dev, "widget");
        var updated = "2026-11-19T09:00:00Z";
        var tools = Tools()
            .On(c => c.Arguments is ["thread", "list", ..], _ => new ToolResult(0, ListJson("p-w", ("t-1", updated, false), ("t-2", "2026-11-18T09:00:00Z", false), ("t-3", "2026-11-17T09:00:00Z", true)), "", false))
            .On(c => c.Arguments is ["thread", "show", "t-2", ..], _ => new ToolResult(0, ShowJson("t-2", 0, archived: true), "", false))
            .On(c => c.Arguments is ["thread", "show", ..], c => new ToolResult(0, ShowJson(c.Arguments[2], pending: 1), "", false));
        var t3 = Service(tools, Options());
        int Shows() => tools.Calls.Count(c => c.Arguments is ["thread", "show", ..]);

        var first = (await t3.EnrichAsync([Repo(repo), Repo(Path.Combine(_dev, "widget-wt"), linked: true)], fullScan: true, CancellationToken.None))[0].T3!;
        Assert.Equal(["t-1"], first.Threads.Select(t => t.Id)); // t-2 is archived by show, t-3 by list
        Assert.Equal(1, first.Threads[0].PendingRequests);
        Assert.Equal(2, Shows());
        Assert.DoesNotContain(tools.Calls, c => c.Arguments.Contains(Path.Combine(_dev, "widget-wt"))); // linked worktrees are not looked up

        await t3.EnrichAsync([Repo(repo)], fullScan: true, CancellationToken.None);
        Assert.Equal(3, Shows()); // t-1 unchanged: cached; t-2 asked again (an archived thread is not cached)

        updated = "2026-11-20T09:00:00Z";
        await t3.EnrichAsync([Repo(repo)], fullScan: true, CancellationToken.None);
        Assert.Equal(5, Shows());

        // Every call is a fixed argument list with no shell in between.
        Assert.All(tools.Calls, c => Assert.Equal(_cli, c.FileName));
        Assert.Contains(tools.Calls, c => c.Arguments.SequenceEqual(["thread", "list", "--project", repo, "--json"]));
        Assert.Contains(tools.Calls, c => c.Arguments.SequenceEqual(["thread", "show", "t-1", "--json", "--messages", "0"]));
    }

    [Fact]
    public async Task T3_missing_or_not_answering_leaves_the_scan_alone()
    {
        var repos = Enumerable.Range(0, 20).Select(i => Repo(Path.Combine(_dev, $"r{i}"))).ToList();

        // Not answering: the first failures stop the rest, and the snapshot says why.
        var down = Tools().On(c => c.Arguments is ["thread", ..], _ => new ToolResult(1, "", "connect ECONNREFUSED 127.0.0.1:3773", false));
        var t3 = Service(down, Options());
        var entries = await t3.EnrichAsync(repos, fullScan: true, CancellationToken.None);
        Assert.All(entries, e => Assert.Null(e.T3));
        Assert.True(down.Calls.Count(c => c.Arguments[0] == "thread") <= 3 + 4, "it kept asking a T3 that does not answer"); // three failures, plus what was already under way
        Assert.Equal((true, "T3 Code did not answer; is the app running?"), (t3.Current!.Available, t3.Current.Problem));

        // Not installed: nothing is run at all.
        File.Delete(_cli);
        var none = new FakeTools();
        var missing = Service(none, Options());
        Assert.Same(repos, await missing.EnrichAsync(repos, fullScan: true, CancellationToken.None));
        Assert.Equal((false, "its CLI was not found"), (missing.Current!.Available, missing.Current.Problem));
        Assert.Empty(none.Calls);

        // Switched off: no T3 info at all.
        Assert.Null(await Service(new FakeTools(), new AgentOptions { T3 = new T3Options { Enabled = false } }).InfoAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_snapshot_says_T3_is_there_and_takes_actions_only_with_actions_on()
    {
        var repo = Path.Combine(_dev, "widget");
        InitRepo(repo);
        var tools = Tools()
            .On(c => c.Arguments is ["thread", "list", ..], _ => new ToolResult(0, ListJson("p-w", ("t-1", "2026-11-19T09:00:00Z", false)), "", false))
            .On(c => c.Arguments is ["thread", "show", ..], c => new ToolResult(0, ShowJson(c.Arguments[2], 0), "", false));

        foreach (var actions in (bool[])[true, false])
        {
            var options = new StaticOptions(Options(actions));
            var git = new GitRunner();
            var scanner = new MachineScanner(new RepoScanner(git, new FetchService(git, options, NullLogger<FetchService>.Instance), options), new SizeCache(Path.Combine(_root, $"sizes-{actions}.json")),
                options, NullLogger<MachineScanner>.Instance, t3: Service(tools, options.CurrentValue));

            var snapshot = await scanner.ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None);

            Assert.Equal((true, "0.1.9"), (snapshot.Machine.T3!.Available, snapshot.Machine.T3.Version));
            Assert.Equal(actions, snapshot.Machine.AcceptsT3);
            Assert.Equal(actions, AgentActions.Accepts(snapshot.Machine, AgentActionKind.StartT3Thread));
            Assert.Equal("t-1", snapshot.Entries.Single().T3!.Threads.Single().Id);
        }
    }

    // ---- the actions: shape ---------------------------------------------------------------------

    private static AgentAction Start(string repo, string prompt = "Fix the login form.", string title = "Fix login", string @base = "main", string branch = "t3/fix-login") =>
        AgentAction.StartT3Thread(repo, prompt, title, @base, branch);

    [Fact]
    public void A_thread_start_is_checked_for_shape_on_both_sides()
    {
        var repo = Path.Combine(_dev, "widget");
        Assert.Null(AgentActions.Validate(Start(repo)));
        Assert.Null(AgentActions.Validate(Start(repo, @base: "origin/main")));
        Assert.Null(AgentActions.Validate(Start(repo, @base: "v1.2.0", branch: "issue/12-fix-login")));
        Assert.Null(AgentActions.Validate(Start(repo, prompt: "Line one\nLine two\r\n\tindented")));
        Assert.Null(AgentActions.Validate(AgentAction.OpenInT3(repo)));

        Assert.Contains("prompt is empty", AgentActions.Validate(Start(repo, prompt: "  \n")));
        Assert.Contains("longer than 32 KB", AgentActions.Validate(Start(repo, prompt: new string('x', AgentActions.MaxPromptBytes + 1))));
        Assert.Contains("longer than 32 KB", AgentActions.Validate(Start(repo, prompt: new string('é', AgentActions.MaxPromptBytes / 2 + 1)))); // bytes, not characters
        Assert.Null(AgentActions.Validate(Start(repo, prompt: new string('x', AgentActions.MaxPromptBytes))));
        Assert.Contains("control characters", AgentActions.Validate(Start(repo, prompt: "hi\0there")));
        Assert.Contains("one line", AgentActions.Validate(Start(repo, title: "two\nlines")));
        Assert.Contains("one line", AgentActions.Validate(Start(repo, title: new string('t', AgentActions.MaxTitleChars + 1))));
        foreach (var bad in (string[])["-x", "--upload-pack=evil", "main~1", "HEAD^", "a..b", "main@{1}", "origin/", "a b", "main.lock", "x:y"])
        {
            Assert.NotNull(AgentActions.Validate(Start(repo, @base: bad)));
        }

        foreach (var bad in (string[])["-b", "bad..name", "has space", "ends/", "x.lock", "a~b", "@", ""])
        {
            Assert.NotNull(AgentActions.Validate(Start(repo, branch: bad)));
        }

        Assert.Contains("do not match", AgentActions.Validate(AgentAction.OpenInT3(repo) with { Prompt = "x" }));
        Assert.Contains("do not match", AgentActions.Validate(Start(repo) with { NewBranch = null }));
        Assert.Contains("do not match", AgentActions.Validate(Start(repo) with { Branch = "main" }));
        Assert.Contains("must be absolute", AgentActions.Validate(Start("widget")));
    }

    [Fact]
    public void Only_an_agent_that_says_so_is_offered_or_handed_a_T3_action_and_the_reason_says_what_to_fix()
    {
        var ready = new MachineInfo { MachineId = "m", Hostname = "m-host", AgentVersion = "0.7.0", ActionsEnabled = true, AcceptsT3 = true, T3 = new T3Info { Available = true } };
        Assert.True(AgentActions.Accepts(ready, AgentActionKind.OpenInT3));
        Assert.True(AgentActions.Accepts(ready, AgentActionKind.StartT3Thread));
        Assert.True(AgentActions.IsRepoAction(AgentActionKind.StartT3Thread));

        var old = new MachineInfo { MachineId = "m", Hostname = "m-host", AgentVersion = "0.6.1", ActionsEnabled = true, AcceptsRemoveProject = true };
        Assert.Contains("Update the agent on m-host to 0.7.0", AgentActions.WhyNotAccepted(old, AgentActionKind.StartT3Thread));

        var noCli = ready with { AcceptsT3 = false, T3 = new T3Info { Available = false, Problem = "its CLI was not found" } };
        Assert.Equal("T3 Code is not available on m-host: its CLI was not found.", AgentActions.WhyNotAccepted(noCli, AgentActionKind.OpenInT3));

        var off = ready with { ActionsEnabled = false };
        Assert.Contains("--enable-actions", AgentActions.WhyNotAccepted(off, AgentActionKind.StartT3Thread));

        // The dialog never repeats the prompt on the command line, and quotes everything else.
        var line = AgentActions.CommandLine(Start("/dev/widget", prompt: "secret plan"));
        Assert.Equal("t3 thread start '/dev/widget' - --title 'Fix login' --worktree 'main' --branch 't3/fix-login' --json < prompt (11 characters)", line);
    }

    // ---- the actions: run -----------------------------------------------------------------------

    private (ActionExecutor Executor, AgentOptions Options) Executor(FakeTools tools, bool actions = true, Action<AgentOptions>? configure = null)
    {
        var options = Options(actions);
        configure?.Invoke(options);
        var monitor = new StaticOptions(options);
        var git = new GitRunner();
        var t3 = new T3Actions(Service(tools, options), monitor, NullLogger<T3Actions>.Instance);
        return (new ActionExecutor(git, new ActionValidator(git, monitor), monitor, NullLogger<ActionExecutor>.Instance, t3: t3), options);
    }

    private string Widget()
    {
        var repo = Path.Combine(_dev, "widget");
        InitRepo(repo);
        return repo;
    }

    [Fact]
    public async Task Starting_a_thread_runs_t3_with_the_prompt_on_stdin_and_reports_the_threads_id()
    {
        var repo = Widget();
        var tools = Tools().On("t3", "thread start", $$"""{"threadId":"{{ThreadId}}","link":"{{Link}}"}""");
        var (executor, _) = Executor(tools);
        const string prompt = "Fix the login form.\nIt rejects valid emails; see issue #12.\n";

        var outcome = await executor.ExecuteAsync(Start(repo, prompt: prompt, title: "#12 Login rejects emails", branch: "issue/12-login"), CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.Equal(ThreadId, outcome.Thread?.ThreadId);
        Assert.Equal(Link, outcome.Thread?.Link);
        Assert.Equal(outcome.Thread, outcome.ToResult().Thread);
        Assert.Equal([repo], outcome.AffectedPaths); // rescanned straight away, so the thread shows

        var call = Assert.Single(tools.Calls, c => c.Arguments is ["thread", "start", ..]);
        Assert.Equal(["thread", "start", repo, "-", "--title", "#12 Login rejects emails", "--worktree", "main", "--branch", "issue/12-login", "--json"], call.Arguments);
        Assert.Equal(prompt, call.StandardInput);
        Assert.DoesNotContain(call.Arguments, a => a.Contains("Fix the login form", StringComparison.Ordinal));
        Assert.False(BranchExists(repo, "issue/12-login")); // the agent made no branch itself: T3 does, in its own worktree
    }

    [Fact]
    public async Task A_failed_start_is_a_failure_with_T3s_own_words_and_no_thread()
    {
        var repo = Widget();
        var tools = Tools().On("t3", "thread start", "", exit: 3, stderr: "Could not reach the T3 Code server.");
        var (executor, _) = Executor(tools);

        var outcome = await executor.ExecuteAsync(Start(repo), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(3, outcome.ExitCode);
        Assert.Contains("Could not reach the T3 Code server.", outcome.Output);
        Assert.Null(outcome.Thread);
    }

    [Fact]
    public async Task A_start_is_refused_before_T3_runs_when_the_branch_exists_the_base_is_unknown_or_actions_are_off()
    {
        var repo = Widget();
        Git(repo, "branch", "taken");
        var tools = Tools().On("t3", "thread start", "{}");
        var (executor, _) = Executor(tools);

        var taken = await executor.ExecuteAsync(Start(repo, branch: "taken"), CancellationToken.None);
        Assert.True(taken.Refused);
        Assert.Contains("already exists", taken.Output);

        var nowhere = await executor.ExecuteAsync(Start(repo, @base: "origin/nope"), CancellationToken.None);
        Assert.True(nowhere.Refused);
        Assert.Contains("does not name a commit", nowhere.Output);

        var outside = await executor.ExecuteAsync(Start(Path.Combine(_root, "elsewhere")), CancellationToken.None);
        Assert.True(outside.Refused);
        Assert.Contains("outside this agent's scan roots", outside.Output);

        var (off, _) = Executor(tools, actions: false);
        var refused = await off.ExecuteAsync(Start(repo), CancellationToken.None);
        Assert.True(refused.Refused);
        Assert.Contains("not enabled", refused.Output);

        var (noT3, _) = Executor(tools, configure: o => o.T3.Enabled = false);
        Assert.Contains("switched off", (await noT3.ExecuteAsync(Start(repo), CancellationToken.None)).Output);

        File.Delete(_cli);
        var (missing, _) = Executor(new FakeTools());
        Assert.Contains("T3 Code is not available here", (await missing.ExecuteAsync(AgentAction.OpenInT3(repo), CancellationToken.None)).Output);

        Assert.DoesNotContain(tools.Calls, c => c.Arguments is ["thread", "start", ..]);
    }

    [Fact]
    public async Task Open_in_T3_runs_t3_app_with_the_repo_and_nothing_else()
    {
        var repo = Widget();
        var tools = Tools().On("t3", "app", "");
        var (executor, _) = Executor(tools);

        var outcome = await executor.ExecuteAsync(AgentAction.OpenInT3(repo), CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.Equal(["app", repo], Assert.Single(tools.Calls, c => c.Arguments[0] == "app").Arguments);
        Assert.Empty(outcome.AffectedPaths);
    }

    [Fact]
    public async Task A_worktree_T3_made_is_not_removed_by_the_dashboard()
    {
        var repo = Widget();
        var t3Worktree = Path.Combine(_home, ".t3", "worktrees", "widget", "t3-1a2b3c4d");
        Directory.CreateDirectory(Path.GetDirectoryName(t3Worktree)!);
        Git(repo, "worktree", "add", "-q", "-b", "t3/fix", t3Worktree);
        var (executor, _) = Executor(Tools());

        var outcome = await executor.ExecuteAsync(AgentAction.RemoveWorktree(repo, t3Worktree), CancellationToken.None);

        Assert.True(outcome.Refused);
        Assert.Contains("T3 Code made this worktree", outcome.Output);
        Assert.True(Directory.Exists(t3Worktree));
    }

    /// <summary>The real process runner, against a shell script standing in for t3: arguments as a list, the prompt on stdin, exit codes.</summary>
    [Fact]
    public async Task The_real_runner_hands_the_prompt_to_the_CLI_on_stdin()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the stand-in is a POSIX shell script
        }

        var repo = Widget();
        var record = Path.Combine(_root, "record");
        Directory.CreateDirectory(record);
        File.WriteAllText(_cli, $$"""
            #!/bin/sh
            printf '%s\n' "$@" > '{{record}}/args'
            cat > '{{record}}/stdin'
            case "$1" in
              --version) echo "t3 v0.1.9" ;;
              thread) [ "$2" = start ] && printf '{"threadId":"{{ThreadId}}","link":"{{Link}}"}\n' ;;
              *) echo "unexpected" >&2; exit 9 ;;
            esac
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(_cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var options = Options();
        var monitor = new StaticOptions(options);
        var git = new GitRunner();
        var service = new T3Service(new ProcessToolRunner(), monitor, NullLogger<T3Service>.Instance, environment: Env);
        var executor = new ActionExecutor(git, new ActionValidator(git, monitor), monitor, NullLogger<ActionExecutor>.Instance, t3: new T3Actions(service, monitor, NullLogger<T3Actions>.Instance));
        var prompt = "Fix it.\n$(touch '" + Path.Combine(_root, "pwned") + "')\n`id` & ; | \"quoted\"\n" + new string('x', 70_000 / 10);

        var outcome = await executor.ExecuteAsync(Start(repo, prompt: prompt, title: "Title with 'quotes' and $HOME"), CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.Equal(ThreadId, outcome.Thread?.ThreadId);
        Assert.Equal(prompt, File.ReadAllText(Path.Combine(record, "stdin")));
        Assert.Equal(["thread", "start", repo, "-", "--title", "Title with 'quotes' and $HOME", "--worktree", "main", "--branch", "t3/fix-login", "--json"],
            File.ReadAllLines(Path.Combine(record, "args")));
        Assert.False(File.Exists(Path.Combine(_root, "pwned")));
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static void InitRepo(string path)
    {
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(path, "README.md"), "# Widget\n");
        Git(path, "add", ".");
        Git(path, "commit", "-q", "-m", "initial");
    }

    private static bool BranchExists(string repo, string branch) =>
        Git(repo, "for-each-ref", "--format=%(refname:short)", "refs/heads").Split('\n').Contains(branch);

    private static string Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in (string[])["-c", "user.name=DevDash Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "-c", "init.defaultBranch=main", .. args])
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private sealed class StaticOptions(AgentOptions value) : IOptionsMonitor<AgentOptions>
    {
        public AgentOptions CurrentValue => value;

        public AgentOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AgentOptions, string?> listener) => null;
    }
}
