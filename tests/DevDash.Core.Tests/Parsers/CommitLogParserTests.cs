using DevDash.Core.Git;

namespace DevDash.Core.Tests.Parsers;

public class CommitLogParserTests
{
    private static readonly string A = new('a', 40);
    private static readonly string B = new('b', 40);
    private static readonly string C = new('c', 40);

    private static string Line(string sha, string parents, string date, string author, string subject) =>
        string.Join(CommitLogParser.Separator, sha, parents, date, author, subject);

    [Fact]
    public void Parses_commits_with_none_one_and_two_parents()
    {
        var output = string.Join('\n',
            Line(C, $"{B} {A}", "2026-11-18T10:00:00+01:00", "Sam Rivera", "Merge branch 'topic'"),
            Line(B, A, "2026-11-17T09:30:00Z", "Alex Kim", "Add the widget"),
            Line(A, "", "2026-11-16T08:00:00Z", "Alex Kim", "Initial commit"),
            "");

        var commits = CommitLogParser.Parse(output);

        Assert.Equal([C, B, A], commits.Select(c => c.Sha));
        Assert.Equal([B, A], commits[0].Parents);
        Assert.Equal([A], commits[1].Parents);
        Assert.Empty(commits[2].Parents);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.FromHours(1)), commits[0].Date);
        Assert.Equal("Sam Rivera", commits[0].Author);
        Assert.Equal("Add the widget", commits[1].Subject);
        Assert.Null(commits[0].TrunkPos);
        Assert.False(commits[0].Unpushed);
    }

    [Fact]
    public void Keeps_separator_characters_in_a_subject_and_reads_sha256_ids()
    {
        var sha256 = new string('d', 64);
        var commits = CommitLogParser.Parse(Line(sha256, "", "2026-11-16T08:00:00Z", "Sam", "one\u001ftwo") + "\r\n");

        var commit = Assert.Single(commits);
        Assert.Equal(sha256, commit.Sha);
        Assert.Equal("one\u001ftwo", commit.Subject);
    }

    [Fact]
    public void Cuts_long_subjects_and_authors()
    {
        var commits = CommitLogParser.Parse(Line(A, "", "2026-11-16T08:00:00Z", new string('n', 80), new string('s', 300)));

        var commit = Assert.Single(commits);
        Assert.Equal(CommitLogParser.MaxSubjectLength, commit.Subject.Length);
        Assert.EndsWith("…", commit.Subject);
        Assert.Equal(CommitLogParser.MaxAuthorLength, commit.Author.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fatal: bad revision 'nope'")]
    [InlineData("not-a-sha\u001f\u001f2026-11-16T08:00:00Z\u001fSam\u001fsubject")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\u001f\u001f2026-11-16T08:00:00Z")]
    public void Skips_lines_that_are_not_commits(string output) =>
        Assert.Empty(CommitLogParser.Parse(output));

    [Fact]
    public void Reads_plain_sha_lists()
    {
        Assert.Equal([A, B], CommitLogParser.ParseShas($"{A}\n{B}\nwarning: something\n\n"));
        Assert.False(CommitLogParser.IsSha(new string('A', 40)));
        Assert.False(CommitLogParser.IsSha("abc"));
    }
}
