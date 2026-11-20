using System.Diagnostics;
using DevDash.Agent;
using DevDash.Agent.Actions;
using DevDash.Agent.Git;
using DevDash.Agent.Scanning;
using DevDash.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDash.Core.Tests.Agent;

/// <summary>
/// Agent actions against throwaway repositories under the system temp folder: what runs, and above
/// all what is refused. Nothing here touches a real working copy.
/// </summary>
public sealed class ActionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("devdash-actions-").FullName;
    private readonly string _dev;
    private readonly string _origin;
    private readonly AgentOptions _options;

    public ActionTests()
    {
        _dev = Directory.CreateDirectory(Path.Combine(_root, "Developer")).FullName;
        _origin = Path.Combine(_root, "origin", "widget.git");

        var seed = Directory.CreateDirectory(Path.Combine(_root, "seed")).FullName;
        Git(seed, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(seed, "README.md"), "# Widget\n");
        Git(seed, "add", ".");
        Git(seed, "commit", "-q", "-m", "initial");
        Directory.CreateDirectory(Path.GetDirectoryName(_origin)!);
        Git(_root, "clone", "-q", "--bare", seed, _origin);
        Git(_dev, "clone", "-q", _origin, "widget");

        _options = new AgentOptions { Roots = [_dev], MachineId = "test-machine", Actions = new ActionsOptions { Enabled = true } };
    }

    private string Widget => Path.Combine(_dev, "widget");

    private ActionExecutor Executor()
    {
        var options = new StaticOptions(_options);
        var git = new GitRunner();
        return new ActionExecutor(git, new ActionValidator(git, options), options, NullLogger<ActionExecutor>.Instance);
    }

    private Task<ActionOutcome> Run(AgentAction action) => Executor().ExecuteAsync(action, CancellationToken.None);

    private bool HasBranch(string name, string? repo = null) =>
        Git(repo ?? Widget, "for-each-ref", "--format=%(refname:short)", "refs/heads").Split('\n').Contains(name);

    private void Commit(string repo, string file, string message)
    {
        File.WriteAllText(Path.Combine(repo, file), message);
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", message);
    }

    // ---- off by default -------------------------------------------------------------------------

    [Fact]
    public void Actions_are_off_unless_the_machine_turns_them_on()
    {
        Assert.False(new AgentOptions().Actions.Enabled);
        Assert.False(AgentCli.Parse(["run"]).Overrides.ContainsKey("Agent:Actions:Enabled"));
        Assert.Equal("true", AgentCli.Parse(["run", "--enable-actions"]).Overrides["Agent:Actions:Enabled"]);
    }

    [Fact]
    public async Task A_disabled_agent_refuses_everything_and_changes_nothing()
    {
        Git(Widget, "branch", "merged");
        _options.Actions.Enabled = false;

        var outcome = await Run(AgentAction.DeleteBranches(Widget, ["merged"]));

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.Refused);
        Assert.Contains("not enabled", outcome.Output);
        Assert.True(HasBranch("merged"));
    }

    [Fact]
    public async Task The_snapshot_says_whether_actions_are_enabled()
    {
        Assert.True((await MachineInfoProvider.GetAsync(_options, CancellationToken.None)).ActionsEnabled);
        Assert.False((await MachineInfoProvider.GetAsync(new AgentOptions(), CancellationToken.None)).ActionsEnabled);
    }

    // ---- delete branches ------------------------------------------------------------------------

    [Fact]
    public async Task Deletes_merged_branches()
    {
        Git(Widget, "branch", "merged-a");
        Git(Widget, "branch", "feature/merged-b");

        var outcome = await Run(AgentAction.DeleteBranches(Widget, ["merged-a", "feature/merged-b"]));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.Equal(0, outcome.ExitCode);
        Assert.False(HasBranch("merged-a"));
        Assert.False(HasBranch("feature/merged-b"));
        Assert.Equal([Widget], outcome.AffectedPaths);
    }

    [Fact]
    public async Task Git_refuses_an_unmerged_branch_and_the_agent_never_forces_it()
    {
        Git(Widget, "checkout", "-q", "-b", "unmerged");
        Commit(Widget, "work.txt", "work that exists nowhere else");
        Git(Widget, "checkout", "-q", "main");

        var outcome = await Run(AgentAction.DeleteBranches(Widget, ["unmerged"]));

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Refused); // git ran, with -d, and said no
        Assert.NotEqual(0, outcome.ExitCode);
        Assert.Contains("not fully merged", outcome.Output);
        Assert.True(HasBranch("unmerged"));
    }

    [Fact]
    public async Task Refuses_the_default_branch_a_checked_out_branch_and_names_that_are_not_branches()
    {
        Git(Widget, "checkout", "-q", "-b", "current");
        Git(Widget, "branch", "ok");

        Assert.Contains("default branch", (await Run(AgentAction.DeleteBranches(Widget, ["main"]))).Output);
        Assert.Contains("checked out", (await Run(AgentAction.DeleteBranches(Widget, ["current"]))).Output);
        Assert.Contains("no local branch", (await Run(AgentAction.DeleteBranches(Widget, ["nope"]))).Output);

        // One bad name refuses the whole action: the good branch is left alone.
        var mixed = await Run(AgentAction.DeleteBranches(Widget, ["ok", "nope"]));
        Assert.True(mixed.Refused);
        Assert.True(HasBranch("ok"));

        foreach (var name in new[] { "-D", "--force", "a b", "a..b", "x;rm -rf ~", "$(touch pwned)", "" })
        {
            var outcome = await Run(AgentAction.DeleteBranches(Widget, [name]));
            Assert.True(outcome.Refused, name);
        }

        Assert.True(HasBranch("main"));
        Assert.True(HasBranch("current"));
        Assert.False(File.Exists(Path.Combine(Widget, "pwned")));
    }

    // ---- which repositories ---------------------------------------------------------------------

    [Fact]
    public async Task Refuses_repositories_outside_the_scan_roots()
    {
        var outside = Path.Combine(_root, "elsewhere", "widget");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        Git(_root, "clone", "-q", _origin, outside);
        Git(outside, "branch", "merged");

        var outcome = await Run(AgentAction.DeleteBranches(outside, ["merged"]));

        Assert.True(outcome.Refused);
        Assert.Contains("outside this agent's scan roots", outcome.Output);
        Assert.True(HasBranch("merged", outside));

        // Dressing the same path up to look like it is under the root does not help.
        var sneaky = await Run(AgentAction.DeleteBranches(Path.Combine(_dev, "..", "elsewhere", "widget"), ["merged"]));
        Assert.True(sneaky.Refused);
        Assert.True(HasBranch("merged", outside));
    }

    [Fact]
    public async Task Refuses_a_link_inside_the_roots_that_points_outside_them()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating symlinks needs a privilege there
        }

        var outside = Path.Combine(_root, "elsewhere", "widget");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        Git(_root, "clone", "-q", _origin, outside);
        Git(outside, "branch", "merged");
        var link = Path.Combine(_dev, "linked");
        Directory.CreateSymbolicLink(link, outside);

        var outcome = await Run(AgentAction.DeleteBranches(link, ["merged"]));

        Assert.True(outcome.Refused);
        Assert.True(HasBranch("merged", outside));
    }

    [Fact]
    public async Task Refuses_paths_that_are_not_the_top_of_a_scanned_repository()
    {
        Directory.CreateDirectory(Path.Combine(Widget, "src"));
        Directory.CreateDirectory(Path.Combine(_dev, "plain-folder"));
        Directory.CreateDirectory(Path.Combine(_dev, "a", "b"));
        Git(Path.Combine(_dev, "a", "b"), "clone", "-q", _origin, "too-deep");

        foreach (var path in new[]
        {
            Path.Combine(Widget, "src"),          // inside a repo, not its top
            Path.Combine(_dev, "plain-folder"),   // not a repo
            Path.Combine(_dev, "missing"),        // not there
            Path.Combine(_dev, "a", "b", "too-deep"), // deeper than the scanner looks
            _dev,                                 // the root itself
            "widget",                             // relative
        })
        {
            var outcome = await Run(AgentAction.PruneWorktrees(path));
            Assert.True(outcome.Refused, path);
        }
    }

    [Fact]
    public async Task Refuses_an_action_whose_fields_do_not_fit_its_kind()
    {
        var outcome = await Run(AgentAction.Fetch(Widget) with { Branches = ["main"] });

        Assert.True(outcome.Refused);
    }

    // ---- push -----------------------------------------------------------------------------------

    [Fact]
    public async Task Pushes_a_branch_that_is_ahead()
    {
        Commit(Widget, "new.txt", "local work");
        var head = Git(Widget, "rev-parse", "HEAD");

        var outcome = await Run(AgentAction.Push(Widget, "origin", "main"));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.Equal(head, Git(_origin, "rev-parse", "refs/heads/main"));
    }

    [Fact]
    public async Task Refuses_to_push_a_branch_that_is_behind_even_when_the_clone_has_not_fetched_yet()
    {
        // Someone else pushes; our clone has local work and does not know it is behind.
        var other = Path.Combine(_root, "other");
        Git(_root, "clone", "-q", _origin, other);
        Commit(other, "theirs.txt", "their work");
        Git(other, "push", "-q", "origin", "main");
        var remoteHead = Git(_origin, "rev-parse", "refs/heads/main");
        Commit(Widget, "mine.txt", "my work");
        var localHead = Git(Widget, "rev-parse", "HEAD");

        var outcome = await Run(AgentAction.Push(Widget, "origin", "main"));

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.Refused);
        Assert.Contains("1 commit(s) behind origin/main", outcome.Output);
        Assert.Equal(remoteHead, Git(_origin, "rev-parse", "refs/heads/main")); // the remote is untouched
        Assert.Equal(localHead, Git(Widget, "rev-parse", "HEAD"));              // and so is the local branch: no rebase, no merge
        Assert.Equal("", Git(Widget, "status", "--porcelain"));
    }

    [Fact]
    public async Task Publishes_a_new_branch_and_sets_its_upstream()
    {
        Git(Widget, "checkout", "-q", "-b", "feature/new");
        Commit(Widget, "feature.txt", "feature");

        var outcome = await Run(AgentAction.Push(Widget, "origin", "feature/new", setUpstream: true));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.Equal(Git(Widget, "rev-parse", "HEAD"), Git(_origin, "rev-parse", "refs/heads/feature/new"));
        Assert.Equal("origin/feature/new", Git(Widget, "rev-parse", "--abbrev-ref", "feature/new@{upstream}"));
    }

    [Fact]
    public async Task Refuses_a_push_to_an_unknown_remote_or_of_an_unknown_branch()
    {
        Assert.Contains("no remote named", (await Run(AgentAction.Push(Widget, "elsewhere", "main"))).Output);
        Assert.Contains("no local branch", (await Run(AgentAction.Push(Widget, "origin", "nope"))).Output);
    }

    // ---- stash ----------------------------------------------------------------------------------

    private string Stash(string content)
    {
        File.WriteAllText(Path.Combine(Widget, "README.md"), content);
        Git(Widget, "stash", "push", "-q", "-m", content);
        return Git(Widget, "rev-parse", "stash@{0}");
    }

    [Fact]
    public async Task Drops_the_stash_it_was_shown()
    {
        var older = Stash("older");
        var newer = Stash("newer");

        var outcome = await Run(AgentAction.DropStash(Widget, 1, older));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.Equal(newer, Git(Widget, "rev-parse", "stash@{0}"));
        Assert.Single(Git(Widget, "stash", "list").Split('\n'));
    }

    [Fact]
    public async Task Refuses_to_drop_when_the_stash_at_that_index_has_changed()
    {
        var older = Stash("older");
        var newer = Stash("newer");

        // The dashboard saw "older" at index 0 before "newer" was pushed on top of it.
        var stale = await Run(AgentAction.DropStash(Widget, 0, older));

        Assert.True(stale.Refused);
        Assert.Contains("no longer the stash that was shown", stale.Output);
        Assert.Equal(newer, Git(Widget, "rev-parse", "stash@{0}"));
        Assert.Equal(older, Git(Widget, "rev-parse", "stash@{1}"));

        var missing = await Run(AgentAction.DropStash(Widget, 7, older));
        Assert.True(missing.Refused);
        Assert.Equal(2, Git(Widget, "stash", "list").Split('\n').Length);
    }

    [Fact]
    public async Task The_scanner_reports_each_stash_commit_so_the_drop_can_be_pinned_to_it()
    {
        var sha = Stash("parked");
        var options = new StaticOptions(_options);
        var git = new GitRunner();

        var state = await new RepoScanner(git, new FetchService(git, options, NullLogger<FetchService>.Instance), options).ScanAsync(Widget, allowFetch: false, CancellationToken.None);

        Assert.Equal(sha, Assert.Single(state.Stashes).Sha);
    }

    // ---- worktrees ------------------------------------------------------------------------------

    private string AddWorktree(string name, string branch)
    {
        var path = Path.Combine(_dev, "widget.worktrees", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Git(Widget, "worktree", "add", "-q", path, "-b", branch, "main");
        return path;
    }

    [Fact]
    public async Task Removes_a_clean_linked_worktree_and_keeps_its_branch()
    {
        var path = AddWorktree("done", "done");

        var outcome = await Run(AgentAction.RemoveWorktree(Widget, path));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.False(Directory.Exists(path));
        Assert.True(HasBranch("done"));
        Assert.Equal([Widget, path], outcome.AffectedPaths);
    }

    [Fact]
    public async Task Git_refuses_a_worktree_with_uncommitted_work_and_the_agent_never_forces_it()
    {
        var path = AddWorktree("busy", "busy");
        File.WriteAllText(Path.Combine(path, "scratch.txt"), "not committed");

        var outcome = await Run(AgentAction.RemoveWorktree(Widget, path));

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Refused);
        Assert.True(File.Exists(Path.Combine(path, "scratch.txt")));
    }

    [Fact]
    public async Task Refuses_worktree_paths_that_are_not_removable_linked_worktrees_of_that_repository()
    {
        var locked = AddWorktree("locked", "locked");
        Git(Widget, "worktree", "lock", locked);
        var detached = Path.Combine(_dev, "widget.worktrees", "detached");
        Git(Widget, "worktree", "add", "-q", "--detach", detached, "main");
        Git(_dev, "clone", "-q", _origin, "other-repo");

        Assert.Contains("main working tree", (await Run(AgentAction.RemoveWorktree(Widget, Widget))).Output);
        Assert.Contains("locked", (await Run(AgentAction.RemoveWorktree(Widget, locked))).Output);
        Assert.Contains("detached", (await Run(AgentAction.RemoveWorktree(Widget, detached))).Output);
        Assert.Contains("not a worktree of", (await Run(AgentAction.RemoveWorktree(Widget, Path.Combine(_dev, "other-repo")))).Output);
        Assert.Contains("not a worktree of", (await Run(AgentAction.RemoveWorktree(Widget, Path.Combine(_root, "seed")))).Output);

        // A real, clean linked worktree, but outside every scan root: not the agent's to delete.
        var outside = Path.Combine(_root, "outside-worktree");
        Git(Widget, "worktree", "add", "-q", outside, "-b", "outside", "main");
        var refused = await Run(AgentAction.RemoveWorktree(Widget, outside));
        Assert.True(refused.Refused, refused.Output);
        Assert.Contains("outside this agent's scan roots", refused.Output);
        Assert.True(Directory.Exists(outside));

        Assert.True(Directory.Exists(locked));
        Assert.True(Directory.Exists(detached));
        Assert.True(Directory.Exists(Path.Combine(_dev, "other-repo")));
        Assert.True(Directory.Exists(Path.Combine(_root, "seed")));
    }

    [Fact]
    public async Task Prunes_worktrees_whose_folder_is_gone()
    {
        var path = AddWorktree("gone", "gone");
        Directory.Delete(path, recursive: true);
        Assert.Contains("prunable", Git(Widget, "worktree", "list", "--porcelain"));

        var outcome = await Run(AgentAction.PruneWorktrees(Widget));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.DoesNotContain("prunable", Git(Widget, "worktree", "list", "--porcelain"));
        Assert.True(HasBranch("gone"));
    }

    // ---- fetch ----------------------------------------------------------------------------------

    [Fact]
    public async Task Fetch_updates_remote_tracking_refs_only()
    {
        var other = Path.Combine(_root, "other");
        Git(_root, "clone", "-q", _origin, other);
        Commit(other, "theirs.txt", "their work");
        Git(other, "push", "-q", "origin", "main");
        var head = Git(Widget, "rev-parse", "HEAD");

        var outcome = await Run(AgentAction.Fetch(Widget));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.Equal(Git(_origin, "rev-parse", "refs/heads/main"), Git(Widget, "rev-parse", "refs/remotes/origin/main"));
        Assert.Equal(head, Git(Widget, "rev-parse", "HEAD"));
    }

    // ---- rescan ---------------------------------------------------------------------------------

    [Fact]
    public void A_path_maps_to_the_top_level_entry_to_rescan()
    {
        var worktree = Path.Combine(_dev, "widget.worktrees", "done");

        Assert.Equal((_dev, "widget"), RescanRequests.Find([_dev], Widget));
        Assert.Equal((_dev, "widget.worktrees"), RescanRequests.Find([_dev], worktree));
        Assert.Null(RescanRequests.Find([_dev], Path.Combine(_root, "seed")));
        Assert.Null(RescanRequests.Find([_dev], _dev));

        var seen = new List<(string, string)>();
        var requests = new RescanRequests(new StaticOptions(_options));
        requests.Requested += (root, name) => seen.Add((root, name));
        Assert.True(requests.Request(Widget));
        Assert.False(requests.Request(_root));
        Assert.Equal([(_dev, "widget")], seen);
    }

    /// <summary>Test-only git, used to build fixtures and to check results.</summary>
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

    private sealed class StaticOptions(AgentOptions value) : IOptionsMonitor<AgentOptions>
    {
        public AgentOptions CurrentValue => value;

        public AgentOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AgentOptions, string?> listener) => null;
    }
}
