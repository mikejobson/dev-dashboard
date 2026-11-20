using DevDash.Contracts;
using DevDash.Core.Cleanup;
using DevDash.Core.Cleanup.Rules;
using DevDash.Core.Git;
using static DevDash.Core.Tests.Cleanup.Build;

namespace DevDash.Core.Tests.Safety;

public class SecretFilesTests
{
    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData(".ENV.production")]
    [InlineData("server.pem")]
    [InlineData("private.key")]
    [InlineData("cert.p12")]
    [InlineData("signing.pfx")]
    [InlineData("id_rsa")]
    [InlineData("id_rsa.old")]
    [InlineData("id_ed25519")]
    [InlineData("credentials.json")]
    [InlineData("credentials-prod.json")]
    public void Recognises_secret_looking_names(string name) => Assert.True(SecretFiles.LooksSecret(name));

    [Theory]
    [InlineData(".env.example")]
    [InlineData(".env.sample")]
    [InlineData(".envrc")]
    [InlineData(".environment")]
    [InlineData("id_rsa.pub")]
    [InlineData("keyboard.txt")]
    [InlineData("monkey")]
    [InlineData(".pem")]
    [InlineData("credentials.md")]
    [InlineData("package.json")]
    [InlineData("")]
    public void Leaves_ordinary_names_and_templates_alone(string name) => Assert.False(SecretFiles.LooksSecret(name));

    [Theory]
    [InlineData("config/dev.pem", "dev.pem")]
    [InlineData("a\\b\\.env", ".env")]
    [InlineData(".env", ".env")]
    [InlineData("secrets/", "secrets")]
    public void Takes_the_file_name_from_either_kind_of_path(string path, string expected) => Assert.Equal(expected, SecretFiles.FileNameOf(path));
}

public class BuildOutputTests
{
    [Theory]
    [InlineData("__pycache__")]
    [InlineData(".DS_Store")]
    [InlineData("module.pyc")]
    [InlineData(".pytest_cache")]
    [InlineData(".vs")]
    [InlineData(".nyc_output")]
    public void Caches_are_never_worth_keeping(string name) => Assert.True(BuildOutput.IsCache(name));

    [Theory]
    [InlineData(".env")]
    [InlineData(".idea")]
    [InlineData(".vscode")]
    [InlineData("local.db")]
    [InlineData(".claude")]
    [InlineData("packages")]
    [InlineData("coverage")]
    [InlineData("node_modules")] // reclaimable, which is a separate question
    public void Other_names_are_not_caches(string name) => Assert.False(BuildOutput.IsCache(name));

    [Theory]
    [InlineData(".angular")]
    [InlineData(".nuxt")]
    [InlineData(".svelte-kit")]
    [InlineData(".astro")]
    [InlineData(".dart_tool")]
    public void Framework_caches_are_reclaimable_wherever_they_are(string name) =>
        Assert.True(BuildOutput.IsReclaimable(name, new HashSet<string>()));

    [Theory]
    [InlineData(".idea")]
    [InlineData(".vscode")]
    [InlineData(".env")]
    [InlineData(".claude")]
    public void Settings_and_secrets_are_never_build_output(string name)
    {
        var beside = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "package.json", "App.sln", "App.csproj", "packages.config", "pyproject.toml" };
        Assert.False(BuildOutput.IsReclaimable(name, beside));
        Assert.False(BuildOutput.IsRecreated(name, () => beside, () => beside));
        Assert.False(BuildOutput.IsCache(name));
    }

    [Theory]
    [InlineData("packages", "App.sln", true)]
    [InlineData("packages", "packages.config", true)]
    [InlineData("packages", "package.json", false)] // a JavaScript monorepo's source
    [InlineData("packages", "App.csproj", false)]
    [InlineData("test-results", "package.json", true)]
    [InlineData("test-results", "notes.md", false)]
    [InlineData("playwright-report", "package.json", true)]
    [InlineData("playwright-report", "", false)]
    [InlineData("coverage", "package.json", true)]
    [InlineData("coverage", "pyproject.toml", true)]
    [InlineData("coverage", "App.sln", true)]
    [InlineData("coverage", "notes.md", false)]
    [InlineData("src", "package.json", false)]
    public void Restored_and_report_folders_only_count_next_to_what_makes_them(string name, string sibling, bool expected) =>
        Assert.Equal(expected, BuildOutput.IsRecreated(name, () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sibling }));

    [Fact]
    public void A_packages_folder_deeper_down_counts_when_the_solution_is_in_the_repository_root()
    {
        IReadOnlySet<string> beside = new HashSet<string> { "App.csproj" };
        Assert.True(BuildOutput.IsRecreated("packages", () => beside, () => new HashSet<string> { "App.sln" }));
        Assert.False(BuildOutput.IsRecreated("packages", () => beside, () => new HashSet<string> { "package.json" }));
    }

    [Theory]
    [InlineData("packages", "App.sln")]
    [InlineData("packages", "packages.config")]
    [InlineData("test-results", "package.json")]
    [InlineData("playwright-report", "package.json")]
    [InlineData("coverage", "package.json")]
    public void Restored_and_report_folders_are_never_counted_or_deleted(string name, string sibling) =>
        Assert.False(BuildOutput.IsReclaimable(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sibling }));

    [Fact]
    public void Ambiguous_folder_names_only_read_their_siblings_when_needed()
    {
        var asked = 0;
        IReadOnlySet<string> Siblings()
        {
            asked++;
            return new HashSet<string> { "App.csproj" };
        }

        Assert.True(BuildOutput.IsReclaimable("node_modules", Siblings));
        Assert.False(BuildOutput.IsReclaimable("src", Siblings));
        Assert.Equal(0, asked);
        Assert.True(BuildOutput.IsReclaimable("bin", Siblings));
        Assert.Equal(1, asked);
    }
}

