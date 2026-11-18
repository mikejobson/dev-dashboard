using DevDash.Core.Git;

namespace DevDash.Core.Tests.Parsers;

public class ForEachRefParserTests
{
    [Fact]
    public void Parses_every_branch_of_a_real_repo()
    {
        var branches = ForEachRefParser.Parse(Fixture.Read("for-each-ref.core.txt"));

        Assert.Equal(50, branches.Count);

        var current = Assert.Single(branches, b => b.IsCurrent);
        Assert.Equal("develop", current.Name);
        Assert.Equal("origin/develop", current.Upstream);
        Assert.Equal("21dce74aa471963065aeff2f55b890c237860a8b", current.Sha);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 15, 4, 17, TimeSpan.Zero), current.LastCommitDate);
        Assert.StartsWith("Merge pull request #383", current.Subject);

        var diverged = branches.Single(b => b.Name == "docs/device-monitoring-plan");
        Assert.Equal(12, diverged.Ahead);
        Assert.Equal(15, diverged.Behind);
        Assert.False(diverged.UpstreamGone);

        var behindOnly = branches.Single(b => b.Name == "feat/edge-move-back");
        Assert.Equal(0, behindOnly.Ahead);
        Assert.Equal(27, behindOnly.Behind);

        var noUpstream = branches.Single(b => b.Name == "backup/develop-pre-reset-20261006");
        Assert.Null(noUpstream.Upstream);
        Assert.Equal(TimeSpan.FromHours(1), noUpstream.LastCommitDate!.Value.Offset);
    }

    [Fact]
    public void Detects_gone_upstreams()
    {
        var branches = ForEachRefParser.Parse(Fixture.Read("for-each-ref.widget.txt"));

        Assert.Equal(17, branches.Count);
        var gone = Assert.Single(branches, b => b.UpstreamGone);
        Assert.Equal("claude/form-template-date-field-szjess", gone.Name);
        Assert.Equal("origin/claude/form-template-date-field-szjess", gone.Upstream);
        Assert.Equal(0, gone.Ahead);
    }

    [Fact]
    public void Skips_blank_and_malformed_lines()
    {
        var branches = ForEachRefParser.Parse("\n\nnot a ref line\nmain\u001fabc\u001f\u001f\u001f\u001f*\u001f\n");

        var only = Assert.Single(branches);
        Assert.Equal("main", only.Name);
        Assert.True(only.IsCurrent);
        Assert.Null(only.LastCommitDate);
        Assert.Null(only.Subject);
    }

    [Fact]
    public void ParseNames_returns_a_set()
    {
        var names = ForEachRefParser.ParseNames("main\nfeature/a\n\n");

        Assert.Equal(2, names.Count);
        Assert.Contains("feature/a", names);
    }
}
