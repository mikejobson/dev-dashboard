using System.Diagnostics;
using DevDash.Agent.Scanning;
using DevDash.Agent.Toolchains;

namespace DevDash.Core.Tests.Agent;

/// <summary>
/// The scanner reads README, manifests and pin files out of repositories, which are other people's
/// content: a link to a secret must not be followed, and a FIFO must not hang the scan.
/// </summary>
public sealed class RepoFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("devdash-repofile-").FullName;
    private readonly string _repo;
    private readonly string _secret;

    public RepoFileTests()
    {
        _repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        _secret = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "home", ".ssh")).FullName, "id_ed25519");
        File.WriteAllText(_secret, "-----BEGIN OPENSSH PRIVATE KEY-----\nsecret key material\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private static bool CanLink => !OperatingSystem.IsWindows(); // symbolic links need developer mode there

    [Fact]
    public void Reads_a_plain_file_inside_the_repo_up_to_the_cap()
    {
        var file = Path.Combine(_repo, "README.md");
        File.WriteAllText(file, "﻿hello world");

        Assert.Equal("hello world", RepoFile.ReadText(_repo, file, 1024));
        Assert.Equal("hello", RepoFile.ReadText(_repo, file, 8)![..5]);
        Assert.Null(RepoFile.ReadAll(_repo, file, 8)); // too big to read whole
        Assert.Null(RepoFile.ReadText(_repo, Path.Combine(_repo, "missing.md"), 1024));
        Assert.Null(RepoFile.ReadText(_repo, _secret, 1024)); // not inside the repo
        Assert.Null(RepoFile.ReadText(_repo, Path.Combine(_repo, "..", "home", ".ssh", "id_ed25519"), 1024));
    }

    [Fact]
    public void A_readme_that_links_to_a_secret_is_not_read()
    {
        if (!CanLink)
        {
            return;
        }

        File.CreateSymbolicLink(Path.Combine(_repo, "README.md"), _secret);
        Directory.CreateDirectory(Path.Combine(_repo, "app"));
        File.CreateSymbolicLink(Path.Combine(_repo, "app", "package.json"), _secret);
        File.CreateSymbolicLink(Path.Combine(_repo, ".nvmrc"), _secret);

        var description = DescriptionReader.Read(_repo);

        Assert.Null(description?.ReadmeExcerpt);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(description));
        Assert.Null(PinReader.Read(_repo));
        Assert.Null(RepoFile.ReadText(_repo, Path.Combine(_repo, "README.md"), 1024));
    }

    [Fact]
    public void A_file_reached_through_a_linked_folder_is_not_read()
    {
        if (!CanLink)
        {
            return;
        }

        Directory.CreateSymbolicLink(Path.Combine(_repo, "src"), Path.GetDirectoryName(_secret)!);

        Assert.False(RepoFile.IsSafe(_repo, Path.Combine(_repo, "src", "id_ed25519")));
        Assert.Null(RepoFile.ReadText(_repo, Path.Combine(_repo, "src", "id_ed25519"), 1024));
    }

    [Fact]
    public async Task A_fifo_named_like_a_readme_does_not_block_the_scan()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // no FIFOs in the file system
        }

        foreach (var name in (string[])["README.md", "package.json", ".nvmrc"])
        {
            using var mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { Path.Combine(_repo, name) } })!;
            await mkfifo.WaitForExitAsync();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        var read = Task.Run(() => (DescriptionReader.Read(_repo), PinReader.Read(_repo)));
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(read, finished);
        Assert.Null((await read).Item1?.ReadmeExcerpt);
        Assert.False(RepoFile.IsSafe(_repo, Path.Combine(_repo, "README.md")));
    }
}
