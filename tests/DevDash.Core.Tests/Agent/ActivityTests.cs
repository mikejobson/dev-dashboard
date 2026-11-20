using System.Diagnostics;
using DevDash.Agent;
using DevDash.Agent.Dependencies;
using DevDash.Agent.Git;
using DevDash.Agent.Scanning;
using DevDash.Agent.Toolchains;
using DevDash.Contracts;
using DevDash.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevDash.Core.Tests.Agent;

public class ActivityParseTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    [Fact]
    public void Counts_commits_per_author_day_and_which_are_mine_without_keeping_any_email()
    {
        var log = string.Join('\n',
            "2026-11-20\u001fme@example.test",
            "2026-11-20\u001fME@Example.Test",
            "2026-11-20\u001fsomeone@example.test",
            "2026-11-03\u001fsomeone@example.test",
            "2025-11-21\u001fme@example.test",   // the first day of the window
            "2025-11-20\u001fme@example.test",   // one day too old: authored long ago, committed (rebased) this year
            "2026-11-21\u001fme@example.test",   // an author a time zone ahead
            "2026-11-25\u001fme@example.test",   // a clock that is wrong
            "not a date\u001fme@example.test",
            "");

        var activity = ActivityCollector.Parse(log, "me@example.test", Today);

        Assert.Equal(new Dictionary<string, int> { ["2025-11-21"] = 1, ["2026-11-03"] = 1, ["2026-11-20"] = 3, ["2026-11-21"] = 1 }, activity.Days);
        Assert.Equal(new Dictionary<string, int> { ["2025-11-21"] = 1, ["2026-11-20"] = 2, ["2026-11-21"] = 1 }, activity.Mine);
        Assert.DoesNotContain("example.test", System.Text.Json.JsonSerializer.Serialize(activity, SnapshotSchema.JsonOptions));
    }

    [Fact]
    public void Without_a_configured_email_there_is_no_mine_at_all()
    {
        var activity = ActivityCollector.Parse("2026-11-20\u001fme@example.test\n", "", Today);

        Assert.Equal(1, activity.Days["2026-11-20"]);
        Assert.Null(activity.Mine);
        Assert.Empty(ActivityCollector.Parse("", null, Today).Days);
    }
}

