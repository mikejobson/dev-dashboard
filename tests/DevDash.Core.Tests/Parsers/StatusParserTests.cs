using DevDash.Core.Git;

namespace DevDash.Core.Tests.Parsers;

public class StatusParserTests
{
    [Fact]
    public void Parses_branch_headers_and_counts_from_a_real_repo()
    {
        var status = StatusParser.Parse(Fixture.Read("status.core.txt"));

        Assert.Equal("21dce74aa471963065aeff2f55b890c237860a8b", status.HeadSha);
        Assert.Equal("develop", status.Branch);
        Assert.False(status.Detached);
        Assert.Equal("origin/develop", status.Upstream);
        Assert.False(status.UpstreamGone);
        Assert.Equal(0, status.Ahead);
        Assert.Equal(0, status.Behind);
        Assert.Equal(0, status.Staged);
        Assert.Equal(1, status.Unstaged);
        Assert.Equal(2, status.Untracked);
        Assert.Contains(status.Paths, p => p is { Path: "tools/dev-env/bin/stack-rebuild.sh", Tracked: true });
        Assert.Contains(status.Paths, p => p is { Path: ".claude/", Tracked: false });
    }

    [Fact]
    public void Branch_without_upstream_and_with_staged_files()
    {
        var status = StatusParser.Parse(Fixture.Read("status.no-upstream-staged.txt"));

        Assert.Equal("v4-update", status.Branch);
        Assert.Null(status.Upstream);
        Assert.False(status.UpstreamGone);
        Assert.Equal(2, status.Staged);    // two "AM" entries
        Assert.Equal(17, status.Unstaged); // every tracked entry has a worktree change
        Assert.Equal(4, status.Untracked);
        Assert.Contains(status.Paths, p => p.Path == "Designer/WidgetTest V4_Manifest.zip" && !p.Tracked);
    }

    [Fact]
    public void Upstream_without_ahead_behind_line_means_the_upstream_is_gone()
    {
        var status = StatusParser.Parse(Fixture.Read("status.upstream-gone.txt"));

        Assert.Equal("origin/feature/create-app", status.Upstream);
        Assert.True(status.UpstreamGone);
    }

    [Fact]
    public void Parses_behind_count()
    {
        var status = StatusParser.Parse(Fixture.Read("status.behind.txt"));

        Assert.Equal(0, status.Ahead);
        Assert.Equal(2, status.Behind);
        Assert.Equal(2, status.Unstaged);
    }

    [Fact]
    public void Handles_detached_head_renames_conflicts_and_ignored_entries()
    {
        const string output = """
            # branch.oid 1111111111111111111111111111111111111111
            # branch.head (detached)
            2 R. N... 100644 100644 100644 aaaa bbbb R100 new name.txt	old name.txt
            u UU N... 100644 100644 100644 100644 aaaa bbbb cccc both.txt
            1 M. N... 100644 100644 100644 aaaa bbbb staged only.txt
            ? scratch.txt
            ! ignored.log
            """;

        var status = StatusParser.Parse(output);

        Assert.True(status.Detached);
        Assert.Null(status.Branch);
        Assert.Equal(2, status.Staged);
        Assert.Equal(0, status.Unstaged);
        Assert.Equal(1, status.Conflicted);
        Assert.Equal(1, status.Untracked);
        Assert.Equal(["new name.txt", "both.txt", "staged only.txt", "scratch.txt"], status.Paths.Select(p => p.Path));
    }

    [Fact]
    public void Initial_commit_has_no_sha()
    {
        var status = StatusParser.Parse("# branch.oid (initial)\n# branch.head main\n");

        Assert.True(status.Initial);
        Assert.Null(status.HeadSha);
        Assert.Equal("main", status.Branch);
    }

    [Fact]
    public void Empty_output_is_a_clean_unknown_state()
    {
        var status = StatusParser.Parse("");

        Assert.Null(status.Branch);
        Assert.Empty(status.Paths);
    }

    [Theory]
    [InlineData("plain.txt", "plain.txt")]
    [InlineData("\"with \\\"quotes\\\".txt\"", "with \"quotes\".txt")]
    [InlineData("\"tab\\there.txt\"", "tab\there.txt")]
    [InlineData("\"caf\\303\\251.txt\"", "café.txt")]
    public void Unquotes_c_style_paths(string quoted, string expected) =>
        Assert.Equal(expected, StatusParser.Unquote(quoted));
}