public class StatusIgnoredTests
{
    [Fact]
    public void Ignored_entries_are_collected_apart_from_the_changes()
    {
        const string output = "# branch.oid 1111111111111111111111111111111111111111\n# branch.head main\n? id_rsa\n? newdir/\n! .env\n! node_modules/\n! \"sp ace/caf\\303\\251.log\"\n";

        var status = StatusParser.Parse(output);

        Assert.Equal(2, status.Untracked);
        Assert.Equal(["id_rsa", "newdir/"], status.Paths.Select(p => p.Path));
        Assert.Equal([".env", "node_modules/", "sp ace/café.log"], status.Ignored);
    }
}

public class WorktreeStateTests
{
    private static readonly WorktreeInfo Linked = new() { Path = "/dev/widget.worktrees/x", Branch = "x", Head = "2222222222222222222222222222222222222222" };

    private static GitState RepoWith(BranchInfo branch) => GitOf() with
    {
        Branches = [Branch("main", upstream: "origin/main"), branch],
        Worktrees = [new WorktreeInfo { Path = "/dev/widget", Branch = "main" }, Linked],
    };

    [Fact]
    public void Says_why_a_worktree_is_stale()
    {
        WorktreeStaleness Classify(BranchInfo branch, WorktreeInfo? wt = null) => WorktreeState.Classify(RepoWith(branch), wt ?? Linked, null, Now, 30);

        Assert.Equal(WorktreeStaleKind.None, Classify(Branch("x", ageDays: 2)).Kind);
        Assert.Equal(WorktreeStaleKind.Merged, Classify(Branch("x", merged: true)).Kind);
        Assert.Equal(WorktreeStaleKind.UpstreamGone, Classify(Branch("x", upstream: "origin/x", gone: true)).Kind);
        Assert.Equal(WorktreeStaleKind.Prunable, Classify(Branch("x"), Linked with { Prunable = true }).Kind);
        Assert.Equal(WorktreeStaleKind.None, Classify(Branch("x", merged: true), Linked with { Locked = true }).Kind);

        var idle = Classify(Branch("x", ageDays: 45));
        Assert.Equal(WorktreeStaleKind.Idle, idle.Kind);
        Assert.Equal("idle 45 days", idle.Label(Now));
        Assert.True(idle.IsStale);
    }

    [Fact]
    public void A_scanned_worktree_finds_itself_whatever_slashes_git_printed()
    {
        var git = RepoWith(Branch("x", merged: true)) with
        {
            IsLinkedWorktree = true,
            Branch = "x",
            HeadSha = Linked.Head,
            Worktrees = [new WorktreeInfo { Path = "C:/dev/widget", Branch = "main" }, Linked with { Path = "C:/dev/widget.worktrees/x" }],
        };
        var entry = new DevEntry { Root = @"C:\dev", Name = "x", Path = @"C:\dev\widget.worktrees\x", Kind = EntryKind.GitRepo, Git = git, LastModified = DaysAgo(1) };

        Assert.Equal(WorktreeStaleKind.Merged, WorktreeState.Of(entry, Now, 30)!.Kind);
        Assert.Null(WorktreeState.Of(Repo("widget"), Now, 30)); // a main working tree is never a stale worktree
    }
}

public class UntrackedSecretsRuleTests
{
    private readonly UntrackedSecretsRule _rule = new();

    [Fact]
    public void Flags_untracked_secret_looking_files_by_name_and_suggests_gitignore()
    {
        var git = GitOf() with { Untracked = 2, Safety = new SafetyFacts { SecretFiles = [".env", "config/dev's.pem"], SecretFileCount = 2 } };

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", Repo("widget", git)))));

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Equal("widget: 2 secret-looking files not in .gitignore", finding.Title);
        Assert.Contains(".env, config/dev's.pem are untracked and not ignored", finding.Explanation);
        Assert.Equal("""git -C '/dev/widget' status --short -- '.env' 'config/dev'\''s.pem'""", Assert.Single(Inspects(finding)));
        Assert.Equal("""printf '%s\n' '/.env' '/config/dev'\''s.pem' >> '/dev/widget/.gitignore'""", Fix(finding).Command);
        Assert.Null(Fix(finding).Action); // never run by an agent
    }

    [Fact]
    public void Says_nothing_without_the_fact_or_without_secrets()
    {
        var context = Context(Machine("mac",
            Repo("old-agent", GitOf() with { Untracked = 4 }),
            Repo("clean", GitOf() with { Safety = new SafetyFacts() })));

        Assert.Empty(_rule.Evaluate(context));
    }

    [Fact]
    public void A_new_secret_is_a_new_finding_so_a_dismissal_does_not_hide_it()
    {
        Finding For(params string[] names) => Assert.Single(_rule.Evaluate(Context(Machine("mac",
            Repo("widget", GitOf() with { Safety = new SafetyFacts { SecretFiles = names, SecretFileCount = names.Length } })))));

        Assert.Equal(For(".env").Fingerprint, For(".env").Fingerprint);
        Assert.NotEqual(For(".env").Fingerprint, For(".env", "id_rsa").Fingerprint);
    }
}