/// <summary>The collectors of this part on real, throwaway repositories: activity, pins, and what the scanner puts in a snapshot.</summary>
public sealed class ActivityScanTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("devdash-activity-").FullName;
    private readonly string _dev;
    private readonly string _repo;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));

    public ActivityScanTests()
    {
        _dev = Directory.CreateDirectory(Path.Combine(_root, "Developer")).FullName;
        _repo = Directory.CreateDirectory(Path.Combine(_dev, "widget")).FullName;
        Git(_repo, "init", "-q", "-b", "main");
        Git(_repo, "config", "user.email", "me@example.test");
        Git(_repo, "config", "user.name", "Me");
        Commit("first", "2026-11-18T10:00:00+00:00");
        Commit("second", "2026-11-18T15:00:00+00:00");
        Commit("theirs", "2026-11-19T09:00:00+00:00", "someone@example.test");
    }

    public void Dispose()
    {
        try
        {
            // Git marks object files read-only on some platforms.
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

    private void Commit(string name, string date, string? email = null, string? repo = null)
    {
        repo ??= _repo;
        File.WriteAllText(Path.Combine(repo, name + ".txt"), name);
        Git(repo, "add", ".");
        GitAt(repo, date, email, "commit", "-q", "-m", name);
    }

    [Fact]
    public async Task Counts_every_branch_but_not_stashes_and_reads_the_log_again_only_when_a_ref_moves()
    {
        // A side branch, and a stash (which git keeps as two commits that are not work).
        Git(_repo, "checkout", "-q", "-b", "side");
        Commit("on-side", "2026-11-20T08:00:00+00:00");
        Git(_repo, "checkout", "-q", "main");
        File.WriteAllText(Path.Combine(_repo, "first.txt"), "edited");
        GitAt(_repo, "2026-11-20T09:00:00+00:00", null, "stash", "push", "-q");

        var collector = new ActivityCollector(new GitRunner(), _clock);
        var commonDir = Path.Combine(_repo, ".git");

        var activity = (await collector.CollectAsync(_repo, commonDir, TimeSpan.FromSeconds(30), CancellationToken.None))!;

        Assert.Equal(new Dictionary<string, int> { ["2026-11-18"] = 2, ["2026-11-19"] = 1, ["2026-11-20"] = 1 }, activity.Days);
        Assert.Equal(new Dictionary<string, int> { ["2026-11-18"] = 2, ["2026-11-20"] = 1 }, activity.Mine);
        Assert.Equal(1, collector.LogRuns);

        // Nothing moved: the cached answer, and no second log.
        Assert.Same(activity, await collector.CollectAsync(_repo, commonDir, TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.Equal(1, collector.LogRuns);

        // A new commit moves a ref.
        Commit("third", "2026-11-20T11:00:00+00:00");
        var after = (await collector.CollectAsync(_repo, commonDir, TimeSpan.FromSeconds(30), CancellationToken.None))!;
        Assert.Equal(2, after.Days["2026-11-20"]);
        Assert.Equal(2, collector.LogRuns);

        // So does midnight: the window slides.
        _clock.Now += TimeSpan.FromDays(1);
        await collector.CollectAsync(_repo, commonDir, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(3, collector.LogRuns);
    }

    [Fact]
    public async Task A_repository_with_no_commits_has_no_activity()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_dev, "empty")).FullName;
        Git(empty, "init", "-q", "-b", "main");

        var activity = await new ActivityCollector(new GitRunner(), _clock).CollectAsync(empty, Path.Combine(empty, ".git"), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(activity is null || activity.Days.Count == 0);
    }

    [Fact]
    public async Task A_snapshot_carries_activity_once_per_repository_pins_toolchains_and_the_last_dependency_check()
    {
        File.WriteAllText(Path.Combine(_repo, "global.json"), """{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }""");
        File.WriteAllText(Path.Combine(_repo, ".nvmrc"), "20\n");
        Commit("pins", "2026-11-19T12:00:00+00:00");
        Directory.CreateDirectory(Path.Combine(_dev, "widget.worktrees"));
        Git(_repo, "worktree", "add", "-q", Path.Combine(_dev, "widget.worktrees", "hotfix"), "-b", "hotfix");

        var options = new TestOptions(new AgentOptions { Roots = [_dev], MachineId = "test-machine", AutoFetch = new AutoFetchOptions { Enabled = false } });
        var git = new GitRunner();
        var tools = new FakeTools().On("node", "--version", "v20.11.0\n");
        var machine = new FakeMachine();
        try
        {
            machine.Tool("opt", "homebrew", "bin", "node");
            var toolchains = new ToolchainService(new ToolchainCollector(tools, new ToolLocator(machine.Environment()), _clock), options, NullLogger<ToolchainService>.Instance, _clock);
            var deps = new DependencyCache(Path.Combine(_root, "deps.json"));
            var health = new DependencyHealth { CheckedAt = _clock.Now, Reports = [new DependencyReport { Ecosystem = DependencyReport.Npm, Vulnerable = new SeverityCounts { High = 1 } }] };
            deps.Set(_repo, new DependencyCacheEntry(health, _clock.Now));
            var targets = new DependencyTargets();
            var scanner = new MachineScanner(
                new RepoScanner(git, new FetchService(git, options, NullLogger<FetchService>.Instance), options, _clock),
                new SizeCache(Path.Combine(_root, "sizes.json")), options, NullLogger<MachineScanner>.Instance, _clock, toolchains, deps, targets);

            var snapshot = await scanner.ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None);

            Assert.Equal("20.11.0", snapshot.Machine.Toolchains!.Find(ToolNames.Node)!.Active);
            Assert.True(snapshot.Machine.DependencyChecks);

            var widget = snapshot.Entries.Single(e => e.Name == "widget");
            Assert.Equal([("dotnet", "10.0.100", "latestFeature"), ("node", "20", null)], widget.Pins!.Select(p => (p.Tool, p.Version, p.RollForward)));
            Assert.Equal(1, widget.Dependencies!.High);
            Assert.Equal(new Dictionary<string, int> { ["2026-11-18"] = 2, ["2026-11-19"] = 2 }, widget.Git!.Activity!.Days);
            Assert.Equal(3, widget.Git.Activity.Mine!.Values.Sum());

            // The linked worktree shares the history, so it does not send it again; it has its own files, so its own pins.
            var hotfix = snapshot.Entries.Single(e => e.Name == "widget.worktrees").Children.Single();
            Assert.True(hotfix.Git!.IsLinkedWorktree);
            Assert.Null(hotfix.Git.Activity);
            Assert.Equal(2, hotfix.Pins!.Count);
            Assert.Null(hotfix.Dependencies);

            // The background checks are told what there is to check.
            Assert.Equal([widget.Path, hotfix.Path], targets.Paths.Order());

            // Nothing private in what is sent.
            var json = System.Text.Json.JsonSerializer.Serialize(snapshot, SnapshotSchema.JsonOptions);
            Assert.DoesNotContain("example.test", json);

            // Each switch takes its part out.
            options.CurrentValue.Activity = false;
            options.CurrentValue.Toolchains.Enabled = false;
            options.CurrentValue.Dependencies.Enabled = false;
            var off = await scanner.ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None);
            var plain = off.Entries.Single(e => e.Name == "widget");
            Assert.Null(off.Machine.Toolchains);
            Assert.False(off.Machine.DependencyChecks);
            Assert.Null(plain.Pins);
            Assert.Null(plain.Dependencies);
            Assert.Null(plain.Git!.Activity);
        }
        finally
        {
            machine.Dispose();
        }
    }

    [Fact]
    public void Reading_user_email_is_the_only_config_the_scanner_may_ask_for()
    {
        GitRunner.EnsureReadOnly(["config", "--get", "user.email"]);
        Assert.Throws<InvalidOperationException>(() => GitRunner.EnsureReadOnly(["config", "user.email", "x@example.test"]));
        Assert.Throws<InvalidOperationException>(() => GitRunner.EnsureReadOnly(["config", "--get", "credential.helper"]));
        Assert.Throws<InvalidOperationException>(() => GitRunner.EnsureReadOnly(["config", "--unset", "user.email"]));
    }

    private static string Git(string workingDirectory, params string[] args) => GitAt(workingDirectory, null, null, args);

    /// <summary>Test-only git, used to build fixtures, with a fixed date and author where a test counts by day.</summary>
    private static string GitAt(string workingDirectory, string? date, string? email, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in (string[])["-c", "commit.gpgsign=false", "-c", "init.defaultBranch=main", .. args])
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        psi.Environment["GIT_AUTHOR_NAME"] = "Someone";
        psi.Environment["GIT_COMMITTER_NAME"] = "Someone";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "committer@example.test";
        psi.Environment["GIT_AUTHOR_EMAIL"] = email ?? "me@example.test";
        if (date is not null)
        {
            psi.Environment["GIT_COMMITTER_DATE"] = date;
            psi.Environment["GIT_AUTHOR_DATE"] = date;
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }
}
