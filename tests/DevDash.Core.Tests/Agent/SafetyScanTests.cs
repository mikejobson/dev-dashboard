using System.Diagnostics;
using DevDash.Agent;
using DevDash.Agent.Git;
using DevDash.Agent.Scanning;
using DevDash.Contracts;
using DevDash.Core.Cleanup;
using DevDash.Core.Cleanup.Rules;
using DevDash.Core.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDash.Core.Tests.Agent;

/// <summary>
/// The safety facts and the verdict they lead to, on throwaway repositories under the system temp
/// folder: a bare "remote", a clone of it in the scan root, and whatever each test does to the clone.
/// </summary>
public sealed class SafetyScanTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("devdash-safety-").FullName;
    private readonly string _dev;
    private readonly string _origin;

    public SafetyScanTests()
    {
        _dev = Directory.CreateDirectory(Path.Combine(_root, "Developer")).FullName;
        _origin = Path.Combine(_root, "origin", "widget.git");

        var seed = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seed);
        Git(seed, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(seed, "README.md"), "# Widget\n");
        File.WriteAllText(Path.Combine(seed, "package.json"), """{ "name": "widget" }""");
        File.WriteAllText(Path.Combine(seed, ".gitignore"), "node_modules/\ndist/\n*.log\n.env\nlocal/\n__pycache__/\n");
        Git(seed, "add", ".");
        Git(seed, "commit", "-q", "-m", "initial");
        Git(seed, "tag", "v1.0");
        Directory.CreateDirectory(Path.GetDirectoryName(_origin)!);
        Git(_root, "clone", "-q", "--bare", seed, _origin);
        Git(_dev, "clone", "-q", _origin, "widget");
    }

    private string Widget => Path.Combine(_dev, "widget");

    private async Task<DevEntry> ScanWidgetAsync(bool fetch = true) =>
        (await ScanAsync(fetch)).Entries.Single(e => e.Name == "widget");

    private Task<MachineSnapshot> ScanAsync(bool fetch = true) =>
        CreateScanner(fetch).ScanAsync(new ScanRequest(AllowFetch: fetch, SizeMode.Force), CancellationToken.None);

    private static SafetyAssessment Assess(DevEntry entry) => RepoSafety.Assess(entry, DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_fresh_clone_with_only_build_output_is_safe_to_remove()
    {
        Directory.CreateDirectory(Path.Combine(Widget, "node_modules", "left-pad"));
        File.WriteAllText(Path.Combine(Widget, "node_modules", "left-pad", "index.js"), "module.exports = 1;");
        Directory.CreateDirectory(Path.Combine(Widget, "dist"));
        File.WriteAllText(Path.Combine(Widget, "dist", "bundle.js"), "built");
        Directory.CreateDirectory(Path.Combine(Widget, "tools", "__pycache__"));
        File.WriteAllText(Path.Combine(Widget, "tools", "__pycache__", "x.pyc"), "cache");
        File.WriteAllText(Path.Combine(Widget, ".DS_Store"), "noise");
        Git(Widget, "config", "core.excludesFile", Path.Combine(_root, "global-ignore"));
        File.WriteAllText(Path.Combine(_root, "global-ignore"), ".DS_Store\n");

        var entry = await ScanWidgetAsync();
        var git = entry.Git!;

        var facts = git.Safety!;
        Assert.Empty(facts.IgnoredFiles);
        Assert.Equal(0, facts.IgnoredFileCount);
        Assert.Empty(facts.SecretFiles);
        Assert.Empty(facts.Submodules);
        Assert.Equal(1, facts.TagCount);
        Assert.Empty(facts.LocalOnlyTags);
        Assert.NotNull(facts.RemoteTagsCheckedAt);
        Assert.True(facts.BranchesChecked);
        Assert.True(facts.HeadOnRemote);
        Assert.True(Assert.Single(git.Branches).OnRemote);
        Assert.Null(git.MainWorktreePath);
        Assert.Equal(0, git.Untracked);

        var safety = Assess(entry);
        Assert.Equal(SafetyVerdict.Safe, safety.Verdict);
        Assert.True(safety.FreedBytes > 0);
        Assert.True(safety.ReclaimableBytes > 0);
    }

    [Fact]
    public async Task Unpushed_commits_are_counted_per_branch_with_the_date_of_the_oldest()
    {
        // Two commits on a tracked branch, one more on a branch that was never pushed and forks from it.
        Git(Widget, "checkout", "-q", "-b", "feature/x");
        Git(Widget, "push", "-q", "-u", "origin", "feature/x");
        Commit(Widget, "a.txt", "first unpushed", "2026-01-10T00:00:00Z");
        Commit(Widget, "b.txt", "second unpushed", "2026-02-10T00:00:00Z");
        Git(Widget, "checkout", "-q", "-b", "spike");
        Commit(Widget, "c.txt", "spike work", "2026-03-10T00:00:00Z");

        var entry = await ScanWidgetAsync();
        var branches = entry.Git!.Branches.ToDictionary(b => b.Name);

        Assert.True(branches["main"].OnRemote);
        Assert.Null(branches["main"].UnpushedCount);
        Assert.False(branches["feature/x"].OnRemote);
        Assert.Equal(2, branches["feature/x"].UnpushedCount);
        Assert.Equal(DateTimeOffset.Parse("2026-01-10T00:00:00Z"), branches["feature/x"].OldestUnpushedAt);
        Assert.False(branches["spike"].OnRemote);
        Assert.Equal(3, branches["spike"].UnpushedCount);
        Assert.False(entry.Git.Safety!.HeadOnRemote);

        var safety = Assess(entry);
        Assert.Equal(SafetyVerdict.AtRisk, safety.Verdict);
        Assert.Equal(RiskLevel.High, safety.Level);
        Assert.Equal(DateTimeOffset.Parse("2026-01-10T00:00:00Z"), safety.OldestRiskAt);
        Assert.Contains(safety.Reasons, r => r.Kind == SafetyReasonKind.UnpushedCommits && r.Subject == "feature/x" && r.Count == 2 && r.Text.StartsWith("2 commits on feature/x not on any remote, oldest "));
        Assert.Contains(safety.Reasons, r => r.Kind == SafetyReasonKind.LocalOnlyBranch && r.Subject == "spike" && r.Text.EndsWith("(never pushed)"));
    }

    [Fact]
    public async Task A_branch_merged_and_then_deleted_on_the_remote_is_not_at_risk()
    {
        // The branch is pushed, merged into main on the remote, and its remote branch deleted.
        Git(Widget, "checkout", "-q", "-b", "done");
        Commit(Widget, "done.txt", "finished work");
        Git(Widget, "push", "-q", "-u", "origin", "done");
        Git(Widget, "checkout", "-q", "main");
        Git(Widget, "merge", "-q", "--no-ff", "-m", "merge done", "done");
        Git(Widget, "push", "-q", "origin", "main");
        Git(Widget, "push", "-q", "origin", "--delete", "done");
        // And one that only ever existed here but whose commit is already in origin/main.
        Git(Widget, "branch", "same-as-main", "main");

        var entry = await ScanWidgetAsync();
        var branches = entry.Git!.Branches.ToDictionary(b => b.Name);

        Assert.True(branches["done"].UpstreamGone);
        Assert.True(branches["done"].OnRemote);
        Assert.Null(branches["same-as-main"].Upstream);
        Assert.True(branches["same-as-main"].OnRemote);
        Assert.Equal(SafetyVerdict.Safe, Assess(entry).Verdict);
    }

    [Fact]
    public async Task Local_only_tags_are_found_by_asking_the_remote_and_never_without_fetching()
    {
        Git(Widget, "tag", "v2.0-local");
        Git(Widget, "tag", "-a", "v3.0-annotated", "-m", "release");
        Git(Widget, "tag", "pushed");
        Git(Widget, "push", "-q", "origin", "pushed");

        // A scan that may not fetch does not talk to the remote: the tags are unknown, not "none".
        var offline = await ScanWidgetAsync(fetch: false);
        Assert.Equal(4, offline.Git!.Safety!.TagCount);
        Assert.Null(offline.Git.Safety.RemoteTagsCheckedAt);
        Assert.Empty(offline.Git.Safety.LocalOnlyTags);
        var unsure = Assess(offline);
        Assert.Equal(SafetyVerdict.Unclear, unsure.Verdict);
        Assert.Equal(SafetyReasonKind.TagsUnknown, Assert.Single(unsure.Reasons).Kind);

        var scanner = CreateScanner(autoFetch: true);
        var online = (await scanner.ScanAsync(new ScanRequest(AllowFetch: true, SizeMode.CachedOnly), CancellationToken.None)).Entries.Single(e => e.Name == "widget");
        Assert.Equal(["v2.0-local", "v3.0-annotated"], online.Git!.Safety!.LocalOnlyTags);
        Assert.Equal(2, online.Git.Safety.LocalOnlyTagCount);
        Assert.NotNull(online.Git.Safety.RemoteTagsCheckedAt);
        var risky = Assess(online);
        Assert.Equal(SafetyVerdict.AtRisk, risky.Verdict);
        Assert.Equal("2 tags not on any remote: v2.0-local, v3.0-annotated", Assert.Single(risky.Reasons).Text);

        // A quick rescan (a file changed) reuses that answer without the network, and still sees a new local tag.
        Git(Widget, "tag", "v4.0-new");
        var quick = (await scanner.ScanAsync(new ScanRequest(AllowFetch: false, SizeMode.CachedOnly), CancellationToken.None)).Entries.Single(e => e.Name == "widget");
        Assert.Equal(["v2.0-local", "v3.0-annotated", "v4.0-new"], quick.Git!.Safety!.LocalOnlyTags);
        Assert.Equal(online.Git.Safety.RemoteTagsCheckedAt, quick.Git.Safety.RemoteTagsCheckedAt);
    }

    [Fact]
    public async Task Ignored_files_that_are_not_build_output_are_named_and_need_acknowledging()
    {
        File.WriteAllText(Path.Combine(Widget, ".env"), "TOKEN=never-read");
        File.WriteAllText(Path.Combine(Widget, "debug.log"), "log");
        Directory.CreateDirectory(Path.Combine(Widget, "local"));
        File.WriteAllText(Path.Combine(Widget, "local", "notes.md"), "mine");
        Directory.CreateDirectory(Path.Combine(Widget, "node_modules"));
        File.WriteAllText(Path.Combine(Widget, "node_modules", "x.js"), "dep");

        var entry = await ScanWidgetAsync();
        var facts = entry.Git!.Safety!;

        Assert.Equal([".env", "debug.log", "local/"], facts.IgnoredFiles);
        Assert.Equal(3, facts.IgnoredFileCount);
        Assert.Empty(facts.SecretFiles); // .env is ignored, so it cannot be committed by accident

        var safety = Assess(entry);
        Assert.Equal(SafetyVerdict.SafeWithCaveats, safety.Verdict);
        Assert.Equal([".env", "debug.log", "local/"], Assert.Single(safety.Caveats).Names);
    }

    [Fact]
    public async Task Ignored_caches_restored_packages_and_test_reports_are_not_worth_mentioning()
    {
        File.WriteAllText(Path.Combine(Widget, ".git", "info", "exclude"), ".angular/\npackages/\ntest-results/\nplaywright-report/\ncoverage/\n.nyc_output/\n.astro/\n.idea/\n.vscode/\n.claude/\nvendor/\n");
        File.WriteAllText(Path.Combine(Widget, "App.sln"), "");
        Git(Widget, "add", "App.sln");
        Git(Widget, "commit", "-q", "-m", "solution");
        foreach (var dir in (string[])[".angular/cache", "packages/Lib.1.0.0", "test-results", "playwright-report", "coverage", ".nyc_output", ".astro", ".idea", ".vscode", ".claude", "vendor/packages"])
        {
            Directory.CreateDirectory(Path.Combine(Widget, dir));
            File.WriteAllText(Path.Combine(Widget, dir, "file"), "x");
        }

        var facts = (await ScanWidgetAsync()).Git!.Safety!;

        // Editor and assistant settings are somebody's. vendor/packages counts because the solution is in the repository root.
        Assert.Equal([".claude/", ".idea/", ".vscode/"], facts.IgnoredFiles);
    }

    [Fact]
    public async Task A_packages_folder_without_a_solution_is_somebodys_files()
    {
        File.WriteAllText(Path.Combine(Widget, ".git", "info", "exclude"), "packages/\ntest-results/\n");
        File.Delete(Path.Combine(Widget, "package.json"));
        Git(Widget, "commit", "-q", "-am", "no manifest");
        foreach (var dir in (string[])["packages", "test-results"])
        {
            Directory.CreateDirectory(Path.Combine(Widget, dir));
            File.WriteAllText(Path.Combine(Widget, dir, "file"), "x");
        }

        var facts = (await ScanWidgetAsync()).Git!.Safety!;

        Assert.Equal(["packages/", "test-results/"], facts.IgnoredFiles);
    }

    [Fact]
    public async Task The_list_of_ignored_names_is_capped_but_the_count_is_not()
    {
        for (var i = 0; i < SafetyFacts.MaxNames + 7; i++)
        {
            File.WriteAllText(Path.Combine(Widget, $"run-{i:000}.log"), "x");
        }

        var facts = (await ScanWidgetAsync()).Git!.Safety!;

        Assert.Equal(SafetyFacts.MaxNames, facts.IgnoredFiles.Count);
        Assert.Equal(SafetyFacts.MaxNames + 7, facts.IgnoredFileCount);
    }

    [Fact]
    public async Task Untracked_secret_looking_files_are_found_by_name_even_inside_untracked_folders()
    {
        const string secret = "-----BEGIN PRIVATE KEY----- do-not-leak";
        File.WriteAllText(Path.Combine(Widget, "id_rsa"), secret);
        File.WriteAllText(Path.Combine(Widget, ".env.local"), secret);
        File.WriteAllText(Path.Combine(Widget, ".env"), secret); // ignored by .gitignore
        File.WriteAllText(Path.Combine(Widget, ".env.example"), "TOKEN="); // a template
        Directory.CreateDirectory(Path.Combine(Widget, "certs", "inner"));
        File.WriteAllText(Path.Combine(Widget, "certs", "inner", "server.pem"), secret);
        File.WriteAllText(Path.Combine(Widget, "certs", "trace.log"), "ignored by *.log");
        File.WriteAllText(Path.Combine(Widget, "certs", "readme.txt"), "plain");
        // A committed key is not this rule's business.
        File.WriteAllText(Path.Combine(Widget, "committed.key"), "public test fixture");
        Git(Widget, "add", "committed.key");
        Git(Widget, "commit", "-q", "-m", "fixture key");
        Git(Widget, "push", "-q", "origin", "main");

        var snapshot = await ScanAsync();
        var entry = snapshot.Entries.Single(e => e.Name == "widget");
        var facts = entry.Git!.Safety!;

        Assert.Equal([".env.local", "certs/inner/server.pem", "id_rsa"], facts.SecretFiles);
        Assert.Equal(3, facts.SecretFileCount);

        // Names only: nothing the agent sends contains a byte of the files.
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot, SnapshotSchema.JsonOptions);
        Assert.DoesNotContain("do-not-leak", json);
        Assert.DoesNotContain("PRIVATE KEY", json);

        var finding = Assert.Single(new UntrackedSecretsRule().Evaluate(new CleanupContext { Machines = [snapshot] }));
        Assert.Contains("printf '%s\\n' '/.env.local' '/certs/inner/server.pem' '/id_rsa' >> ", finding.Commands.Single(c => c.Kind == CommandKind.Fix).Command);

        var safety = Assess(entry);
        Assert.Equal(SafetyVerdict.AtRisk, safety.Verdict);
        Assert.Contains(safety.Reasons, r => r.Kind == SafetyReasonKind.UntrackedFiles && r.Text.Contains("including .env.local, certs/inner/server.pem, id_rsa"));
    }

    [Fact]
    public async Task Submodules_are_checked_for_uncommitted_and_unpushed_work()
    {
        // A second "remote" used as a submodule, added and pushed.
        var libOrigin = Path.Combine(_root, "origin", "lib.git");
        var libSeed = Path.Combine(_root, "lib-seed");
        Directory.CreateDirectory(libSeed);
        Git(libSeed, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(libSeed, "lib.txt"), "lib");
        Git(libSeed, "add", ".");
        Git(libSeed, "commit", "-q", "-m", "lib");
        Git(_root, "clone", "-q", "--bare", libSeed, libOrigin);
        Git(Widget, "-c", "protocol.file.allow=always", "submodule", "add", "-q", libOrigin, "libs/lib");
        Git(Widget, "commit", "-q", "-m", "add lib");
        Git(Widget, "push", "-q", "origin", "main");
        var lib = Path.Combine(Widget, "libs", "lib");

        var clean = await ScanWidgetAsync();
        var sub = Assert.Single(clean.Git!.Safety!.Submodules);
        Assert.Equal("libs/lib", sub.Path);
        Assert.True(sub is { Initialized: true, Checked: true, Dirty: false, Unpushed: false, Stashes: 0 });
        Assert.Equal(SafetyVerdict.Safe, Assess(clean).Verdict);

        // A commit made inside the submodule and never pushed, plus an untracked file there.
        Commit(lib, "local.txt", "local lib change");
        File.WriteAllText(Path.Combine(lib, "scratch.txt"), "?");

        var changed = await ScanWidgetAsync();
        sub = Assert.Single(changed.Git!.Safety!.Submodules);
        Assert.True(sub is { Dirty: true, Unpushed: true });
        var safety = Assess(changed);
        Assert.Equal(SafetyVerdict.AtRisk, safety.Verdict);
        Assert.Contains(safety.Reasons, r => r.Kind == SafetyReasonKind.SubmoduleUnpushed && r.Subject == "libs/lib");
        Assert.Contains(safety.Reasons, r => r.Kind == SafetyReasonKind.SubmoduleDirty && r.Subject == "libs/lib");
    }

    [Fact]
    public async Task A_linked_worktree_names_its_main_repo_and_is_assessed_on_its_own_folder()
    {
        var linkedPath = Path.Combine(_dev, "widget.worktrees", "hotfix");
        Directory.CreateDirectory(Path.GetDirectoryName(linkedPath)!);
        Git(Widget, "worktree", "add", "-q", linkedPath, "-b", "hotfix", "main");
        Commit(linkedPath, "fix.txt", "hotfix work");

        var snapshot = await ScanAsync();
        var all = RepoSafety.AssessAll(snapshot, DateTimeOffset.UtcNow);
        var main = all.Single(c => c.Entry.Name == "widget");
        var linked = all.Single(c => c.Entry.Name == "hotfix");

        Assert.True(linked.Entry.Git!.IsLinkedWorktree);
        Assert.True(RepoIdentity.SamePath(main.Entry.Git!.Worktrees[0].Path, linked.Entry.Git.MainWorktreePath));
        Assert.Same(main.Entry, linked.Main);
        Assert.Equal(main.RepoKey, linked.RepoKey);

        // The unpushed hotfix commit belongs to the repository: the main working tree reports it, once.
        Assert.Equal(SafetyVerdict.AtRisk, main.Safety.Verdict);
        Assert.Contains(main.Safety.Reasons, r => r.Kind == SafetyReasonKind.LocalOnlyBranch && r.Subject == "hotfix" && r.Count == 1);
        Assert.Contains(main.Safety.Reasons, r => r.Kind == SafetyReasonKind.LinkedWorktrees);
        Assert.Equal(SafetyVerdict.Safe, linked.Safety.Verdict);
        Assert.False(linked.Safety.IncludesRepository);

        // Uncommitted work in the worktree is the worktree's own.
        File.WriteAllText(Path.Combine(linkedPath, "fix.txt"), "edited");
        var again = RepoSafety.AssessAll(await ScanAsync(), DateTimeOffset.UtcNow).Single(c => c.Entry.Name == "hotfix");
        Assert.Equal(SafetyVerdict.AtRisk, again.Safety.Verdict);
        Assert.Equal(SafetyReasonKind.UncommittedChanges, Assert.Single(again.Safety.Reasons).Kind);
    }

    [Fact]
    public async Task A_repo_with_no_remote_stashes_and_a_detached_head_is_at_risk()
    {
        var solo = Path.Combine(_dev, "solo");
        Directory.CreateDirectory(solo);
        Git(solo, "init", "-q", "-b", "main");
        Commit(solo, "a.txt", "only here");
        Git(solo, "tag", "v0");

        var entry = (await ScanAsync()).Entries.Single(e => e.Name == "solo");
        Assert.False(Assert.Single(entry.Git!.Branches).OnRemote);
        Assert.Equal(["v0"], entry.Git.Safety!.LocalOnlyTags);
        var safety = Assess(entry);
        Assert.Equal(SafetyVerdict.AtRisk, safety.Verdict);
        Assert.Equal("No remote: 1 branch and 1 tag exist only here", Assert.Single(safety.Reasons).Text);

        // In the clone: a stash, and a commit made on a detached HEAD that no branch holds.
        File.WriteAllText(Path.Combine(Widget, "README.md"), "# Widget\n\nparked\n");
        Git(Widget, "stash", "push", "-q", "-m", "parked");
        Git(Widget, "checkout", "-q", "--detach");
        Commit(Widget, "loose.txt", "detached work");

        var widget = await ScanWidgetAsync();
        Assert.False(widget.Git!.Safety!.HeadOnRemote);
        var kinds = Assess(widget).Reasons.Select(r => r.Kind).ToList();
        Assert.Equal([SafetyReasonKind.DetachedHead, SafetyReasonKind.Stashes], kinds);
    }

    private void Commit(string repo, string file, string message, string? date = null)
    {
        File.WriteAllText(Path.Combine(repo, file), message);
        Git(repo, "add", file);
        GitAt(date, repo, "commit", "-q", "-m", message);
    }

    private MachineScanner CreateScanner(bool autoFetch)
    {
        var options = new StaticOptions(new AgentOptions
        {
            Roots = [_dev],
            MachineId = "test-machine",
            AutoFetch = new AutoFetchOptions { Enabled = autoFetch, IntervalHours = 4, TimeoutSeconds = 30 },
        });
        var git = new GitRunner();
        var fetch = new FetchService(git, options, NullLogger<FetchService>.Instance);
        var cache = new SizeCache(Path.Combine(_root, "sizes.json"));
        return new MachineScanner(new RepoScanner(git, fetch, options), cache, options, NullLogger<MachineScanner>.Instance);
    }

    /// <summary>Test-only git, used to build fixtures. The agent itself can only go through <see cref="GitRunner"/>.</summary>
    private static string Git(string workingDirectory, params string[] args) => GitAt(null, workingDirectory, args);

    private static string GitAt(string? committerDate, string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in (string[])["-c", "user.name=DevDash Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", "-c", "init.defaultBranch=main", .. args])
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (committerDate is not null)
        {
            psi.Environment["GIT_COMMITTER_DATE"] = committerDate;
            psi.Environment["GIT_AUTHOR_DATE"] = committerDate;
        }

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

    private sealed class StaticOptions(AgentOptions value) : IOptionsMonitor<AgentOptions>
    {
        public AgentOptions CurrentValue => value;

        public AgentOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AgentOptions, string?> listener) => null;
    }
}
