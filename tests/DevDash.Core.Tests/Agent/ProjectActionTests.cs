using System.Diagnostics;
using DevDash.Agent;
using DevDash.Agent.Actions;
using DevDash.Agent.Git;
using DevDash.Agent.Scanning;
using DevDash.Contracts;
using DevDash.Core.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDash.Core.Tests.Agent;

/// <summary>
/// The 0.6.0 actions against throwaway repositories under the system temp folder: remove a project,
/// clone it back, delete build output, fast-forward a branch. Mostly what is refused. The trash is a
/// folder inside the test's own temp folder; nothing here goes near a real one, or a real working copy.
/// </summary>
public sealed class ProjectActionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("devdash-projects-").FullName;
    private readonly string _dev;
    private readonly string _origin;
    private readonly string _seed;
    private readonly string _trash;
    private readonly AgentOptions _options;
    private readonly StaticOptions _monitor;
    private readonly GitRunner _git = new();
    private readonly MachineScanner _scanner;
    private readonly RemovalLog _removals;
    private readonly KnownRemotes _known = new();

    public ProjectActionTests()
    {
        _dev = Directory.CreateDirectory(Path.Combine(_root, "Developer")).FullName;
        _trash = Path.Combine(_root, "trash");
        _origin = Path.Combine(_root, "origin", "widget.git");

        _seed = Directory.CreateDirectory(Path.Combine(_root, "seed")).FullName;
        Git(_seed, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(_seed, "README.md"), "# Widget\n");
        Git(_seed, "add", ".");
        Git(_seed, "commit", "-q", "-m", "initial");
        Directory.CreateDirectory(Path.GetDirectoryName(_origin)!);
        Git(_root, "clone", "-q", "--bare", _seed, _origin);
        Git(_seed, "remote", "add", "origin", _origin);
        Git(_dev, "clone", "-q", _origin, "widget");

        _options = new AgentOptions
        {
            Roots = [_dev],
            MachineId = "test-machine",
            Watch = false,
            Actions = new ActionsOptions { Enabled = true },
            AutoFetch = new AutoFetchOptions { Enabled = true, IntervalHours = 4, TimeoutSeconds = 30 },
        };
        _monitor = new StaticOptions(_options);
        _scanner = new MachineScanner(
            new RepoScanner(_git, new FetchService(_git, _monitor, NullLogger<FetchService>.Instance), _monitor),
            new SizeCache(Path.Combine(_root, "sizes.json")), _monitor, NullLogger<MachineScanner>.Instance);
        _removals = new RemovalLog(Path.Combine(_root, "removed.json"));
    }

    private string Widget => Path.Combine(_dev, "widget");

    private ActionExecutor Executor(ITrash? trash = null)
    {
        var validator = new ActionValidator(_git, _monitor, _removals, _known);
        var projects = new ProjectActions(_git, validator, _monitor, NullLogger<ProjectActions>.Instance, _scanner, trash ?? new FolderTrash(_trash), _removals);
        return new ActionExecutor(_git, validator, _monitor, NullLogger<ActionExecutor>.Instance, projects: projects);
    }

    private Task<ActionOutcome> Run(AgentAction action, ITrash? trash = null) => Executor(trash).ExecuteAsync(action, CancellationToken.None);

    /// <summary>What the hub would have shown: a scan of the folder and its assessment.</summary>
    private async Task<SafetyAssessment> AssessAsync(string path, bool includeRepository = true)
    {
        var entry = await _scanner.ScanRepoAsync(_dev, path, new ScanRequest(AllowFetch: true, SizeMode.CachedOnly), CancellationToken.None);
        return RepoSafety.Assess(entry, DateTimeOffset.UtcNow, includeRepository);
    }

    private async Task<AgentAction> RemovalOf(string path, bool includeRepository = true, IEnumerable<string>? acknowledged = null) =>
        AgentAction.RemoveProject(path, (await AssessAsync(path, includeRepository)).Fingerprint, acknowledged);

    private void Commit(string repo, string file, string message)
    {
        File.WriteAllText(Path.Combine(repo, file), message);
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", message);
    }

    /// <summary>A commit on the remote that the clone has not fetched.</summary>
    private void CommitUpstream(string message)
    {
        File.WriteAllText(Path.Combine(_seed, "upstream.txt"), message);
        Git(_seed, "add", ".");
        Git(_seed, "commit", "-q", "-m", message);
        Git(_seed, "push", "-q", "origin", "main");
    }

    private static void AssertRefused(ActionOutcome outcome, string because)
    {
        Assert.False(outcome.Succeeded, outcome.Output);
        Assert.True(outcome.Refused, outcome.Output);
        Assert.Contains(because, outcome.Output);
    }

    // ---- shape: the right fields for the kind, and nothing that could be read as an option ---------

    public static TheoryData<AgentAction> Malformed => new()
    {
        new AgentAction { Kind = AgentActionKind.RemoveProject, RepoPath = "/dev/x" }, // no fingerprint
        AgentAction.RemoveProject("/dev/x", "not-a-fingerprint"),
        AgentAction.RemoveProject("/dev/x", "ABCDEFABCDEFABCDEFABCDEF"),
        AgentAction.RemoveProject("relative/x", "0123456789abcdef01234567"),
        AgentAction.RemoveProject("/dev/x", "0123456789abcdef01234567") with { Branch = "main" },
        AgentAction.RemoveProject("/dev/x", "0123456789abcdef01234567") with { BuildDirs = ["/dev/x/node_modules"] },
        AgentAction.RemoveProject("/dev/x", "0123456789abcdef01234567") with { AcknowledgedCaveats = [] },
        AgentAction.RemoveProject("/dev/x", "0123456789abcdef01234567") with { AcknowledgedCaveats = ["a\nb"] },
        new AgentAction { Kind = AgentActionKind.Clone, RepoPath = "/dev/x" }, // no URL
        AgentAction.CloneInto("/dev/x", "--upload-pack=touch pwned"),
        AgentAction.CloneInto("/dev/x", "-oProxyCommand=x"),
        AgentAction.CloneInto("/dev/x", "ext::sh -c touch% pwned"),
        AgentAction.CloneInto("/dev/x", "ext::ssh://example.invalid/x"),
        AgentAction.CloneInto("/dev/x", "https://example.invalid/a b.git"),
        AgentAction.CloneInto("/dev/x", "relative/path"),
        AgentAction.CloneInto("/dev/x", "ftp://example.invalid/x.git"),
        AgentAction.CloneInto("/dev/x", "https://example.invalid/x.git", "-o"),
        AgentAction.CloneInto("x", "https://example.invalid/x.git"),
        AgentAction.CloneInto("/dev/x", "https://example.invalid/x.git") with { Branch = "main" },
        new AgentAction { Kind = AgentActionKind.CleanBuildDirs, RepoPath = "/dev/x" }, // no folders
        AgentAction.CleanBuildDirs("/dev/x", []),
        AgentAction.CleanBuildDirs("/dev/x", ["node_modules"]),
        AgentAction.CleanBuildDirs("/dev/x", ["/dev/x/node_modules", "/dev/x/node_modules"]),
        AgentAction.CleanBuildDirs("/dev/x", Enumerable.Range(0, AgentActions.MaxBuildDirs + 1).Select(i => $"/dev/x/p{i}/node_modules")),
        AgentAction.CleanBuildDirs("/dev/x", ["/dev/x/node_modules"]) with { Url = "https://example.invalid/x.git" },
        new AgentAction { Kind = AgentActionKind.PullFastForward, RepoPath = "/dev/x", Branch = "main" }, // no remote
        AgentAction.PullFastForward("/dev/x", "--force", "main"),
        AgentAction.PullFastForward("/dev/x", "origin", "-f"),
        AgentAction.PullFastForward("/dev/x", "origin", "a..b"),
        AgentAction.PullFastForward("/dev/x", "origin", "main") with { SetUpstream = true },
        AgentAction.PullFastForward("/dev/x", "origin", "main") with { ExpectedFingerprint = "0123456789abcdef01234567" },
        // The new fields on the old kinds.
        AgentAction.Fetch("/dev/x") with { Url = "https://example.invalid/x.git" },
        AgentAction.Fetch("/dev/x") with { BuildDirs = ["/dev/x/bin"] },
        AgentAction.PruneWorktrees("/dev/x") with { ExpectedFingerprint = "0123456789abcdef01234567" },
        AgentAction.Push("/dev/x", "origin", "main") with { AcknowledgedCaveats = [".env"] },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task Malformed_actions_are_refused_before_anything_is_looked_at(AgentAction action)
    {
        Assert.NotNull(AgentActions.Validate(action));

        var outcome = await Run(action);
        Assert.True(outcome.Refused, outcome.Output);
        Assert.True(Directory.Exists(Widget));
    }

    [Fact]
    public void Well_formed_actions_pass_the_shape_check_and_say_what_they_do()
    {
        var remove = AgentAction.RemoveProject("/dev/x", "0123456789abcdef01234567", [".env"]);
        var clone = AgentAction.CloneInto("/dev/x", "git@github.com:acme/x.git", "upstream");
        var clean = AgentAction.CleanBuildDirs("/dev/x", ["/dev/x/node_modules", "/dev/x/it's/bin"]);
        var pull = AgentAction.PullFastForward("/dev/x", "origin", "feature/y");
        Assert.All((AgentAction[])[remove, clone, clean, pull, AgentAction.CloneInto("/dev/x", "https://github.com/acme/x.git"), AgentAction.CloneInto("/dev/x", "ssh://git@example.invalid:22/x.git"), AgentAction.CloneInto("/dev/x", "/srv/git/x.git")],
            a => Assert.Null(AgentActions.Validate(a)));

        Assert.Equal("trash '/dev/x'", AgentActions.CommandLine(remove));
        Assert.Equal("git clone --origin 'upstream' -- 'git@github.com:acme/x.git' '/dev/x'", AgentActions.CommandLine(clone));
        Assert.Equal(@"rm -rf '/dev/x/node_modules' '/dev/x/it'\''s/bin'", AgentActions.CommandLine(clean));
        Assert.Equal("git -C '/dev/x' pull --ff-only 'origin' 'feature/y'", AgentActions.CommandLine(pull));
        Assert.Contains("trash", AgentActions.Safeguard(remove));
        Assert.Contains("for good", AgentActions.Safeguard(clean));
        Assert.Contains("Fast-forward only", AgentActions.Safeguard(pull));

        // "origin" is the default and is not sent.
        Assert.Null(AgentAction.CloneInto("/dev/x", "https://github.com/acme/x.git", "origin").Remote);
        // None of them is one fixed git command.
        Assert.All((AgentAction[])[remove, clone, clean, pull], a => Assert.Throws<ArgumentOutOfRangeException>(() => AgentActions.GitArgs(a)));
        Assert.All((AgentAction[])[remove, clone, clean, pull], a => Assert.True(AgentActions.IsRepoAction(a.Kind)));
    }

    [Fact]
    public async Task The_snapshot_says_which_of_the_new_actions_the_agent_takes()
    {
        var on = await MachineInfoProvider.GetAsync(_options, CancellationToken.None);
        Assert.True(on is { AcceptsRemoveProject: true, AcceptsClone: true, AcceptsCleanBuildDirs: true, AcceptsPull: true });
        // The T3 Code kinds also need T3's CLI, which the scanner looks for (MachineScanner sets AcceptsT3); see T3Tests.
        Assert.All(Enum.GetValues<AgentActionKind>().Where(k => AgentActions.IsRepoAction(k) && !AgentActions.IsT3Action(k)), kind => Assert.True(AgentActions.Accepts(on, kind)));

        var off = await MachineInfoProvider.GetAsync(new AgentOptions(), CancellationToken.None);
        Assert.True(off is { AcceptsRemoveProject: false, AcceptsClone: false, AcceptsCleanBuildDirs: false, AcceptsPull: false });
        Assert.Contains("--enable-actions", AgentActions.WhyNotAccepted(off, AgentActionKind.RemoveProject));

        // An agent from before 0.6.0 has actions on and none of the flags: it is told to update, and old kinds still work.
        var old = new MachineInfo { MachineId = "old", Hostname = "old-host", AgentVersion = "0.5.0", ActionsEnabled = true };
        Assert.True(AgentActions.Accepts(old, AgentActionKind.DeleteBranches));
        Assert.All((AgentActionKind[])[AgentActionKind.RemoveProject, AgentActionKind.Clone, AgentActionKind.CleanBuildDirs, AgentActionKind.PullFastForward], kind =>
        {
            Assert.False(AgentActions.Accepts(old, kind));
            Assert.Contains("Update the agent on old-host to 0.6.0", AgentActions.WhyNotAccepted(old, kind));
        });
        Assert.True(Version.Parse(MachineInfoProvider.AgentVersion.Split('-')[0]) >= new Version(0, 6, 0)); // this agent takes them
    }

    // ---- remove ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_clean_fully_pushed_project_is_moved_to_the_trash_not_deleted()
    {
        var outcome = await Run(await RemovalOf(Widget));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.False(Directory.Exists(Widget));
        Assert.True(File.Exists(Path.Combine(_trash, "widget", "README.md")));
        Assert.True(Directory.Exists(Path.Combine(_trash, "widget", ".git")));
        Assert.Contains("Nothing was deleted", outcome.Output);
        Assert.Equal([Widget], outcome.AffectedPaths);
        Assert.True(_removals.Allows(Widget, _origin));
    }

    [Fact]
    public async Task A_second_project_with_the_same_name_does_not_replace_the_first_in_the_trash()
    {
        Assert.True((await Run(await RemovalOf(Widget))).Succeeded);
        Git(_dev, "clone", "-q", _origin, "widget");
        File.WriteAllText(Path.Combine(_trash, "widget", "marker.txt"), "the first one");

        var outcome = await Run(await RemovalOf(Widget));

        Assert.True(outcome.Succeeded, outcome.Output);
        var trashed = Directory.GetDirectories(_trash);
        Assert.Equal(2, trashed.Length);
        Assert.True(File.Exists(Path.Combine(_trash, "widget", "marker.txt")));
        Assert.All(trashed, dir => Assert.True(File.Exists(Path.Combine(dir, "README.md"))));
    }

    [Fact]
    public async Task A_disabled_agent_removes_nothing()
    {
        var action = await RemovalOf(Widget);
        _options.Actions.Enabled = false;

        AssertRefused(await Run(action), "not enabled");
        Assert.True(Directory.Exists(Widget));
    }

    [Fact]
    public async Task A_fingerprint_from_before_a_change_is_refused()
    {
        var before = await RemovalOf(Widget);
        Commit(Widget, "more.txt", "a commit the dashboard has not seen");
        Git(Widget, "push", "-q", "origin", "main");

        // Still safe (the commit is pushed), but not what the user was shown.
        Assert.True((await AssessAsync(Widget)).IsRemovable);
        AssertRefused(await Run(before), "has changed since it was shown");
        Assert.True(Directory.Exists(Widget));
        Assert.False(Directory.Exists(_trash));

        AssertRefused(await Run(AgentAction.RemoveProject(Widget, "0123456789abcdef01234567")), "has changed since it was shown");
    }

    [Fact]
    public async Task Work_that_exists_only_here_is_never_removed()
    {
        var clean = await RemovalOf(Widget);

        Commit(Widget, "work.txt", "an unpushed commit");
        AssertRefused(await Run(clean), "not safe to remove");
        AssertRefused(await Run(await RemovalOf(Widget)), "not on any remote"); // even quoting its current fingerprint
        Git(Widget, "push", "-q", "origin", "main");

        File.WriteAllText(Path.Combine(Widget, "README.md"), "# Widget, edited\n");
        AssertRefused(await Run(await RemovalOf(Widget)), "uncommitted change");
        Git(Widget, "stash", "-q");
        AssertRefused(await Run(await RemovalOf(Widget)), "stash");
        Git(Widget, "stash", "drop", "-q");

        File.WriteAllText(Path.Combine(Widget, "notes.txt"), "never added");
        AssertRefused(await Run(await RemovalOf(Widget)), "untracked");
        File.Delete(Path.Combine(Widget, "notes.txt"));

        Git(Widget, "tag", "v-local");
        AssertRefused(await Run(await RemovalOf(Widget)), "tag");
        Git(Widget, "tag", "-d", "v-local");

        Git(Widget, "checkout", "-q", "-b", "local-only");
        Commit(Widget, "local.txt", "never pushed");
        Git(Widget, "checkout", "-q", "main");
        AssertRefused(await Run(await RemovalOf(Widget)), "local-only");

        Assert.True(Directory.Exists(Widget));
        Assert.False(Directory.Exists(_trash));
    }

    [Fact]
    public async Task A_repo_with_no_remote_is_never_removed()
    {
        var solo = Directory.CreateDirectory(Path.Combine(_dev, "solo")).FullName;
        Git(solo, "init", "-q", "-b", "main");
        Commit(solo, "a.txt", "only here");

        AssertRefused(await Run(await RemovalOf(solo)), "No remote");
        Assert.True(Directory.Exists(solo));
    }

    [Fact]
    public async Task The_agent_fetches_first_so_a_branch_deleted_on_the_remote_counts()
    {
        Git(Widget, "checkout", "-q", "-b", "feature");
        Commit(Widget, "feature.txt", "pushed, for now");
        Git(Widget, "push", "-q", "-u", "origin", "feature");
        Git(Widget, "checkout", "-q", "main");
        var action = await RemovalOf(Widget);
        Assert.True((await AssessAsync(Widget)).IsRemovable);

        // Someone deletes the branch on the remote. This clone still believes origin/feature exists.
        Git(_origin, "branch", "-D", "feature");

        AssertRefused(await Run(action), "not safe to remove");
        Assert.True(Directory.Exists(Widget));
    }

    [Fact]
    public async Task A_fetch_that_fails_is_a_refusal()
    {
        var action = await RemovalOf(Widget);
        Git(Widget, "remote", "set-url", "origin", Path.Combine(_root, "gone.git"));

        AssertRefused(await Run(action), "could not fetch");
        Assert.True(Directory.Exists(Widget));
    }

    [Fact]
    public async Task Ignored_files_must_each_be_acknowledged()
    {
        File.WriteAllText(Path.Combine(Widget, ".gitignore"), ".env\nlocal.db\nnode_modules/\n");
        Git(Widget, "add", ".gitignore");
        Git(Widget, "commit", "-q", "-m", "ignore");
        Git(Widget, "push", "-q", "origin", "main");
        File.WriteAllText(Path.Combine(Widget, ".env"), "SECRET=1");
        File.WriteAllText(Path.Combine(Widget, "local.db"), "rows");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(Widget, "node_modules")).FullName, "x.js"), "//"); // build output: not a caveat

        var safety = await AssessAsync(Widget);
        Assert.Equal(SafetyVerdict.SafeWithCaveats, safety.Verdict);
        Assert.Equal([".env", "local.db"], safety.Caveats.SelectMany(c => c.Names).Order());

        AssertRefused(await Run(AgentAction.RemoveProject(Widget, safety.Fingerprint)), "not acknowledged: .env, local.db");
        AssertRefused(await Run(AgentAction.RemoveProject(Widget, safety.Fingerprint, [".env"])), "not acknowledged: local.db");
        Assert.True(Directory.Exists(Widget));

        var outcome = await Run(AgentAction.RemoveProject(Widget, safety.Fingerprint, [".env", "local.db"]));
        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.True(File.Exists(Path.Combine(_trash, "widget", ".env"))); // in the trash, not gone
    }

    [Fact]
    public async Task Only_a_repository_this_agent_scans_can_be_removed()
    {
        var fingerprint = (await AssessAsync(Widget)).Fingerprint;
        var outside = Path.Combine(_root, "elsewhere");
        Git(_root, "clone", "-q", _origin, "elsewhere");

        AssertRefused(await Run(AgentAction.RemoveProject(outside, fingerprint)), "outside this agent's scan roots");
        AssertRefused(await Run(AgentAction.RemoveProject(_dev, fingerprint)), "outside this agent's scan roots"); // the root itself
        AssertRefused(await Run(AgentAction.RemoveProject(_root, fingerprint)), "outside this agent's scan roots");
        AssertRefused(await Run(AgentAction.RemoveProject(Path.Combine(_dev, "widget", "..", "..", "elsewhere"), fingerprint)), "not a plain absolute path");
        AssertRefused(await Run(AgentAction.RemoveProject(Path.Combine(_dev, "missing"), fingerprint)), "not a git repository");

        var sub = Directory.CreateDirectory(Path.Combine(Widget, "src")).FullName;
        AssertRefused(await Run(AgentAction.RemoveProject(sub, fingerprint)), "not a git repository");
        var deep = Directory.CreateDirectory(Path.Combine(Widget, "src", "deeper")).FullName;
        AssertRefused(await Run(AgentAction.RemoveProject(deep, fingerprint)), "not a repository this agent scans");

        var plain = Directory.CreateDirectory(Path.Combine(_dev, "plain")).FullName;
        AssertRefused(await Run(AgentAction.RemoveProject(plain, fingerprint)), "not a git repository");

        Assert.True(Directory.Exists(outside));
        Assert.True(Directory.Exists(plain));
        Assert.True(Directory.Exists(Widget));
    }

    [Fact]
    public async Task A_link_into_the_scan_root_cannot_stand_in_for_a_folder_outside_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating a symbolic link needs a privilege the CI runner does not have
        }

        var outside = Path.Combine(_root, "elsewhere");
        Git(_root, "clone", "-q", _origin, "elsewhere");
        var link = Path.Combine(_dev, "linked");
        Directory.CreateSymbolicLink(link, outside);

        AssertRefused(await Run(AgentAction.RemoveProject(link, "0123456789abcdef01234567")), "outside this agent's scan roots");

        // A link to a repository that is inside the root is still a link, not a folder to move.
        var inside = Path.Combine(_dev, "alias");
        Directory.CreateSymbolicLink(inside, Widget);
        AssertRefused(await Run(AgentAction.RemoveProject(inside, (await AssessAsync(Widget)).Fingerprint)), "is a link to a folder");

        Assert.True(Directory.Exists(outside));
        Assert.True(Directory.Exists(Widget));
    }

    [Fact]
    public async Task When_the_trash_cannot_take_it_nothing_is_deleted()
    {
        var outcome = await Run(await RemovalOf(Widget), new BrokenTrash());

        Assert.False(outcome.Succeeded);
        Assert.Contains("Not removed", outcome.Output);
        Assert.True(File.Exists(Path.Combine(Widget, "README.md")));
        Assert.False(_removals.Allows(Widget, _origin));

        // No trash at all (an operating system the agent has none for) is the same.
        Assert.False(new NoTrash().Move(Widget).Ok);
        Assert.True(Directory.Exists(Widget));
    }

    [Fact]
    public async Task A_linked_worktree_is_removed_by_git_and_its_branch_is_kept()
    {
        var tree = Path.Combine(_dev, "widget.worktrees", "side");
        Git(Widget, "worktree", "add", "-q", "-b", "side", tree);

        // The main working tree has something hanging off it now: it is not removable.
        AssertRefused(await Run(await RemovalOf(Widget)), "linked worktree");

        // A worktree with files of its own is not either.
        File.WriteAllText(Path.Combine(tree, "scratch.txt"), "untracked");
        AssertRefused(await Run(await RemovalOf(tree, includeRepository: false)), "untracked");
        File.Delete(Path.Combine(tree, "scratch.txt"));

        var outcome = await Run(await RemovalOf(tree, includeRepository: false));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.Contains("git worktree remove", outcome.Output);
        Assert.False(Directory.Exists(tree));
        Assert.False(Directory.Exists(_trash)); // git deleted it; it was never the trash's to take
        Assert.Contains("side", Git(Widget, "for-each-ref", "--format=%(refname:short)", "refs/heads").Split('\n'));
        Assert.Equal(2, outcome.AffectedPaths.Count);
        Assert.DoesNotContain("widget.worktrees", Git(Widget, "worktree", "list"));
    }

    [Fact]
    public async Task A_worktree_whose_main_repository_is_not_scanned_is_left_alone()
    {
        var main = Path.Combine(_root, "elsewhere");
        Git(_root, "clone", "-q", _origin, "elsewhere");
        var tree = Path.Combine(_dev, "stray");
        Git(main, "worktree", "add", "-q", "-b", "stray", tree);

        AssertRefused(await Run(await RemovalOf(tree)), "main working tree");
        Assert.True(Directory.Exists(tree));
    }

    // ---- the trash itself -----------------------------------------------------------------------

    [Fact]
    public void A_folder_trash_keeps_every_folder_under_a_name_of_its_own()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero));
        var trash = new FolderTrash(_trash, clock);
        for (var i = 0; i < 4; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, "work", "thing")).FullName;
            File.WriteAllText(Path.Combine(dir, "n.txt"), i.ToString());
            var moved = trash.Move(dir);
            Assert.True(moved.Ok, moved.Error);
            Assert.False(Directory.Exists(dir));
            Assert.True(File.Exists(Path.Combine(moved.Location!, "n.txt")));
        }

        var names = Directory.GetDirectories(_trash).Select(Path.GetFileName).ToList();
        Assert.Equal(4, names.Count);
        Assert.Contains("thing", names);
        Assert.Equal(["0", "1", "2", "3"], Directory.GetDirectories(_trash).Select(d => File.ReadAllText(Path.Combine(d, "n.txt"))).Order());

        Assert.False(trash.Move(Path.Combine(_root, "work", "not-there")).Ok);
    }

    [Fact]
    public void The_freedesktop_trash_writes_a_trashinfo_beside_each_folder()
    {
        var home = Path.Combine(_root, "xdg", "Trash");
        var trash = new FreedesktopTrash(home, new FixedClock(new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero)));
        var first = Directory.CreateDirectory(Path.Combine(_root, "work", "my project")).FullName;
        File.WriteAllText(Path.Combine(first, "a.txt"), "a");

        var moved = trash.Move(first);

        Assert.True(moved.Ok, moved.Error);
        Assert.False(Directory.Exists(first));
        Assert.True(File.Exists(Path.Combine(home, "files", "my project", "a.txt")));
        var info = File.ReadAllText(Path.Combine(home, "info", "my project.trashinfo"));
        Assert.StartsWith("[Trash Info]\nPath=", info);
        Assert.Matches(@"\nDeletionDate=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\n$", info);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains($"Path={string.Join('/', first.Split('/').Select(Uri.EscapeDataString))}\n", info);
            Assert.Contains("my%20project", info);
        }

        // The same name again: both are kept, each with its own info file.
        Directory.CreateDirectory(first);
        Assert.True(trash.Move(first).Ok);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(home, "files")).Length);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(home, "info"), "*.trashinfo").Length);
        Assert.All(Directory.GetDirectories(Path.Combine(home, "files")), d => Assert.True(File.Exists(Path.Combine(home, "info", Path.GetFileName(d) + ".trashinfo"))));
    }

    [Fact]
    public void The_macos_trash_is_a_rename_into_the_users_trash_folder()
    {
        var home = Path.Combine(_root, "home", ".Trash");
        var trash = new MacTrash(home);
        var dir = Directory.CreateDirectory(Path.Combine(_root, "work", "thing")).FullName;

        var moved = trash.Move(dir);

        Assert.True(moved.Ok, moved.Error);
        Assert.Equal(Path.Combine(home, "thing"), moved.Location);
        Assert.True(Directory.Exists(moved.Location));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void The_macos_trash_asks_the_system_tool_first_and_believes_only_the_folder_having_gone()
    {
        var home = Path.Combine(_root, "home", ".Trash");
        var finder = Directory.CreateDirectory(Path.Combine(_root, "finder")).FullName; // where the pretend tool puts things
        var asked = new List<string>();

        // The tool moves it: success, and the rename is never needed.
        var dir = Directory.CreateDirectory(Path.Combine(_root, "work", "-rf")).FullName;
        var moved = new MacTrash(home, tool: path =>
        {
            asked.Add(path);
            Directory.Move(path, Path.Combine(finder, "moved"));
            return new TrashToolRun(true, false, 0, "");
        }).Move(dir);
        Assert.True(moved.Ok, moved.Error);
        Assert.False(Directory.Exists(dir));
        Assert.False(Directory.Exists(home));
        Assert.Equal([dir], asked); // one absolute path, so a name that starts with a dash is never an option
        Assert.True(Path.IsPathFullyQualified(asked[0]));

        // The tool fails, or cannot be started, with the folder still there: the rename into the trash folder.
        foreach (var run in (TrashToolRun[])[new(true, false, 5, "not permitted"), new(false, false, -1, "no such file")])
        {
            Directory.CreateDirectory(dir);
            var fallback = new MacTrash(home, tool: _ => run).Move(dir);
            Assert.True(fallback.Ok, fallback.Error);
            Assert.False(Directory.Exists(dir));
            Assert.StartsWith(home, fallback.Location);
            Assert.True(Directory.Exists(fallback.Location));
        }

        // The tool says it worked but the folder is still there: not believed, so the rename does it.
        Directory.CreateDirectory(dir);
        var claimed = new MacTrash(home, tool: _ => new TrashToolRun(true, false, 0, "")).Move(dir);
        Assert.True(claimed.Ok, claimed.Error);
        Assert.False(Directory.Exists(dir));
        Assert.StartsWith(home, claimed.Location);

        // The tool reports an error although the folder went: it left its path, and that is what counts.
        Directory.CreateDirectory(dir);
        var grumbled = new MacTrash(home, tool: path =>
        {
            Directory.Move(path, Path.Combine(finder, "grumbled"));
            return new TrashToolRun(true, false, 1, "warning");
        }).Move(dir);
        Assert.True(grumbled.Ok, grumbled.Error);

        // The tool was stopped half way: nothing else is tried, and the folder is reported as still there.
        Directory.CreateDirectory(dir);
        var before = Directory.GetDirectories(home).Length;
        var stuck = new MacTrash(home, tool: _ => new TrashToolRun(true, true, -1, "timed out")).Move(dir);
        Assert.False(stuck.Ok);
        Assert.Contains("still there", stuck.Error);
        Assert.True(Directory.Exists(dir));
        Assert.Equal(before, Directory.GetDirectories(home).Length);
    }

    [Fact]
    public void A_macos_trash_pointed_at_a_folder_never_runs_the_system_tool()
    {
        // This is what keeps every test out of the real Trash: naming the folder switches the tool off.
        Assert.False(new MacTrash(Path.Combine(_root, "home", ".Trash")).UsesTool);
        Assert.True(new MacTrash(Path.Combine(_root, "home", ".Trash"), tool: _ => new TrashToolRun(false, false, -1, "")).UsesTool);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(File.Exists(MacTrash.ToolPath), new MacTrash().UsesTool);
        }
    }

    [Fact]
    public void The_trash_can_be_pointed_at_a_folder_by_configuration()
    {
        var configured = Trash.ForThisMachine(new ActionsOptions { TrashDirectory = _trash });
        Assert.IsType<FolderTrash>(configured);
        Assert.Equal(_trash, configured.Name);

        // Otherwise it is the operating system's own. (Not used here: a test must never fill a real trash.)
        var own = Trash.ForThisMachine(new ActionsOptions());
        Assert.Equal(OperatingSystem.IsMacOS() ? typeof(MacTrash) : OperatingSystem.IsWindows() ? typeof(WindowsRecycleBin) : typeof(FreedesktopTrash), own.GetType());
    }

    // ---- clone ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_removed_project_can_be_cloned_back_into_its_folder()
    {
        Assert.True((await Run(await RemovalOf(Widget))).Succeeded);

        var outcome = await Run(AgentAction.CloneInto(Widget, _origin));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.True(File.Exists(Path.Combine(Widget, "README.md")));
        Assert.Equal("main", Git(Widget, "branch", "--show-current"));
        Assert.Contains("widget.git", Git(Widget, "remote", "get-url", "origin"));
        Assert.Equal([Widget], outcome.AffectedPaths);
        Assert.True(Directory.Exists(Path.Combine(_trash, "widget"))); // the trashed copy is not touched
    }

    [Fact]
    public async Task A_clone_can_name_its_remote()
    {
        Assert.True((await Run(await RemovalOf(Widget))).Succeeded);

        Assert.True((await Run(AgentAction.CloneInto(Widget, _origin, "upstream"))).Succeeded);
        Assert.Equal("upstream", Git(Widget, "remote"));
    }

    [Fact]
    public async Task The_agent_only_clones_a_url_it_knows_itself()
    {
        var other = Path.Combine(_root, "origin", "other.git");
        Git(_root, "clone", "-q", "--bare", _seed, other);
        Assert.True((await Run(await RemovalOf(Widget))).Succeeded);

        // Not a remote of the project that was removed from this folder.
        AssertRefused(await Run(AgentAction.CloneInto(Widget, other)), "does not know that URL");
        AssertRefused(await Run(AgentAction.CloneInto(Widget, "https://example.invalid/anything.git")), "does not know that URL");
        // The right URL, a folder nothing was removed from.
        AssertRefused(await Run(AgentAction.CloneInto(Path.Combine(_dev, "widget2"), _origin)), "does not know that URL");
        Assert.False(Directory.Exists(Widget));
        Assert.False(Directory.Exists(Path.Combine(_dev, "widget2")));

        // A URL some repository in the last scan has as a remote is one the machine's owner already uses.
        Git(_dev, "clone", "-q", other, "other");
        _known.Update(await _scanner.ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None));
        Assert.True((await Run(AgentAction.CloneInto(Path.Combine(_dev, "other-again"), other))).Succeeded);
    }

    [Fact]
    public async Task A_clone_never_goes_over_something_or_outside_the_scan_roots()
    {
        Assert.True((await Run(await RemovalOf(Widget))).Succeeded);
        _known.Update(new MachineSnapshot
        {
            Machine = new MachineInfo { MachineId = "m", Hostname = "m" },
            Entries = [new DevEntry { Root = _dev, Name = "x", Path = Path.Combine(_dev, "x"), Kind = EntryKind.GitRepo, Git = new GitState { Remotes = [new RemoteInfo { Name = "origin", Url = _origin }] } }],
        });

        AssertRefused(await Run(AgentAction.CloneInto(Path.Combine(_root, "elsewhere"), _origin)), "outside this agent's scan roots");
        AssertRefused(await Run(AgentAction.CloneInto(_dev, _origin)), "outside this agent's scan roots");
        AssertRefused(await Run(AgentAction.CloneInto(Path.Combine(_dev, "a", "..", "..", "elsewhere"), _origin)), "not a plain absolute path");
        AssertRefused(await Run(AgentAction.CloneInto(Path.Combine(_dev, ".hidden"), _origin)), "not where this agent scans");
        AssertRefused(await Run(AgentAction.CloneInto(Path.Combine(_dev, "no-such-folder", "widget"), _origin)), "does not exist");

        Directory.CreateDirectory(Path.Combine(_dev, "a", "b"));
        AssertRefused(await Run(AgentAction.CloneInto(Path.Combine(_dev, "a", "b", "widget"), _origin)), "not where this agent scans");

        // Something is there: a folder (even an empty one), or a file.
        Directory.CreateDirectory(Widget);
        AssertRefused(await Run(AgentAction.CloneInto(Widget, _origin)), "already exists");
        Directory.Delete(Widget);
        File.WriteAllText(Widget, "a file");
        AssertRefused(await Run(AgentAction.CloneInto(Widget, _origin)), "already exists");
        File.Delete(Widget);

        // Not inside another repository.
        Git(_dev, "clone", "-q", _origin, "host");
        AssertRefused(await Run(AgentAction.CloneInto(Path.Combine(_dev, "host", "nested"), _origin)), "is itself a repository");

        _options.Actions.Enabled = false;
        AssertRefused(await Run(AgentAction.CloneInto(Widget, _origin)), "not enabled");
        Assert.False(Path.Exists(Widget));
        Assert.False(Directory.Exists(Path.Combine(_root, "elsewhere")));
    }

    [Fact]
    public async Task A_clone_cannot_escape_through_a_linked_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating a symbolic link needs a privilege the CI runner does not have
        }

        Assert.True((await Run(await RemovalOf(Widget))).Succeeded);
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(_dev, "widget.worktrees"), outside);
        _removals.Record(Path.Combine(_dev, "widget.worktrees", "widget"), [_origin], DateTimeOffset.UtcNow);

        AssertRefused(await Run(AgentAction.CloneInto(Path.Combine(_dev, "widget.worktrees", "widget"), _origin)), "outside this agent's scan roots");
        Assert.Empty(Directory.GetFileSystemEntries(outside));

        // A dangling link where the clone would go is "something there".
        File.CreateSymbolicLink(Widget, Path.Combine(_dev, "nowhere"));
        AssertRefused(await Run(AgentAction.CloneInto(Widget, _origin)), "already exists");
    }

    [Fact]
    public void The_removal_record_survives_a_restart_and_is_per_folder()
    {
        var file = Path.Combine(_root, "log", "removed.json");
        new RemovalLog(file).Record(Widget, ["https://example.invalid/a.git", "git@example.invalid:a.git"], DateTimeOffset.UtcNow);

        var reloaded = new RemovalLog(file);
        Assert.True(reloaded.Allows(Widget, "https://example.invalid/a.git"));
        Assert.True(reloaded.Allows(Widget + Path.DirectorySeparatorChar, "git@example.invalid:a.git"));
        Assert.False(reloaded.Allows(Widget, "https://example.invalid/b.git"));
        Assert.False(reloaded.Allows(Widget + "2", "https://example.invalid/a.git"));

        // A later removal of the same folder replaces what it may be cloned from.
        reloaded.Record(Widget, ["https://example.invalid/c.git"], DateTimeOffset.UtcNow);
        Assert.False(new RemovalLog(file).Allows(Widget, "https://example.invalid/a.git"));
        Assert.True(new RemovalLog(file).Allows(Widget, "https://example.invalid/c.git"));
    }

    // ---- build output ---------------------------------------------------------------------------

    private string BuildDir(string relative, string? repo = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(repo ?? Widget, relative)).FullName;
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "deep", "er")).FullName, "file.bin"), "built");
        return dir;
    }

    [Fact]
    public async Task Deletes_build_folders_that_git_tracks_nothing_in()
    {
        File.WriteAllText(Path.Combine(Widget, "package.json"), "{}");
        Directory.CreateDirectory(Path.Combine(Widget, "src", "App"));
        File.WriteAllText(Path.Combine(Widget, "src", "App", "App.csproj"), "<Project />");
        var modules = BuildDir("node_modules");
        var dist = BuildDir("dist");
        var bin = BuildDir(Path.Combine("src", "App", "bin"));
        var obj = BuildDir(Path.Combine("src", "App", "obj"));
        var nested = BuildDir(Path.Combine("packages", "ui", "node_modules"));

        var one = await Run(AgentAction.CleanBuildDirs(Widget, [modules]));
        Assert.True(one.Succeeded, one.Output);
        Assert.False(Directory.Exists(modules));
        Assert.True(Directory.Exists(dist));
        Assert.Equal([Widget], one.AffectedPaths);

        var rest = await Run(AgentAction.CleanBuildDirs(Widget, [dist, bin, obj, nested]));
        Assert.True(rest.Succeeded, rest.Output);
        Assert.All((string[])[dist, bin, obj, nested], d => Assert.False(Directory.Exists(d)));
        Assert.True(File.Exists(Path.Combine(Widget, "src", "App", "App.csproj")));
        Assert.True(File.Exists(Path.Combine(Widget, "README.md")));
        Assert.True(Directory.Exists(Path.Combine(Widget, ".git")));
    }

    [Fact]
    public async Task Only_known_build_folders_are_deleted()
    {
        var bin = BuildDir("bin"); // no project file beside it: could be a folder of scripts
        var src = BuildDir("src");
        var target = BuildDir("target");
        var dist = BuildDir("dist");

        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [bin])), "not a build folder the agent knows");
        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [src])), "not a build folder the agent knows");
        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [target])), "not a build folder the agent knows");
        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [dist])), "not a build folder the agent knows");
        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [Path.Combine(Widget, ".git")])), "git's own folder");
        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [Path.Combine(Widget, ".git", "node_modules")])), "git's own folder");
        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [Widget])), "is not inside");
        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [Path.Combine(Widget, "node_modules")])), "does not exist");

        // One bad folder refuses the whole action.
        var modules = BuildDir("node_modules");
        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [modules, src])), "not a build folder the agent knows");
        Assert.All((string[])[bin, src, target, dist, modules], d => Assert.True(Directory.Exists(d)));
    }

    [Fact]
    public async Task Restored_packages_and_report_folders_are_recognised_but_never_deleted()
    {
        // Names too ordinary to delete: NuGet's packages beside a solution, and test reports beside a package.json.
        File.WriteAllText(Path.Combine(Widget, "App.sln"), "");
        File.WriteAllText(Path.Combine(Widget, "package.json"), "{}");
        var packages = BuildDir("packages");
        var coverage = BuildDir("coverage");
        var results = BuildDir("test-results");
        var angular = BuildDir(".angular");

        foreach (var dir in (string[])[packages, coverage, results])
        {
            AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [dir])), "not a build folder the agent knows");
            Assert.True(Directory.Exists(dir));
        }

        // A framework cache has a name nobody else uses, so it can go.
        var cache = await Run(AgentAction.CleanBuildDirs(Widget, [angular]));
        Assert.True(cache.Succeeded, cache.Output);
        Assert.False(Directory.Exists(angular));
    }

    [Fact]
    public async Task A_folder_git_tracks_files_in_is_not_build_output()
    {
        var modules = BuildDir("node_modules");
        Git(Widget, "add", "-f", "node_modules");
        Git(Widget, "commit", "-q", "-m", "vendored");

        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [modules])), "git tracks files");
        Assert.True(Directory.Exists(modules));
    }

    [Fact]
    public async Task Build_folders_outside_the_repository_are_refused()
    {
        var other = Path.Combine(_dev, "other");
        Git(_dev, "clone", "-q", _origin, "other");
        var theirs = BuildDir("node_modules", other);
        var outside = BuildDir("node_modules", Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName);

        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [theirs])), "is not inside");
        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [outside])), "is not inside");
        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [Path.Combine(Widget, "..", "other", "node_modules")])), "not a plain absolute path");
        AssertRefused(await Run(AgentAction.CleanBuildDirs(Path.Combine(_root, "elsewhere"), [outside])), "outside this agent's scan roots");
        AssertRefused(await Run(AgentAction.CleanBuildDirs(_dev, [theirs])), "outside this agent's scan roots");

        _options.Actions.Enabled = false;
        AssertRefused(await Run(AgentAction.CleanBuildDirs(other, [theirs])), "not enabled");
        Assert.True(Directory.Exists(theirs));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task Links_are_never_followed_out_of_the_repository()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating a symbolic link needs a privilege the CI runner does not have
        }

        var outside = Directory.CreateDirectory(Path.Combine(_root, "precious")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "not build output");

        // node_modules is itself a link to somewhere else.
        Directory.CreateSymbolicLink(Path.Combine(Widget, "node_modules"), outside);
        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [Path.Combine(Widget, "node_modules")])), "reached through a link");

        // A folder on the way to it is a link.
        Directory.CreateDirectory(Path.Combine(outside, "node_modules"));
        Directory.CreateSymbolicLink(Path.Combine(Widget, "packages"), outside);
        AssertRefused(await Run(AgentAction.CleanBuildDirs(Widget, [Path.Combine(Widget, "packages", "node_modules")])), "reached through a link");
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.True(Directory.Exists(Path.Combine(outside, "node_modules")));

        // A link inside a real build folder is removed as a link: what it points at stays.
        Directory.Delete(Path.Combine(Widget, "node_modules"));
        var modules = BuildDir("node_modules");
        Directory.CreateSymbolicLink(Path.Combine(modules, "linked-package"), outside);
        File.CreateSymbolicLink(Path.Combine(modules, "linked-file"), Path.Combine(outside, "keep.txt"));

        var outcome = await Run(AgentAction.CleanBuildDirs(Widget, [modules]));
        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.False(Directory.Exists(modules));
        Assert.Equal("not build output", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public async Task Build_output_that_is_already_gone_is_not_a_failure()
    {
        var modules = BuildDir("node_modules");
        var inner = BuildDir(Path.Combine("node_modules", "pkg", "node_modules"));

        // The inner one goes with the outer one.
        var outcome = await Run(AgentAction.CleanBuildDirs(Widget, [modules, inner]));
        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.False(Directory.Exists(modules));
    }

    // ---- fast-forward ---------------------------------------------------------------------------

    private string Head(string repo, string rev = "HEAD") => Git(repo, "rev-parse", rev);

    [Fact]
    public async Task Fast_forwards_the_checked_out_branch_when_its_tree_is_clean()
    {
        CommitUpstream("new on the remote");
        var before = Head(Widget);

        var outcome = await Run(AgentAction.PullFastForward(Widget, "origin", "main"));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.NotEqual(before, Head(Widget));
        Assert.Equal(Head(_seed), Head(Widget));
        Assert.True(File.Exists(Path.Combine(Widget, "upstream.txt"))); // the files moved too
        Assert.Contains("Fast-forwarded main by 1 commit", outcome.Output);
        Assert.Equal([Widget], outcome.AffectedPaths);

        // Again: nothing to do, and that is fine.
        var again = await Run(AgentAction.PullFastForward(Widget, "origin", "main"));
        Assert.True(again.Succeeded);
        Assert.Contains("already has everything", again.Output);
    }

    [Fact]
    public async Task Never_pulls_into_a_working_tree_with_uncommitted_changes()
    {
        CommitUpstream("new on the remote");
        File.WriteAllText(Path.Combine(Widget, "README.md"), "# Widget, half edited\n");
        var before = Head(Widget);

        AssertRefused(await Run(AgentAction.PullFastForward(Widget, "origin", "main")), "uncommitted change");

        Assert.Equal(before, Head(Widget));
        Assert.Equal("# Widget, half edited\n", File.ReadAllText(Path.Combine(Widget, "README.md")));

        // Staged counts as well.
        Git(Widget, "add", "README.md");
        AssertRefused(await Run(AgentAction.PullFastForward(Widget, "origin", "main")), "uncommitted change");
        Assert.Equal(before, Head(Widget));
    }

    [Fact]
    public async Task Fast_forwards_a_branch_that_is_not_checked_out_without_touching_the_working_tree()
    {
        Git(Widget, "checkout", "-q", "-b", "elsewhere");
        File.WriteAllText(Path.Combine(Widget, "wip.txt"), "work in progress on another branch");
        CommitUpstream("new on the remote");
        var before = Head(Widget, "refs/heads/main");

        var outcome = await Run(AgentAction.PullFastForward(Widget, "origin", "main"));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.NotEqual(before, Head(Widget, "refs/heads/main"));
        Assert.Equal(Head(_seed), Head(Widget, "refs/heads/main"));
        Assert.Equal("elsewhere", Git(Widget, "branch", "--show-current"));
        Assert.True(File.Exists(Path.Combine(Widget, "wip.txt")));
        Assert.False(File.Exists(Path.Combine(Widget, "upstream.txt")));
    }

    [Fact]
    public async Task A_branch_with_commits_of_its_own_is_never_merged_or_rebased()
    {
        CommitUpstream("new on the remote");
        Commit(Widget, "mine.txt", "a local commit");
        var before = Head(Widget);

        AssertRefused(await Run(AgentAction.PullFastForward(Widget, "origin", "main")), "cannot be fast-forwarded");
        Assert.Equal(before, Head(Widget));

        // The same when it is not checked out.
        Git(Widget, "checkout", "-q", "-b", "elsewhere");
        AssertRefused(await Run(AgentAction.PullFastForward(Widget, "origin", "main")), "cannot be fast-forwarded");
        Assert.Equal(before, Head(Widget, "refs/heads/main"));
    }

    [Fact]
    public async Task Fast_forwards_in_the_linked_worktree_that_has_the_branch_checked_out()
    {
        var tree = Path.Combine(_dev, "widget.worktrees", "main-tree");
        Git(Widget, "checkout", "-q", "-b", "parked");
        Git(Widget, "worktree", "add", "-q", tree, "main");
        CommitUpstream("new on the remote");

        var outcome = await Run(AgentAction.PullFastForward(Widget, "origin", "main"));

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.True(File.Exists(Path.Combine(tree, "upstream.txt")));
        Assert.False(File.Exists(Path.Combine(Widget, "upstream.txt")));
        Assert.Equal(2, outcome.AffectedPaths.Count);

        // Dirty there: refused, even though the repository the action names is clean.
        CommitUpstream("and another");
        File.WriteAllText(Path.Combine(tree, "README.md"), "edited in the worktree");
        AssertRefused(await Run(AgentAction.PullFastForward(Widget, "origin", "main")), "uncommitted change");
    }

    [Fact]
    public async Task Only_a_branch_tracking_the_same_name_on_that_remote_is_pulled()
    {
        Git(Widget, "branch", "no-upstream");
        Git(Widget, "branch", "--track", "renamed", "origin/main");

        AssertRefused(await Run(AgentAction.PullFastForward(Widget, "origin", "no-upstream")), "has no upstream");
        AssertRefused(await Run(AgentAction.PullFastForward(Widget, "origin", "renamed")), "tracks origin/main");
        AssertRefused(await Run(AgentAction.PullFastForward(Widget, "origin", "nope")), "no local branch");
        AssertRefused(await Run(AgentAction.PullFastForward(Widget, "nowhere", "main")), "no remote named");
        AssertRefused(await Run(AgentAction.PullFastForward(Path.Combine(_root, "seed"), "origin", "main")), "outside this agent's scan roots");

        _options.Actions.Enabled = false;
        AssertRefused(await Run(AgentAction.PullFastForward(Widget, "origin", "main")), "not enabled");
    }

    [Fact]
    public async Task A_pull_that_cannot_fetch_is_refused()
    {
        Git(Widget, "remote", "set-url", "origin", Path.Combine(_root, "gone.git"));

        AssertRefused(await Run(AgentAction.PullFastForward(Widget, "origin", "main")), "could not fetch");
    }

    // ---- plumbing -------------------------------------------------------------------------------

    private sealed class BrokenTrash : ITrash
    {
        public string Name => "the trash";

        public TrashResult Move(string path) => TrashResult.Failed("the trash is on another volume.");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
            // Git's object files are read-only, which stops a delete on Windows. Links are left as they are.
            foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                file.Attributes = FileAttributes.Normal;
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
