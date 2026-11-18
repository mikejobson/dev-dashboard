using DevDash.Core.Git;

namespace DevDash.Core.Tests.Parsers;

public class StashListParserTests
{
    [Fact]
    public void Parses_real_stashes()
    {
        var stashes = StashListParser.Parse(Fixture.Read("stash-list.widget.txt"));

        Assert.Equal(3, stashes.Count);
        Assert.Equal([0, 1, 2], stashes.Select(s => s.Index));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788870205), stashes[0].Date);
        Assert.Equal("claude/order-history-deployment-kglqdt", stashes[0].Branch);
        Assert.StartsWith("WIP on claude/order-history-deployment-kglqdt: 02b322b", stashes[1].Message);
        Assert.Equal("claude/order-history-deployment-kglqdt", stashes[1].Branch);
    }

    [Fact]
    public void Keeps_messages_that_contain_colons()
    {
        var stashes = StashListParser.Parse(Fixture.Read("stash-list.core.txt"));

        Assert.Equal(2, stashes.Count);
        Assert.Equal("fix/login-social-buttons", stashes[0].Branch);
        Assert.Equal("On claude/mcp-phase3: wip: proxy host + Mcp__PublicBaseUrl (from claude/mcp-phase3)", stashes[1].Message);
    }

    [Fact]
    public void Ignores_lines_it_cannot_read()
    {
        var stashes = StashListParser.Parse("garbage\nstash@{x}\u001f12\u001fmsg\nstash@{4}\u001f100\u001fcustom message\n");

        var only = Assert.Single(stashes);
        Assert.Equal(4, only.Index);
        Assert.Null(only.Branch);
    }

    [Fact]
    public void Reads_the_stash_commit_from_the_current_format()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var stashes = StashListParser.Parse($"stash@{{0}}\u001f100\u001f{sha}\u001fWIP on main: abc work\nstash@{{1}}\u001f90\u001fOn main: odd\u001fmessage\n");

        Assert.Equal(sha, stashes[0].Sha);
        Assert.Equal("WIP on main: abc work", stashes[0].Message);
        Assert.Equal("main", stashes[0].Branch);
        // Not a sha in the third field: an older three-field line whose message happens to hold a separator.
        Assert.Null(stashes[1].Sha);
        Assert.Equal("On main: odd\u001fmessage", stashes[1].Message);
    }
}
