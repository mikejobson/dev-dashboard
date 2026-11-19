using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using DevDash.Agent;
using DevDash.Agent.Actions;
using DevDash.Agent.Git;
using DevDash.Agent.Scanning;
using DevDash.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDash.Core.Tests.Agent;

/// <summary>
/// The agent side of a refresh asked for from the dashboard: what is accepted, that it rescans one
/// entry (forced sizes, a fetch only where the machine fetches), and that it all goes through the one
/// scan loop. Throwaway repositories under the system temp folder; the "hub" is a stub that keeps what
/// it is sent.
/// </summary>
public sealed class RefreshTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("devdash-refresh-").FullName;
    private readonly string _dev;
    private readonly string _seed;
    private readonly AgentOptions _options;
    private readonly StaticOptions _monitor;
    private readonly RecordingHub _hub = new();

    public RefreshTests()
    {
        _dev = Directory.CreateDirectory(Path.Combine(_root, "Developer")).FullName;
        var origin = Path.Combine(_root, "origin", "widget.git");

        _seed = Directory.CreateDirectory(Path.Combine(_root, "seed")).FullName;
        Git(_seed, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(_seed, "README.md"), "# Widget\n");
        Git(_seed, "add", ".");
        Git(_seed, "commit", "-q", "-m", "initial");
        Directory.CreateDirectory(Path.GetDirectoryName(origin)!);
        Git(_root, "clone", "-q", "--bare", _seed, origin);
        Git(_seed, "remote", "add", "origin", origin);
        Git(_dev, "clone", "-q", origin, "widget");

        var gadget = Directory.CreateDirectory(Path.Combine(_dev, "gadget")).FullName;
        Git(gadget, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(gadget, "README.md"), "# Gadget\n");
        Git(gadget, "add", ".");
        Git(gadget, "commit", "-q", "-m", "initial");

        _options = new AgentOptions
        {
            Roots = [_dev],
            MachineId = "test-machine",
            HubUrl = "http://hub.invalid/",
            ApiKey = "test-key",
            Watch = false,
            AutoFetch = new AutoFetchOptions { Enabled = true, IntervalHours = 4, TimeoutSeconds = 30 },
        };
        _monitor = new StaticOptions(_options);
    }

    private string Widget => Path.Combine(_dev, "widget");

    private string Gadget => Path.Combine(_dev, "gadget");

    private (AgentWorker Worker, RescanRequests Rescans) CreateWorker()
    {
        var git = new GitRunner();
        var scanner = new MachineScanner(
            new RepoScanner(git, new FetchService(git, _monitor, NullLogger<FetchService>.Instance), _monitor),
            new SizeCache(Path.Combine(_root, "sizes.json")), _monitor, NullLogger<MachineScanner>.Instance);
        var rescans = new RescanRequests(_monitor);
        var worker = new AgentWorker(scanner, new HubClient(new HttpClient(_hub), _monitor), _monitor, NullLogger<AgentWorker>.Instance, rescans);
        return (worker, rescans);
    }

    private ActionExecutor Executor(RescanRequests? rescans)
    {
        var git = new GitRunner();
        return new ActionExecutor(git, new ActionValidator(git, _monitor), _monitor, NullLogger<ActionExecutor>.Instance, rescans: rescans);
    }

    /// <summary>A commit on the remote that the clone has not fetched.</summary>
    private void CommitUpstream(string message)
    {
        File.WriteAllText(Path.Combine(_seed, "upstream.txt"), message);
        Git(_seed, "add", ".");
        Git(_seed, "commit", "-q", "-m", message);
        Git(_seed, "push", "-q", "origin", "main");
    }

    private static DevEntry Entry(MachineSnapshot snapshot, string name) => snapshot.Entries.Single(e => e.Name == name);

    // ---- the action -----------------------------------------------------------------------------

    [Fact]
    public void A_refresh_is_a_typed_action_with_a_path_and_nothing_else()
    {
        var refresh = AgentAction.Refresh("/dev/widget");

        Assert.Null(AgentActions.Validate(refresh));
        Assert.False(AgentActions.IsRepoAction(refresh.Kind)); // it is not a git action, so --enable-actions is not what gates it
        Assert.True(AgentActions.TakesPath(refresh.Kind));
        Assert.Equal("refresh '/dev/widget'", AgentActions.CommandLine(refresh));
        Assert.Equal("Refresh /dev/widget", AgentActions.Summary(refresh));
        Assert.Contains("Read-only", AgentActions.Safeguard(refresh));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentActions.GitArgs(refresh)); // there is no git command behind it

        Assert.Contains("Entry path is missing", AgentActions.Validate(new AgentAction { Kind = AgentActionKind.Refresh }));
        Assert.Contains("must be absolute", AgentActions.Validate(AgentAction.Refresh("widget")));
        Assert.Contains("do not match", AgentActions.Validate(refresh with { Branch = "main" }));
        Assert.Contains("do not match", AgentActions.Validate(refresh with { Version = "0.5.0" }));
    }

    [Fact]
    public async Task The_snapshot_says_whether_the_agent_takes_refreshes()
    {
        // The default agent (updates on request, actions off) polls, so it takes them.
        Assert.True((await MachineInfoProvider.GetAsync(new AgentOptions(), CancellationToken.None)).AcceptsRefresh);
        Assert.True((await MachineInfoProvider.GetAsync(new AgentOptions { Updates = { Mode = "off" }, Actions = { Enabled = true } }, CancellationToken.None)).AcceptsRefresh);

        // One that only reports never asks the hub for anything, so it cannot be reached.
        Assert.False((await MachineInfoProvider.GetAsync(new AgentOptions { Updates = { Mode = "off" } }, CancellationToken.None)).AcceptsRefresh);
    }

    // ---- validation -----------------------------------------------------------------------------

    [Fact]
    public void The_validator_accepts_any_path_inside_a_scanned_entry()
    {
        Assert.False(_options.Actions.Enabled);
        var validator = new ActionValidator(new GitRunner(), _monitor);

        Assert.Null(validator.RefreshRefusal(AgentAction.Refresh(Widget)));
        Assert.Null(validator.RefreshRefusal(AgentAction.Refresh(Path.Combine(_dev, "widget.worktrees", "feature")))); // maps to the folder that holds it
        Assert.Null(validator.RefreshRefusal(AgentAction.Refresh(Path.Combine(_dev, "gone")))); // deleted since: the rescan drops it
    }

    [Fact]
    public void The_validator_rejects_paths_outside_the_scan_roots()
    {
        var validator = new ActionValidator(new GitRunner(), _monitor);

        Assert.Contains("outside this agent's scan roots", validator.RefreshRefusal(AgentAction.Refresh(_seed)));
        Assert.Contains("outside this agent's scan roots", validator.RefreshRefusal(AgentAction.Refresh(_dev))); // the root itself is a full scan, not an entry
        Assert.Contains("outside this agent's scan roots", validator.RefreshRefusal(AgentAction.Refresh(Path.GetPathRoot(_dev)!)));
        Assert.Contains("not a plain absolute path", validator.RefreshRefusal(AgentAction.Refresh(Path.Combine(_dev, "widget", "..", "..", "seed"))));
        Assert.Contains("not an entry this agent scans", validator.RefreshRefusal(AgentAction.Refresh(Path.Combine(_dev, ".cache"))));
        Assert.Contains("Not a refresh", validator.RefreshRefusal(AgentAction.Fetch(Widget)));
        Assert.Contains("No action", validator.RefreshRefusal(null));
    }

    [Fact]
    public void The_validator_rejects_a_link_inside_the_roots_that_points_outside_them()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating a symbolic link needs a privilege there
        }

        var link = Path.Combine(_dev, "sneaky");
        Directory.CreateSymbolicLink(link, _seed);

        Assert.Contains("outside this agent's scan roots", new ActionValidator(new GitRunner(), _monitor).RefreshRefusal(AgentAction.Refresh(link)));
    }

    [Fact]
    public async Task A_refresh_runs_with_actions_off_and_a_git_action_still_does_not()
    {
        Assert.False(_options.Actions.Enabled);
        var (worker, rescans) = CreateWorker();
        await worker.FullScanNowAsync(CancellationToken.None);
        var executor = Executor(rescans);

        var refresh = executor.ExecuteAsync(AgentAction.Refresh(Widget), CancellationToken.None);
        await worker.RescanPendingNowAsync(CancellationToken.None);
        var outcome = await refresh;

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains("Rescanned widget", outcome.Output);
        Assert.Empty(outcome.AffectedPaths); // it was the rescan; nothing more to ask for

        var fetch = await executor.ExecuteAsync(AgentAction.Fetch(Widget), CancellationToken.None);
        Assert.True(fetch.Refused);
        Assert.Contains("not enabled", fetch.Output);
    }

    [Fact]
    public async Task A_refresh_outside_the_roots_is_refused_before_anything_is_scanned()
    {
        var (worker, rescans) = CreateWorker();
        await worker.FullScanNowAsync(CancellationToken.None);
        var pushes = _hub.Snapshots.Count;

        var outcome = await Executor(rescans).ExecuteAsync(AgentAction.Refresh(_seed), CancellationToken.None);
        await worker.RescanPendingNowAsync(CancellationToken.None);

        Assert.True(outcome.Refused);
        Assert.Contains("outside this agent's scan roots", outcome.Output);
        Assert.Equal(pushes, _hub.Snapshots.Count);
    }

    [Fact]
    public async Task An_agent_with_no_scan_loop_says_so_instead_of_waiting()
    {
        var alone = await Executor(new RescanRequests(_monitor)).ExecuteAsync(AgentAction.Refresh(Widget), CancellationToken.None);
        Assert.False(alone.Succeeded);
        Assert.Contains("not running its scan loop", alone.Output);

        var none = await Executor(null).ExecuteAsync(AgentAction.Refresh(Widget), CancellationToken.None);
        Assert.True(none.Refused);
    }

    // ---- the rescan -----------------------------------------------------------------------------

    [Fact]
    public async Task Rescans_only_that_entry_with_a_forced_fetch_and_forced_sizes()
    {
        var (worker, rescans) = CreateWorker();
        await worker.FullScanNowAsync(CancellationToken.None);
        var before = Assert.Single(_hub.Snapshots);
        Assert.Equal(0, Entry(before, "widget").Git!.Behind);
        var sizeBefore = Entry(before, "widget").Size!.TotalBytes;

        // Things change everywhere: upstream, in widget, and in gadget.
        CommitUpstream("second");
        File.WriteAllBytes(Path.Combine(Widget, "big.bin"), new byte[200_000]);
        File.WriteAllText(Path.Combine(Gadget, "untracked.txt"), "not part of this refresh");

        var refresh = Assert.Single(rescans.Refresh([Widget]));
        Assert.False(refresh.IsCompleted); // it waits for the scan loop
        await worker.RescanPendingNowAsync(CancellationToken.None);
        var outcome = await refresh;

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains("fetched", outcome.Message);
        Assert.Contains("size measured", outcome.Message);
        Assert.Equal(2, _hub.Snapshots.Count);
        var after = _hub.Snapshots[1];

        // The fetch was only minutes after the last one, well inside the 4 hour interval: it was forced.
        var widget = Entry(after, "widget");
        Assert.Equal(1, widget.Git!.Behind);
        Assert.Equal(1, widget.Git.Untracked);

        // Sizes were measured a moment ago and would not be due for a day: also forced.
        Assert.True(widget.Size!.TotalBytes >= sizeBefore + 200_000, $"{sizeBefore} -> {widget.Size.TotalBytes}");

        // The other entry is exactly as the full scan left it, new file and all.
        Assert.Equal(0, Entry(after, "gadget").Git!.Untracked);
        Assert.Equal(Entry(before, "gadget").Size!.MeasuredAt, Entry(after, "gadget").Size!.MeasuredAt);
        Assert.Equal(Entry(before, "gadget").Size!.TotalBytes, Entry(after, "gadget").Size!.TotalBytes);
        Assert.True(widget.Size.MeasuredAt > Entry(before, "widget").Size!.MeasuredAt);
        Assert.Equal(before.Entries.Count, after.Entries.Count);
    }

    [Fact]
    public async Task Does_not_fetch_on_a_machine_that_has_fetching_off()
    {
        _options.AutoFetch.Enabled = false;
        var (worker, rescans) = CreateWorker();
        await worker.FullScanNowAsync(CancellationToken.None);
        CommitUpstream("second");
        File.WriteAllText(Path.Combine(Widget, "new.txt"), "local change");

        var refresh = Assert.Single(rescans.Refresh([Widget]));
        await worker.RescanPendingNowAsync(CancellationToken.None);
        var outcome = await refresh;

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains("auto-fetch is off on this machine", outcome.Message);
        var widget = Entry(_hub.Snapshots[^1], "widget");
        Assert.Equal(1, widget.Git!.Untracked); // it was rescanned
        Assert.Equal(0, widget.Git.Behind); // and nothing was fetched
        Assert.False(File.Exists(Path.Combine(Widget, ".git", "FETCH_HEAD")));
    }

    [Fact]
    public async Task Several_requests_for_one_entry_are_one_rescan_and_one_push()
    {
        var (worker, rescans) = CreateWorker();
        await worker.FullScanNowAsync(CancellationToken.None);
        Assert.Single(_hub.Snapshots);

        // Twice the same path, once a path deeper in the same entry, and once in two batches.
        var first = rescans.Refresh([Widget, Widget, Path.Combine(Widget, "src")]);
        var second = rescans.Refresh([Widget]);
        await worker.RescanPendingNowAsync(CancellationToken.None);

        var outcomes = await Task.WhenAll(first.Concat(second));
        Assert.Equal(4, outcomes.Length);
        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Message));
        Assert.Single(outcomes.Select(o => o.Message).Distinct()); // one rescan answered all four
        Assert.Equal(2, _hub.Snapshots.Count);

        // Nothing is left over to run again.
        await worker.RescanPendingNowAsync(CancellationToken.None);
        Assert.Equal(2, _hub.Snapshots.Count);
    }

    [Fact]
    public async Task Two_entries_in_one_batch_are_one_push()
    {
        var (worker, rescans) = CreateWorker();
        await worker.FullScanNowAsync(CancellationToken.None);
        File.WriteAllText(Path.Combine(Widget, "a.txt"), "a");
        File.WriteAllText(Path.Combine(Gadget, "b.txt"), "b");

        var tasks = rescans.Refresh([Widget, Gadget]);
        await worker.RescanPendingNowAsync(CancellationToken.None);
        var outcomes = await Task.WhenAll(tasks);

        Assert.Contains("Rescanned widget", outcomes[0].Message);
        Assert.Contains("Rescanned gadget", outcomes[1].Message);
        Assert.Contains("nothing to fetch", outcomes[1].Message); // gadget has no remote
        Assert.Equal(2, _hub.Snapshots.Count);
        Assert.Equal(1, Entry(_hub.Snapshots[1], "widget").Git!.Untracked);
        Assert.Equal(1, Entry(_hub.Snapshots[1], "gadget").Git!.Untracked);
    }

    [Fact]
    public async Task A_full_scan_in_between_does_not_lose_the_request()
    {
        var (worker, rescans) = CreateWorker();
        await worker.FullScanNowAsync(CancellationToken.None);

        var refresh = Assert.Single(rescans.Refresh([Widget]));
        await worker.FullScanNowAsync(CancellationToken.None); // the timer got there first
        Assert.False(refresh.IsCompleted);
        await worker.RescanPendingNowAsync(CancellationToken.None);

        Assert.True((await refresh).Succeeded);
        Assert.Equal(3, _hub.Snapshots.Count);
    }

    [Fact]
    public async Task An_entry_that_has_gone_is_dropped_from_the_snapshot()
    {
        var (worker, rescans) = CreateWorker();
        await worker.FullScanNowAsync(CancellationToken.None);
        // Git marks object files read-only on Windows, which Directory.Delete refuses.
        foreach (var file in Directory.EnumerateFiles(Gadget, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Gadget, recursive: true);

        var refresh = Assert.Single(rescans.Refresh([Gadget]));
        await worker.RescanPendingNowAsync(CancellationToken.None);
        var outcome = await refresh;

        Assert.True(outcome.Succeeded);
        Assert.Contains("no longer exists", outcome.Message);
        Assert.DoesNotContain(_hub.Snapshots[^1].Entries, e => e.Name == "gadget");
    }

    [Fact]
    public async Task Failures_are_reported_a_refresh_before_the_first_scan_a_fetch_that_fails_a_hub_that_is_down()
    {
        var (worker, rescans) = CreateWorker();
        var early = Assert.Single(rescans.Refresh([Widget]));
        await worker.RescanPendingNowAsync(CancellationToken.None);
        Assert.False((await early).Succeeded);
        Assert.Contains("first scan", (await early).Message);

        await worker.FullScanNowAsync(CancellationToken.None);

        // The remote has gone: the rescan still happens and is pushed, and the answer says the fetch did not work.
        Git(Widget, "remote", "set-url", "origin", Path.Combine(_root, "origin", "missing.git"));
        File.WriteAllText(Path.Combine(Widget, "new.txt"), "local change");
        var noRemote = Assert.Single(rescans.Refresh([Widget]));
        await worker.RescanPendingNowAsync(CancellationToken.None);
        Assert.False((await noRemote).Succeeded);
        Assert.Contains("git fetch failed", (await noRemote).Message);
        Assert.Equal(1, Entry(_hub.Snapshots[^1], "widget").Git!.Untracked);

        _hub.Status = HttpStatusCode.ServiceUnavailable;
        var hubDown = Assert.Single(rescans.Refresh([Gadget]));
        await worker.RescanPendingNowAsync(CancellationToken.None);
        Assert.False((await hubDown).Succeeded);
        Assert.Contains("could not be pushed", (await hubDown).Message);
    }

    [Fact]
    public async Task A_refresh_that_waits_too_long_behind_another_scan_reports_busy()
    {
        _options.Actions.RefreshTimeoutSeconds = 5; // the floor
        var (worker, rescans) = CreateWorker();
        await worker.FullScanNowAsync(CancellationToken.None);

        // Nobody runs the scan loop, as if a long full scan were under way.
        var stopwatch = Stopwatch.StartNew();
        var outcome = await Executor(rescans).ExecuteAsync(AgentAction.Refresh(Widget), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("busy with another scan", outcome.Output);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 4, 30);

        // The request is still there, so the entry is rescanned when the loop gets to it.
        await worker.RescanPendingNowAsync(CancellationToken.None);
        Assert.Equal(2, _hub.Snapshots.Count);
    }

    // ---- polling --------------------------------------------------------------------------------

    [Fact]
    public void The_poll_loop_asks_again_at_once_after_a_long_poll_and_falls_back_to_the_interval_otherwise()
    {
        var wait = TimeSpan.FromSeconds(25);
        var poll = TimeSpan.FromSeconds(30);
        static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

        // Work arrived: ask again straight away.
        Assert.Equal(TimeSpan.Zero, ActionWorker.NextDelay(new(1, false), S(0.1), wait, poll, 0));

        // The hub held the request and it timed out empty: that was the wait, so go straight back.
        Assert.Equal(TimeSpan.Zero, ActionWorker.NextDelay(new(0, false), S(25.1), wait, poll, 0));

        // An empty answer at once: a hub that does not hold polls. One request per interval, as before 0.5.0.
        Assert.Equal(S(29.9), ActionWorker.NextDelay(new(0, false), S(0.1), wait, poll, 0));
        Assert.Equal(S(29.9), ActionWorker.NextDelay(new(0, false), S(0.1), TimeSpan.Zero, poll, 0)); // long poll switched off

        // The hub failed: back off, quickly at first, never slower than the interval.
        Assert.Equal(S(2), ActionWorker.NextDelay(new(0, true), S(0.1), wait, poll, 1));
        Assert.Equal(S(4), ActionWorker.NextDelay(new(0, true), S(0.1), wait, poll, 2));
        Assert.Equal(S(16), ActionWorker.NextDelay(new(0, true), S(0.1), wait, poll, 4));
        Assert.Equal(poll, ActionWorker.NextDelay(new(0, true), S(0.1), wait, poll, 5));
        Assert.Equal(poll, ActionWorker.NextDelay(new(0, true), S(40), wait, poll, 500));
    }

    [Fact]
    public async Task The_client_asks_the_hub_to_hold_the_poll_and_gives_up_on_one_that_never_answers()
    {
        var seen = new List<string>();
        var handler = new StubHandler(request =>
        {
            seen.Add(request.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ActionClaimResponse(), options: SnapshotSchema.JsonOptions) };
        });
        var client = new ActionsClient(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, _monitor);

        Assert.Empty(await client.ClaimAsync(CancellationToken.None, waitSeconds: 25));
        Assert.Empty(await client.ClaimAsync(CancellationToken.None));
        Assert.Empty(await client.ClaimAsync(CancellationToken.None, waitSeconds: 100_000)); // never more than the hub would allow
        Assert.Equal(["/api/v1/actions/claim?wait=25", "/api/v1/actions/claim", $"/api/v1/actions/claim?wait={AgentActions.MaxClaimWaitSeconds}"], seen);

        // Shutting down ends a poll that is being held.
        using var stopping = new CancellationTokenSource();
        var hanging = new ActionsClient(new HttpClient(new StubHandler(_ => null)) { Timeout = Timeout.InfiniteTimeSpan }, _monitor);
        var held = hanging.ClaimAsync(stopping.Token, waitSeconds: 25);
        await Task.Delay(100);
        Assert.False(held.IsCompleted);
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
    }

    /// <summary>Test-only git, used to build fixtures. The agent itself can only go through <see cref="GitRunner"/>.</summary>
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
        catch (IOException)
        {
            // best effort
        }
    }

    /// <summary>Stands in for the hub's ingest endpoint and keeps every snapshot it is sent.</summary>
    private sealed class RecordingHub : HttpMessageHandler
    {
        public List<MachineSnapshot> Snapshots { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status);
            }

            var snapshot = (await request.Content!.ReadFromJsonAsync<MachineSnapshot>(SnapshotSchema.JsonOptions, cancellationToken))!;
            Snapshots.Add(snapshot);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new IngestResponse { MachineId = snapshot.Machine.MachineId, Entries = snapshot.Entries.Count }, options: SnapshotSchema.JsonOptions),
            };
        }
    }

    /// <summary>Answers with what the function returns, or never when it returns null.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage?> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (respond(request) is { } response)
            {
                return response;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class StaticOptions(AgentOptions value) : IOptionsMonitor<AgentOptions>
    {
        public AgentOptions CurrentValue => value;

        public AgentOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AgentOptions, string?> listener) => null;
    }
}
