using DevDash.Core.Git;

namespace DevDash.Core.Tests.Parsers;

public class RemoteUrlNormalizerTests
{
    [Theory]
    [InlineData("https://github.com/Acme-Corp/core.git", "github.com/acme-corp/core")]
    [InlineData("https://github.com/example/Widget-API-V3", "github.com/example/widget-api-v3")]
    [InlineData("https://github.com/owner/repo/", "github.com/owner/repo")]
    [InlineData("git@github.com:Owner/Repo.git", "github.com/owner/repo")]
    [InlineData("ssh://git@github.com/owner/repo.git", "github.com/owner/repo")]
    [InlineData("ssh://git@github.com:22/owner/repo", "github.com/owner/repo")]
    [InlineData("ssh://git@ssh.github.com:443/owner/repo.git", "github.com/owner/repo")]
    [InlineData("git://github.com/owner/repo.git", "github.com/owner/repo")]
    [InlineData("https://user:ghp_secret@github.com/owner/repo.git", "github.com/owner/repo")]
    [InlineData("https://x-access-token@GitHub.com/owner/repo", "github.com/owner/repo")]
    [InlineData("https://gitlab.com/group/sub/project.git", "gitlab.com/group/sub/project")]
    [InlineData("git@gitlab.example.com:group/sub/project.git", "gitlab.example.com/group/sub/project")]
    [InlineData("https://dev.azure.com/org/project/_git/repo", "dev.azure.com/org/project/repo")]
    [InlineData("git@ssh.dev.azure.com:v3/org/project/repo", "dev.azure.com/org/project/repo")]
    [InlineData("  https://github.com/owner/repo.git  ", "github.com/owner/repo")]
    public void Normalizes_hosted_remotes(string url, string expected) =>
        Assert.Equal(expected, RemoteUrlNormalizer.Normalize(url)?.Key);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/Users/dev/repos/thing.git")]
    [InlineData("../sibling")]
    [InlineData("file:///srv/git/thing.git")]
    [InlineData("C:\\repos\\thing")]
    [InlineData("C:/repos/thing")]
    [InlineData("https://github.com/just-an-owner")]
    public void Returns_null_for_local_or_incomplete_remotes(string? url) =>
        Assert.Null(RemoteUrlNormalizer.Normalize(url));

    [Fact]
    public void Keeps_the_original_case_for_display()
    {
        var identity = RemoteUrlNormalizer.Normalize("git@github.com:Acme-Corp/Core.git");

        Assert.Equal("github.com", identity!.Host);
        Assert.Equal("Acme-Corp/Core", identity.Path);
        Assert.Equal("github.com/Acme-Corp/Core", identity.ToString());
    }

    [Theory]
    [InlineData("https://user:ghp_secret@github.com/owner/repo.git", "https://github.com/owner/repo.git")]
    [InlineData("https://token@github.com/owner/repo.git", "https://github.com/owner/repo.git")]
    [InlineData("https://github.com/owner/repo.git", "https://github.com/owner/repo.git")]
    [InlineData("ssh://git@github.com/owner/repo.git", "ssh://git@github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git", "git@github.com:owner/repo.git")]
    public void Strips_credentials_from_urls(string url, string expected) =>
        Assert.Equal(expected, RemoteUrlNormalizer.StripCredentials(url));

    [Fact]
    public void Remote_list_keeps_fetch_urls_without_credentials()
    {
        const string output = "origin\thttps://tok:secret@github.com/Acme-Corp/Gadget.git (fetch)\n"
            + "origin\thttps://tok:secret@github.com/Acme-Corp/Gadget.git (push)\n"
            + "backup\t/Volumes/usb/gadget.git (fetch)\n"
            + "backup\t/Volumes/usb/gadget.git (push)\n";

        var remotes = RemoteListParser.Parse(output);

        Assert.Equal(2, remotes.Count);
        Assert.Equal("origin", remotes[0].Name);
        Assert.Equal("https://github.com/Acme-Corp/Gadget.git", remotes[0].Url);
        Assert.Equal("github.com/acme-corp/gadget", remotes[0].Normalized);
        Assert.Equal("backup", remotes[1].Name);
        Assert.Null(remotes[1].Normalized);
    }

    [Fact]
    public void Remote_list_reads_partial_clones_with_a_filter_after_the_fetch_marker()
    {
        const string output = "origin\thttps://github.com/Acme-Corp/Gadget.git (fetch) [blob:none]\n"
            + "origin\thttps://github.com/Acme-Corp/Gadget.git (push)\n"
            + "shallow\tgit@github.com:acme-corp/widget.git (fetch) [tree:0]\n";

        var remotes = RemoteListParser.Parse(output);

        Assert.Equal(2, remotes.Count);
        Assert.Equal("origin", remotes[0].Name);
        Assert.Equal("https://github.com/Acme-Corp/Gadget.git", remotes[0].Url);
        Assert.Equal("github.com/acme-corp/gadget", remotes[0].Normalized);
        Assert.Equal("shallow", remotes[1].Name);
        Assert.Equal("git@github.com:acme-corp/widget.git", remotes[1].Url);
    }
}
