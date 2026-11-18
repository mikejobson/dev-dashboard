using DevDash.Core.Git;

namespace DevDash.Core.Tests.Parsers;

public class WorktreeListParserTests
{
    [Fact]
    public void Parses_a_real_repo_with_many_worktrees()
    {
        var worktrees = WorktreeListParser.Parse(Fixture.Read("worktree-list.core.txt"));

        Assert.Equal(12, worktrees.Count);

        var main = worktrees[0];
        Assert.Equal("/Users/dev/Developer/core", main.Path);
        Assert.Equal("develop", main.Branch);
        Assert.Equal("21dce74aa471963065aeff2f55b890c237860a8b", main.Head);

        var detached = Assert.Single(worktrees, w => w.Detached);
        Assert.Null(detached.Branch);
        Assert.EndsWith("youthful-perlman-69db18", detached.Path);

        var external = worktrees[^1];
        Assert.Equal("/Users/dev/Developer/core.worktrees/panel-app-polish", external.Path);
        Assert.Equal("feature/panel-app-polish", external.Branch);
        Assert.DoesNotContain(worktrees, w => w.Prunable);
    }

    [Fact]
    public void Parses_prunable_locked_and_bare_entries()
    {
        const string output = """
            worktree /repos/thing.git
            bare

            worktree /tmp/gone
            HEAD da5ecb813f577f6cf54a2621ba46e8bac6dc8110
            detached
            prunable gitdir file points to non-existent location

            worktree /repos/thing-hotfix
            HEAD 0000000000000000000000000000000000000001
            branch refs/heads/hotfix/a b
            locked keep for now
            """;

        var worktrees = WorktreeListParser.Parse(output);

        Assert.Equal(3, worktrees.Count);
        Assert.True(worktrees[0].Bare);
        Assert.True(worktrees[1].Prunable);
        Assert.Equal("gitdir file points to non-existent location", worktrees[1].PrunableReason);
        Assert.True(worktrees[1].Detached);
        Assert.True(worktrees[2].Locked);
        Assert.Equal("hotfix/a b", worktrees[2].Branch);
    }

    [Fact]
    public void Empty_output_gives_no_worktrees() => Assert.Empty(WorktreeListParser.Parse(""));
}
