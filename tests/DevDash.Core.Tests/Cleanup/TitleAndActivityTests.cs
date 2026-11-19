using DevDash.Contracts;
using DevDash.Core.Cleanup;
using DevDash.Core.Cleanup.Rules;
using static DevDash.Core.Tests.Cleanup.Build;

namespace DevDash.Core.Tests.Cleanup;

public class RuleTitleTests
{
    [Fact]
    public void No_rule_heading_has_a_placeholder_in_it()
    {
        var options = new CleanupOptions();
        foreach (var rule in CleanupEngine.DefaultRules())
        {
            foreach (var text in new[] { rule.Title, rule.TitleFor(options), rule.Description })
            {
                Assert.DoesNotMatch(@"\bN\b", text);
                Assert.DoesNotContain("{", text);
            }
        }
    }

    [Fact]
    public void Headings_name_the_configured_thresholds()
    {
        var options = new CleanupOptions { UncommittedDays = 21, StashDays = 45, UntouchedDays = 365 };

        Assert.Equal("Uncommitted changes older than 14 days", new OldUncommittedChangesRule().Title);
        Assert.Equal("Uncommitted changes older than 21 days", ((ICleanupRule)new OldUncommittedChangesRule()).TitleFor(options));
        Assert.Equal("Stashes older than 30 days", new OldStashRule().Title);
        Assert.Equal("Stashes older than 45 days", ((ICleanupRule)new OldStashRule()).TitleFor(options));
        Assert.Equal("Repos untouched for more than 6 months", new UntouchedRepoRule().Title);
        Assert.Equal("Repos untouched for more than 365 days", ((ICleanupRule)new UntouchedRepoRule()).TitleFor(options));
        Assert.Equal("Uncommitted changes older than 1 day", ((ICleanupRule)new OldUncommittedChangesRule()).TitleFor(new CleanupOptions { UncommittedDays = 1 }));
        // A rule with no threshold keeps its plain heading.
        Assert.Equal("Stale PRs", ((ICleanupRule)new StalePullRequestRule()).TitleFor(options));
    }
}

public class LastActivityTests
{
    [Fact]
    public void Uncommitted_changes_date_from_the_newest_edit_not_from_the_repo()
    {
        var git = GitOf(with: g => g with { Unstaged = 1, NewestTrackedChangeAt = DaysAgo(120) });

        var finding = Assert.Single(new OldUncommittedChangesRule().Evaluate(Context(Machine("mac", Repo("widget", git, modifiedDaysAgo: 2)))));

        Assert.Equal(DaysAgo(120), finding.LastActivityAt);
    }

    [Fact]
    public void Branch_findings_date_from_the_branch_tip()
    {
        var git = GitOf(with: g => g with
        {
            Branches =
            [
                .. g.Branches,
                Branch("fresh", upstream: "origin/fresh", ahead: 2, ageDays: 3),
                Branch("ancient", upstream: "origin/ancient", ahead: 1, ageDays: 400),
                Branch("gone", upstream: "origin/gone", gone: true, ageDays: 200),
            ],
        });
        var context = Context(Machine("mac", Repo("widget", git)));

        var unpushed = new UnpushedCommitsRule().Evaluate(context).ToList();
        Assert.Equal(DaysAgo(3), unpushed.Single(f => f.Title.Contains("fresh")).LastActivityAt);
        Assert.Equal(DaysAgo(400), unpushed.Single(f => f.Title.Contains("ancient")).LastActivityAt);
        Assert.Equal(DaysAgo(200), Assert.Single(new GoneUpstreamRule().Evaluate(context)).LastActivityAt);
    }

    [Fact]
    public void Pull_request_findings_date_from_the_last_update()
    {
        var context = new CleanupContext { Now = Now, PullRequests = [Pr(1, idleDays: 45, ci: DevDash.Core.GitHub.CiState.Failure)] };

        Assert.Equal(DaysAgo(45), Assert.Single(new StalePullRequestRule().Evaluate(context)).LastActivityAt);
        Assert.Equal(DaysAgo(45), Assert.Single(new FailingCiPullRequestRule().Evaluate(context)).LastActivityAt);
    }

    [Fact]
    public void Other_findings_fall_back_to_the_folders_newest_commit_or_edit()
    {
        var size = new SizeInfo
        {
            TotalBytes = 900L * 1024 * 1024,
            ReclaimableBytes = 700L * 1024 * 1024,
            ReclaimableDirs = [new ReclaimableDir { Path = "/dev/widget/node_modules", Bytes = 700L * 1024 * 1024 }],
        };
        var context = Context(Machine("mac", Repo("widget", modifiedDaysAgo: 9, size: size), Folder("Docs")));

        var build = Assert.Single(new LargeBuildDirsRule().Evaluate(context));
        // The repo's last commit (1 day ago) is newer than its newest file edit.
        Assert.Equal(DaysAgo(1), build.LastActivityAt);
        // A finding about a repo's folder belongs to that repo, so it groups with the repo's other findings.
        Assert.Equal("github.com/acme/widget", build.RepoKey);

        var folder = Assert.Single(new NonGitFolderRule().Evaluate(context));
        Assert.Equal(DaysAgo(10), folder.LastActivityAt);
        Assert.Null(folder.RepoKey);
    }

    [Fact]
    public void The_date_is_not_part_of_the_fingerprint()
    {
        Finding At(double days) => Assert.Single(new OldUncommittedChangesRule().Evaluate(
            Context(Machine("mac", Repo("widget", GitOf(with: g => g with { Unstaged = 1, NewestTrackedChangeAt = DaysAgo(days) }))))));

        Assert.Equal(At(20).Fingerprint, At(90).Fingerprint);
    }
}
