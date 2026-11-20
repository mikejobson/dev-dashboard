using DevDash.Agent.Git;

namespace DevDash.Core.Tests.Agent;

public class GitRunnerTests
{
    [Theory]
    [InlineData("status", "--porcelain=v2", "--branch")]
    [InlineData("for-each-ref", "--format=%(refname)", "refs/heads")]
    [InlineData("worktree", "list", "--porcelain")]
    [InlineData("stash", "list", "--format=%gd")]
    [InlineData("remote", "-v")]
    [InlineData("symbolic-ref", "--short", "refs/remotes/origin/HEAD")]
    [InlineData("rev-parse", "--git-common-dir")]
    [InlineData("log", "-1", "--format=%H")]
    [InlineData("rev-list", "--count", "--first-parent", "origin/main")]
    [InlineData("status", "--porcelain=v2", "--branch", "--ignored")]
    [InlineData("submodule", "status", "--recursive")]
    [InlineData("merge-base", "0000000000000000000000000000000000000000", "1111111111111111111111111111111111111111")]
    [InlineData("diff", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--full-index", "--unified=0", "0000000000000000000000000000000000000000", "1111111111111111111111111111111111111111")]
    public void Allows_the_read_only_commands_the_scanner_uses(params string[] args) =>
        GitRunner.EnsureReadOnly(args);

    [Theory]
    [InlineData("commit", "-m", "x")]
    [InlineData("push")]
    [InlineData("fetch")] // only through FetchAsync, with fixed arguments
    [InlineData("reset", "--hard")]
    [InlineData("clean", "-fdx")]
    [InlineData("checkout", "main")]
    [InlineData("branch", "-D", "x")]
    [InlineData("worktree", "remove", "/x")]
    [InlineData("worktree", "prune")]
    [InlineData("stash", "drop")]
    [InlineData("stash", "pop")]
    [InlineData("remote", "remove", "origin")]
    [InlineData("remote", "set-url", "origin", "x")]
    [InlineData("symbolic-ref", "HEAD", "refs/heads/x")]
    [InlineData("submodule", "update", "--init")]
    [InlineData("submodule", "foreach", "--recursive", "git clean -fdx")]
    [InlineData("ls-remote", "--tags", "origin")] // only through ListRemoteTagsAsync, with fixed arguments
    [InlineData("gc")]
    [InlineData("rev-list", "--all")] // only the counting form
    [InlineData("rev-list", "--count")]
    [InlineData("diff", "--no-ext-diff", "--no-textconv", "HEAD")] // the working tree is not compared
    [InlineData("diff", "--no-ext-diff", "--no-textconv", "--output=/tmp/x", "0000000000000000000000000000000000000000", "1111111111111111111111111111111111111111")]
    [InlineData("diff", "0000000000000000000000000000000000000000", "1111111111111111111111111111111111111111")] // external diff helpers must be off
    [InlineData("merge-base", "main", "feature")] // commit names only
    [InlineData("status", "some-path")]
    public void Refuses_anything_that_could_change_a_repository(params string[] args) =>
        Assert.Throws<InvalidOperationException>(() => GitRunner.EnsureReadOnly(args));

    [Fact]
    public async Task RunAsync_refuses_before_starting_a_process()
    {
        var runner = new GitRunner("this-binary-does-not-exist");

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Path.GetTempPath(), ["push", "--force"], TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_missing_git_binary_is_a_failed_result_not_an_exception()
    {
        var runner = new GitRunner("this-binary-does-not-exist");

        var result = await runner.RunAsync(Path.GetTempPath(), ["status", "--porcelain=v2"], TimeSpan.FromSeconds(1));

        Assert.False(result.Ok);
    }
}
