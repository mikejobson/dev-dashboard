using System.Net;
using DevDash.Contracts;
using DevDash.Core.GitHub;
using DevDash.Hub.GitHub;
using DevDash.Hub.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Hub.Tests;

/// <summary>Which open pull request belongs to which branch, and the chip that shows it.</summary>
public class BranchPullRequestTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static PullRequestInfo Pr(int number, string head, string? sha = null, bool fork = false, CiState ci = CiState.None, bool draft = false) => new()
    {
        Repo = "acme/widget", Number = number, Title = $"PR {number}", HeadRef = head, HeadSha = sha, IsCrossRepository = fork, Ci = ci, IsDraft = draft,
        Url = $"https://github.com/acme/widget/pull/{number}",
    };

    [Fact]
    public void A_branch_matches_the_pull_request_for_its_name_on_the_remote()
    {
        string[] remotes = ["origin", "upstream"];
        var prs = new[] { Pr(1, "feature/login"), Pr(2, "main"), Pr(3, "other") };

        // Its own name when it tracks nothing, or tracks the same name.
        Assert.Equal([1], BranchPullRequests.For(prs, new BranchInfo { Name = "feature/login" }, remotes).Select(m => m.PullRequest.Number));
        Assert.Equal([1], BranchPullRequests.For(prs, new BranchInfo { Name = "feature/login", Upstream = "origin/feature/login" }, remotes).Select(m => m.PullRequest.Number));

        // The branch it tracks, when that has another name: "wip" pushed as feature/login.
        Assert.Equal([1], BranchPullRequests.For(prs, new BranchInfo { Name = "wip", Upstream = "upstream/feature/login" }, remotes).Select(m => m.PullRequest.Number));
        Assert.Empty(BranchPullRequests.For(prs, new BranchInfo { Name = "other", Upstream = "origin/elsewhere" }, remotes));

        // Names are exact: no prefixes, no case folding.
        Assert.Empty(BranchPullRequests.For(prs, new BranchInfo { Name = "feature" }, remotes));
        Assert.Empty(BranchPullRequests.For(prs, new BranchInfo { Name = "Main" }, remotes));
        Assert.Equal("feature/login", BranchPullRequests.RemoteName(new BranchInfo { Name = "x", Upstream = "origin/feature/login" }, remotes));
        Assert.Equal("x", BranchPullRequests.RemoteName(new BranchInfo { Name = "x", Upstream = "unknown/feature" }, remotes));
    }

    [Fact]
    public void The_head_commit_settles_it_when_both_sides_know_theirs()
    {
        // Same name, same commit: the branch is exactly what the pull request is.
        Assert.True(BranchPullRequests.Match(Pr(1, "feature", A), "feature", A)!.AtHead);

        // Same name, another commit: still its pull request, but not at its head (unpushed work, or a fetch behind).
        Assert.False(BranchPullRequests.Match(Pr(1, "feature", A), "feature", B)!.AtHead);

        // The commit is not known on one side: the name decides, and nothing is claimed about the commit.
        Assert.Null(BranchPullRequests.Match(Pr(1, "feature"), "feature", B)!.AtHead);
        Assert.Null(BranchPullRequests.Match(Pr(1, "feature", A), "feature", "")!.AtHead);

        // Another name, the same commit: found by the commit.
        Assert.True(BranchPullRequests.Match(Pr(1, "feature", A), "local-name", A)!.AtHead);
        Assert.Null(BranchPullRequests.Match(Pr(1, "feature", A), "local-name", B));

        // An abbreviated id is the same commit.
        Assert.True(BranchPullRequests.SameCommit(A[..10], A));
        Assert.False(BranchPullRequests.SameCommit(A[..10], B));
        Assert.Null(BranchPullRequests.SameCommit(null, A));
    }

    [Fact]
    public void A_pull_request_from_a_fork_only_matches_by_commit()
    {
        // The fork's "main" is not this repository's main.
        Assert.Null(BranchPullRequests.Match(Pr(7, "main", A, fork: true), "main", B));
        Assert.Null(BranchPullRequests.Match(Pr(7, "main", fork: true), "main", B));
        Assert.True(BranchPullRequests.Match(Pr(7, "main", A, fork: true), "pr-7", A)!.AtHead);
    }

    [Fact]
    public void Several_pull_requests_from_one_branch_list_the_one_at_this_commit_first()
    {
        var matches = BranchPullRequests.For([Pr(9, "feature", B), Pr(4, "feature"), Pr(12, "feature", A)], "feature", A);

        Assert.Equal([12, 4, 9], matches.Select(m => m.PullRequest.Number));
    }

    [Fact]
    public async Task The_repo_page_puts_the_pull_request_on_the_map_and_on_the_branch_row()
    {
        using var factory = new HubFactory();
        await factory.Services.GetRequiredService<SnapshotStore>().IngestAsync(HubFactory.Snapshot());
        await factory.Services.GetRequiredService<GitHubStore>().SetAsync(new GitHubSnapshot
        {
            ViewerLogin = "me",
            PullRequests =
            [
                Pr(42, "feature/login", "1111111111111111111111111111111111111111", ci: CiState.Failure) with { Title = "Add the login form", ReviewDecision = "CHANGES_REQUESTED" },
                Pr(43, "elsewhere", ci: CiState.Success, draft: true),
            ],
        });

        var html = WebUtility.HtmlDecode(await factory.CreateClient().GetStringAsync("/repo?key=github.com%2Facme%2Fwidget"));

        // On the branch's row in the branch table: number, CI, review state, and a link to GitHub.
        var chip = html[html.IndexOf("class=\"pr-chip", StringComparison.Ordinal)..];
        chip = chip[..chip.IndexOf("</a>", StringComparison.Ordinal)];
        Assert.Contains("href=\"https://github.com/acme/widget/pull/42\"", chip);
        Assert.Contains("#42", chip);
        Assert.Contains("dot danger", chip);
        Assert.Contains("CI failing", chip);
        Assert.Contains("changes", chip);
        Assert.Contains("#42 Add the login form", html); // the tooltip

        // A pull request whose branch no machine has is not pinned on some other branch.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, @"pull/43"""));
    }
}
