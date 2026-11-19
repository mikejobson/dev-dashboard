using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevDash.Agent;
using DevDash.Agent.Actions;
using DevDash.Agent.Git;
using DevDash.Agent.Scanning;
using DevDash.Agent.Service;
using DevDash.Agent.Updates;
using DevDash.Contracts;
using DevDash.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevDash.Core.Tests.Agent;

/// <summary>
/// The agent's self-update, against a pretend release site and installations in temporary folders.
/// Nothing here downloads anything real, replaces a real program or touches a service.
/// </summary>
public sealed class UpdateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("devdash-update-").FullName;
    private readonly FakeRelease _site = new();

    private string Dir(string name = "bin") => Path.Combine(_root, name);

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

    // ---- versions -------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("10.20.30")]
    [InlineData("1.0.0-rc.1")]
    [InlineData("1.0.0-alpha-2.x")]
    public void Strict_versions_parse_and_print_back_unchanged(string text)
    {
        Assert.True(SemVer.TryParse(text, out var version));
        Assert.Equal(text, version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-rc..1")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3+build")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3\n")]
    [InlineData("1.2.3/../../x")]
    [InlineData("1.2.3; rm -rf ~")]
    [InlineData("latest")]
    [InlineData("99999999999.0.0")]
    public void Anything_else_is_not_a_version(string text) => Assert.False(SemVer.IsValid(text));

    [Fact]
    public void Versions_order_by_semver_precedence()
    {
        string[] ascending = ["0.1.0", "0.1.1", "0.2.0", "0.10.0", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "2.0.0"];
        var parsed = ascending.Select(v => SemVer.ParseOrNull(v)!).ToList();
        for (var i = 0; i < parsed.Count - 1; i++)
        {
            Assert.True(parsed[i] < parsed[i + 1], $"{parsed[i]} should sort before {parsed[i + 1]}");
            Assert.True(parsed[i + 1] > parsed[i]);
        }

        Assert.Equal(SemVer.ParseOrNull("1.2.3"), SemVer.ParseOrNull("1.2.3"));
        Assert.Equal(ascending, parsed.OrderByDescending(v => v).Reverse().Select(v => v.ToString()));
    }

    // ---- signature and checksums ----------------------------------------------------------------

    private static (byte[] Checksums, byte[] Signature) Signed(ECDsa key, string text = "abc  devdash-agent-linux-x64.tar.gz\n")
    {
        var data = Encoding.ASCII.GetBytes(text);
        return (data, key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public void A_signature_from_the_trusted_key_is_accepted()
    {
        var (checksums, signature) = Signed(_site.Key);
        Assert.Null(ReleaseVerifier.SignatureProblem(checksums, signature, _site.Trust));
    }

    [Fact]
    public void Changed_checksums_a_changed_signature_another_key_and_no_signature_are_all_refused()
    {
        var (checksums, signature) = Signed(_site.Key);

        var edited = (byte[])checksums.Clone();
        edited[0] ^= 1;
        Assert.Contains("not valid", ReleaseVerifier.SignatureProblem(edited, signature, _site.Trust));

        var flipped = (byte[])signature.Clone();
        flipped[^1] ^= 1;
        Assert.Contains("not valid", ReleaseVerifier.SignatureProblem(checksums, flipped, _site.Trust));
        Assert.Contains("not valid", ReleaseVerifier.SignatureProblem(checksums, "not a signature"u8, _site.Trust));

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Contains("not valid", ReleaseVerifier.SignatureProblem(checksums, Signed(stranger).Signature, _site.Trust));

        Assert.Contains("no signature", ReleaseVerifier.SignatureProblem(checksums, [], _site.Trust));
    }

    [Fact]
    public void The_placeholder_key_in_source_verifies_nothing()
    {
        // Until a real key is generated, a release build must refuse every update rather than accept one.
        var placeholder = new ReleaseTrust(ReleaseSigning.PublicKeyPem);
        if (placeholder.IsConfigured)
        {
            return; // the real key has been pasted in; nothing to check here
        }

        var (checksums, signature) = Signed(_site.Key);
        Assert.Contains("no release signing key", ReleaseVerifier.SignatureProblem(checksums, signature, placeholder));
        Assert.Contains("not set", placeholder.Describe());
    }

    [Fact]
    public void Only_a_p256_public_key_counts_as_a_release_key()
    {
        Assert.True(new ReleaseTrust(_site.Key.ExportSubjectPublicKeyInfoPem()).IsConfigured);
        Assert.False(new ReleaseTrust("").IsConfigured);
        Assert.False(new ReleaseTrust("-----BEGIN PUBLIC KEY-----\nnonsense\n-----END PUBLIC KEY-----").IsConfigured);
        Assert.False(new ReleaseTrust(_site.Key.ExportECPrivateKeyPem()).IsConfigured);   // never a private key
        Assert.False(new ReleaseTrust(_site.Key.ExportPkcs8PrivateKeyPem()).IsConfigured);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.False(new ReleaseTrust(p384.ExportSubjectPublicKeyInfoPem()).IsConfigured);
        using var rsa = RSA.Create(2048);
        Assert.False(new ReleaseTrust(rsa.ExportSubjectPublicKeyInfoPem()).IsConfigured);
    }

    [Fact]
    public void A_build_only_trusts_a_test_key_when_it_was_compiled_in()
    {
        // The test project builds the agent normally, so the embedded trust is the constant from source.
        Assert.False(ReleaseSigning.Embedded.IsTestKey);
        Assert.Equal(ReleaseSigning.PublicKeyPem, ReleaseSigning.Embedded.PublicKeyPem);
        Assert.DoesNotContain(typeof(UpdateOptions).GetProperties(), p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Checksums_parse_like_sha256sum_output()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var parsed = ReleaseVerifier.ParseChecksums($"{a}  devdash-agent-osx-arm64.tar.gz\r\n{b} *devdash-agent-win-x64.zip\n\nnot a line\n{a}  ../evil\n{a}  twice\n{b}  twice\n");

        Assert.Equal(a, parsed["devdash-agent-osx-arm64.tar.gz"]);
        Assert.Equal(b.ToLowerInvariant(), parsed["devdash-agent-win-x64.zip"]);
        Assert.Equal(2, parsed.Count); // the path and the file listed with two different hashes are dropped

        Assert.Null(ReleaseVerifier.ChecksumProblem(parsed, "devdash-agent-osx-arm64.tar.gz", a.ToUpperInvariant()));
        Assert.Contains("mismatch", ReleaseVerifier.ChecksumProblem(parsed, "devdash-agent-osx-arm64.tar.gz", b));
        Assert.Contains("no entry", ReleaseVerifier.ChecksumProblem(parsed, "devdash-agent-linux-x64.tar.gz", a));
    }

    // ---- the updater ----------------------------------------------------------------------------

    private static void AssertUntouched(UpdateInstallation installation, string version)
    {
        foreach (var name in installation.FileNames)
        {
            Assert.Equal(version, FakeRelease.VersionAt(installation.PathOf(name)));
            Assert.False(File.Exists(UpdateInstallation.BackupPath(installation.PathOf(name))), "no .old should be left behind");
        }

        Assert.Null(new UpdateStateStore(installation.StateFile).Read().Pending);
        Assert.False(Directory.Exists(installation.StagingDirectory), "the download folder should be cleared");
    }

    [Theory]
    [InlineData(ServicePlatform.Linux)]
    [InlineData(ServicePlatform.MacOS)]
    [InlineData(ServicePlatform.Windows)]
    public async Task Update_installs_the_latest_release_keeps_the_previous_one_and_waits_to_be_confirmed(ServicePlatform platform)
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0", platform);
        _site.Publish("1.1.0", installation, latest: false);
        _site.Publish("1.2.0", installation);
        var probe = new FakeProbe();

        var result = await _site.Updater(installation, "1.0.0", probe: probe).RunAsync(new UpdateRequest(), CancellationToken.None);

        Assert.Equal(UpdateStatus.Installed, result.Status);
        Assert.Equal("1.2.0", result.TargetVersion);
        Assert.Equal(installation.FileNames, probe.Ran); // every new program was run before anything was replaced
        foreach (var name in installation.FileNames)
        {
            Assert.Equal("1.2.0", FakeRelease.VersionAt(installation.PathOf(name)));
            Assert.Equal("1.0.0", FakeRelease.VersionAt(UpdateInstallation.BackupPath(installation.PathOf(name))));
        }

        var pending = new UpdateStateStore(installation.StateFile).Read().Pending;
        Assert.NotNull(pending);
        Assert.Equal(("1.0.0", "1.2.0", 0), (pending.FromVersion, pending.ToVersion, pending.Starts));
        Assert.Equal(installation.FileNames.Count, pending.Files.Count);
        Assert.False(Directory.Exists(installation.StagingDirectory));
        Assert.False(File.Exists(Path.Combine(_root, "escape.sh")));
        Assert.All(_site.Requests, url => Assert.StartsWith(FakeRelease.BaseUrl + "/", url));
        if (!OperatingSystem.IsWindows() && platform != ServicePlatform.Windows)
        {
            Assert.True(File.GetUnixFileMode(installation.ConsoleBinary).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public async Task A_named_version_is_installed_from_that_versions_own_folder()
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        _site.Publish("1.1.0", installation, latest: false);
        _site.Publish("1.2.0", installation);

        var result = await _site.Updater(installation, "1.0.0").RunAsync(new UpdateRequest("1.1.0"), CancellationToken.None);

        Assert.Equal(UpdateStatus.Installed, result.Status);
        Assert.Equal("1.1.0", FakeRelease.VersionAt(installation.ConsoleBinary));
        Assert.All(_site.Requests, url => Assert.Contains("/download/v1.1.0/", url));
    }

    [Fact]
    public async Task The_same_version_is_a_no_op_and_an_older_one_is_refused_without_downloading_anything()
    {
        var installation = FakeRelease.Install(Dir(), "1.2.0");
        _site.Publish("1.1.0", installation, latest: false);
        _site.Publish("1.2.0", installation);
        var updater = _site.Updater(installation, "1.2.0");

        var same = await updater.RunAsync(new UpdateRequest("1.2.0"), CancellationToken.None);
        var older = await updater.RunAsync(new UpdateRequest("1.1.0"), CancellationToken.None);
        var prerelease = await updater.RunAsync(new UpdateRequest("1.2.0-rc.1"), CancellationToken.None);
        Assert.Empty(_site.Requests);
        var latest = await updater.RunAsync(new UpdateRequest(), CancellationToken.None);

        Assert.Equal(UpdateStatus.UpToDate, same.Status);
        Assert.Equal(UpdateStatus.Refused, older.Status);
        Assert.Contains("does not downgrade", older.Message);
        Assert.Equal(UpdateStatus.Refused, prerelease.Status); // 1.2.0-rc.1 sorts before 1.2.0
        Assert.Equal(UpdateStatus.UpToDate, latest.Status);
        AssertUntouched(installation, "1.2.0");
    }

    [Fact]
    public async Task Check_only_reports_and_changes_nothing()
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        _site.Publish("1.2.0", installation);

        var result = await _site.Updater(installation, "1.0.0").RunAsync(new UpdateRequest(CheckOnly: true), CancellationToken.None);

        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal("1.2.0", result.TargetVersion);
        Assert.DoesNotContain(_site.Requests, url => url.EndsWith(installation.AssetName, StringComparison.Ordinal));
        AssertUntouched(installation, "1.0.0");
    }

    public static TheoryData<string, string> Attacks => new()
    {
        { "asset", "checksum mismatch" },
        { "checksums", "signature on checksums.txt is not valid" },
        { "signature", "signature on checksums.txt is not valid" },
        { "no-signature", "no signature" },
        { "other-key", "signature on checksums.txt is not valid" },
        { "version", "checksum mismatch for version.txt" },
        { "replay", "the signed files at that address are for 1.1.0" },
        { "missing-program", "does not contain devdash-agent" },
    };

    [Theory]
    [MemberData(nameof(Attacks))]
    public async Task A_release_that_does_not_verify_is_refused_and_nothing_changes(string attack, string expected)
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        switch (attack)
        {
            case "asset":
                _site.Publish("1.2.0", installation, tamper: files => files[installation.AssetName] = FakeRelease.Archive(installation, _ => FakeRelease.Program("6.6.6")));
                break;
            case "checksums":
                // The attacker swaps the program and rewrites checksums.txt to match, but cannot re-sign it.
                _site.Publish("1.2.0", installation, tamper: files =>
                {
                    files[installation.AssetName] = FakeRelease.Archive(installation, _ => FakeRelease.Program("6.6.6"));
                    files[AgentUpdater.ChecksumsFile] = Encoding.ASCII.GetBytes(
                        $"{ReleaseVerifier.Sha256Hex(files[installation.AssetName])}  {installation.AssetName}\n{ReleaseVerifier.Sha256Hex(files[AgentUpdater.VersionFile])}  {AgentUpdater.VersionFile}\n");
                });
                break;
            case "signature":
                _site.Publish("1.2.0", installation, tamper: files => files[AgentUpdater.SignatureFile][8] ^= 0x40);
                break;
            case "no-signature":
                _site.Publish("1.2.0", installation, tamper: files => files.Remove(AgentUpdater.SignatureFile));
                break;
            case "other-key":
                _site.Publish("1.2.0", installation, signWith: stranger);
                break;
            case "version":
                _site.Publish("1.2.0", installation, tamper: files => files[AgentUpdater.VersionFile] = "9.9.9\n"u8.ToArray());
                break;
            case "replay":
                // A genuine, correctly signed older release, offered at the address of a newer version.
                _site.Publish("1.1.0", installation, servedAs: "1.2.0", latest: false);
                break;
            default:
                _site.Publish("1.2.0", installation, tamper: files =>
                {
                    // An archive that verifies (it is signed by the right key) but does not hold the program.
                    using var buffer = new MemoryStream();
                    using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                    using (var tar = new System.Formats.Tar.TarWriter(gzip))
                    {
                        tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "bin/devdash-agent") { DataStream = new MemoryStream(FakeRelease.Program("1.2.0")) });
                        tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.SymbolicLink, "devdash-agent") { LinkName = "/bin/sh" });
                    }

                    files[installation.AssetName] = buffer.ToArray();
                    files[AgentUpdater.ChecksumsFile] = Encoding.ASCII.GetBytes(
                        $"{ReleaseVerifier.Sha256Hex(files[installation.AssetName])}  {installation.AssetName}\n{ReleaseVerifier.Sha256Hex(files[AgentUpdater.VersionFile])}  {AgentUpdater.VersionFile}\n");
                    files[AgentUpdater.SignatureFile] = _site.Key.SignData(files[AgentUpdater.ChecksumsFile], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
                });
                break;
        }

        var probe = new FakeProbe();
        var result = await _site.Updater(installation, "1.0.0", probe: probe).RunAsync(new UpdateRequest("1.2.0"), CancellationToken.None);

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains(expected, result.Message);
        Assert.Empty(probe.Ran); // an unverified download is never run
        AssertUntouched(installation, "1.0.0");
    }

    [Fact]
    public async Task A_release_signed_by_the_real_key_is_refused_by_a_build_with_no_key()
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        _site.Publish("1.2.0", installation);

        var result = await _site.Updater(installation, "1.0.0", trust: new ReleaseTrust("-----BEGIN PUBLIC KEY-----\nPLACEHOLDER\n-----END PUBLIC KEY-----"))
            .RunAsync(new UpdateRequest(), CancellationToken.None);

        Assert.Equal(UpdateStatus.Refused, result.Status);
        Assert.Contains("no release signing key", result.Message);
        Assert.Empty(_site.Requests);
        AssertUntouched(installation, "1.0.0");
    }

    [Theory]
    [InlineData("broken")]      // crashes on start
    [InlineData("fake devdash-agent 1.1.9")] // runs, but is not the version the release says it is
    public async Task A_new_program_that_does_not_run_or_is_the_wrong_version_is_never_swapped_in(string content)
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        _site.Publish("1.2.0", installation, program: _ => Encoding.UTF8.GetBytes(content + "\n"));

        var result = await _site.Updater(installation, "1.0.0").RunAsync(new UpdateRequest(), CancellationToken.None);

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("did not run correctly here", result.Message);
        Assert.Contains("Nothing was changed", result.Message);
        AssertUntouched(installation, "1.0.0");
    }

    [Fact]
    public async Task Releases_are_only_fetched_over_https_or_from_this_machine()
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        _site.Publish("1.2.0", installation);
        var options = new AgentOptions { Updates = new UpdateOptions { BaseUrl = "http://releases.test/acme/releases" } };

        var result = await _site.Updater(installation, "1.0.0", options).RunAsync(new UpdateRequest(), CancellationToken.None);

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("not an https URL", result.Message);
        Assert.Empty(_site.Requests);

        Assert.Null(AgentUpdater.UrlProblem(new Uri("http://127.0.0.1:8123/x")));
        Assert.Null(AgentUpdater.UrlProblem(new Uri("http://localhost:8123/x")));
        Assert.NotNull(AgentUpdater.UrlProblem(new Uri("ftp://example.com/x")));
        Assert.Equal("https://github.com/mikejobson/dev-dashboard/releases/download/v0.2.0/checksums.txt",
            AgentUpdater.ReleaseUrl(new UpdateOptions(), SemVer.ParseOrNull("0.2.0"), "checksums.txt").ToString());
        Assert.Equal("https://github.com/someone/fork/releases/latest/download/version.txt",
            AgentUpdater.ReleaseUrl(new UpdateOptions { Repository = "someone/fork" }, null, "version.txt").ToString());
    }

    [Fact]
    public async Task A_development_build_cannot_update_itself()
    {
        var (installation, problem) = UpdateInstallation.For("/usr/local/share/dotnet/dotnet", ServicePlatform.MacOS, "osx-arm64");
        Assert.Null(installation);
        Assert.Contains("development build", problem);

        var updater = new AgentUpdater(new HttpClient(_site), new TestOptions(new AgentOptions { Updates = new UpdateOptions { BaseUrl = FakeRelease.BaseUrl } }),
            new UpdateContext(null, problem, "1.0.0", _site.Trust), new FakeProbe(), new UpdateFiles(), TimeProvider.System, NullLogger<AgentUpdater>.Instance);
        var result = await updater.RunAsync(new UpdateRequest("1.2.0"), CancellationToken.None);

        Assert.Equal(UpdateStatus.Refused, result.Status);
        Assert.Contains("development build", result.Message);
        Assert.Empty(_site.Requests);
    }

    [Fact]
    public void The_installation_is_the_folder_of_the_running_program()
    {
        var (unix, _) = UpdateInstallation.For("/home/sam/.local/bin/devdash-agent", ServicePlatform.Linux, "linux-arm64");
        Assert.Equal(["devdash-agent"], unix!.FileNames);
        Assert.Equal("devdash-agent-linux-arm64.tar.gz", unix.AssetName);

        // The scheduled task runs the windowless build; both files belong to the same installation.
        var (windows, _) = UpdateInstallation.For(Path.Combine(Dir(), "DEVDASH-AGENTW.EXE"), ServicePlatform.Windows, "win-x64");
        Assert.Equal(["devdash-agent.exe", "devdash-agentw.exe"], windows!.FileNames);
        Assert.Equal("devdash-agent-win-x64.zip", windows.AssetName);
        Assert.Equal(Dir(), windows.Directory);

        Assert.Contains("not a standard install", UpdateInstallation.For("/opt/tools/my-agent", ServicePlatform.Linux, "linux-x64").Problem);
        Assert.Contains("no release builds", UpdateInstallation.For("/opt/tools/devdash-agent", ServicePlatform.Linux, null).Problem);
    }

    [Fact]
    public async Task An_update_that_is_still_unconfirmed_blocks_the_next_one_unless_a_person_asks()
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        _site.Publish("1.1.0", installation, latest: false);
        _site.Publish("1.2.0", installation);
        Assert.True((await _site.Updater(installation, "1.0.0").RunAsync(new UpdateRequest("1.1.0"), CancellationToken.None)).Installed);

        // Now running as 1.1.0, which has not reported to the hub yet.
        var fromDashboard = await _site.Updater(installation, "1.1.0").RunAsync(new UpdateRequest("1.2.0"), CancellationToken.None);
        Assert.Equal(UpdateStatus.Refused, fromDashboard.Status);
        Assert.Contains("has not been confirmed yet", fromDashboard.Message);
        Assert.Equal("1.1.0", FakeRelease.VersionAt(installation.ConsoleBinary));

        var byHand = await _site.Updater(installation, "1.1.0").RunAsync(new UpdateRequest("1.2.0", ReplacePending: true), CancellationToken.None);
        Assert.True(byHand.Installed);
        Assert.Equal("1.2.0", FakeRelease.VersionAt(installation.ConsoleBinary));
        Assert.Equal("1.1.0", FakeRelease.VersionAt(UpdateInstallation.BackupPath(installation.ConsoleBinary)));
    }

    // ---- swap planning --------------------------------------------------------------------------

    [Fact]
    public void On_unix_the_current_file_is_copied_aside_and_the_new_one_renamed_over_it()
    {
        var installation = new UpdateInstallation { Platform = ServicePlatform.MacOS, Directory = "/home/sam/.local/bin", Rid = "osx-arm64" };
        var plan = SwapPlanner.Install(installation, "/stage", _ => true);

        var file = Assert.Single(plan.Files);
        var current = Path.Combine("/home/sam/.local/bin", "devdash-agent");
        Assert.Equal(new InstalledFile(current, current + ".old"), file.File);
        Assert.Equal(
            [
                new SwapStep(SwapOp.Delete, current + ".old", SkipIfMissing: true),
                new SwapStep(SwapOp.Copy, current, current + ".old"),
                // One atomic rename: the path never holds half a file, and never holds nothing.
                new SwapStep(SwapOp.Move, Path.Combine("/stage", "devdash-agent"), current, Overwrite: true),
            ],
            file.Steps);
        Assert.Equal(1, file.BackupStep);

        Assert.Equal([new SwapStep(SwapOp.Move, current + ".old", current, Overwrite: true)], SwapPlanner.Rollback(ServicePlatform.MacOS, [file.File]));
    }

    [Fact]
    public void On_windows_both_programs_are_renamed_aside_because_a_running_exe_cannot_be_overwritten()
    {
        var installation = new UpdateInstallation { Platform = ServicePlatform.Windows, Directory = "bin", Rid = "win-x64" };
        var plan = SwapPlanner.Install(installation, "stage", _ => true);

        Assert.Equal(["devdash-agent.exe", "devdash-agentw.exe"], plan.Files.Select(f => Path.GetFileName(f.File.Path)));
        foreach (var file in plan.Files)
        {
            var current = file.File.Path;
            Assert.Equal(current + ".old", file.File.BackupPath);
            Assert.Equal(
                [
                    new SwapStep(SwapOp.Delete, current + ".old", SkipIfMissing: true),
                    new SwapStep(SwapOp.Delete, current + ".bad", SkipIfMissing: true, BestEffort: true),
                    new SwapStep(SwapOp.Move, current, current + ".old"),
                    new SwapStep(SwapOp.Move, Path.Combine("stage", Path.GetFileName(current)), current),
                ],
                file.Steps);
            Assert.DoesNotContain(file.Steps, s => s.Op == SwapOp.Copy || (s.Op == SwapOp.Move && s.Overwrite));
        }

        // Rolling back happens inside the new, running program: it is moved aside rather than deleted.
        var rollback = SwapPlanner.Rollback(ServicePlatform.Windows, [plan.Files[0].File]);
        var path = plan.Files[0].File.Path;
        Assert.Equal(
            [
                new SwapStep(SwapOp.Delete, path + ".bad", SkipIfMissing: true, BestEffort: true),
                new SwapStep(SwapOp.Move, path, path + ".bad", Overwrite: true, SkipIfMissing: true),
                new SwapStep(SwapOp.Move, path + ".old", path),
            ],
            rollback);
    }

    [Fact]
    public void A_file_with_no_previous_version_gets_no_backup_and_is_removed_on_rollback()
    {
        var installation = new UpdateInstallation { Platform = ServicePlatform.Windows, Directory = "bin", Rid = "win-x64" };
        var plan = SwapPlanner.Install(installation, "stage", path => !path.EndsWith("devdash-agentw.exe", StringComparison.Ordinal));

        var windowless = plan.Files[1];
        Assert.Null(windowless.File.BackupPath);
        Assert.Equal(-1, windowless.BackupStep);
        Assert.DoesNotContain(windowless.Steps, s => s.To?.EndsWith(".old", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(SwapPlanner.Rollback(ServicePlatform.Windows, [windowless.File]), s => s.From.EndsWith(".old", StringComparison.Ordinal));
    }

    /// <summary>Real file operations, except that one chosen operation fails.</summary>
    private sealed class FailingFiles(Func<string, string, bool> fail) : IUpdateFiles
    {
        private readonly UpdateFiles _real = new();

        public bool Exists(string path) => _real.Exists(path);

        public void Delete(string path) => _real.Delete(path);

        public void Copy(string from, string to) => _real.Copy(from, to);

        public void Move(string from, string to, bool overwrite)
        {
            if (fail(from, to))
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            _real.Move(from, to, overwrite);
        }
    }

    [Theory]
    [InlineData(ServicePlatform.Linux)]
    [InlineData(ServicePlatform.Windows)]
    public async Task If_the_swap_fails_part_way_every_file_is_put_back(ServicePlatform platform)
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0", platform);
        // A leftover from some earlier update: it must not be what gets "restored".
        File.WriteAllBytes(UpdateInstallation.BackupPath(installation.ConsoleBinary), FakeRelease.Program("0.5.0"));
        _site.Publish("1.2.0", installation);

        // The last program file cannot be moved into place (on Windows: the second of the two).
        var last = installation.PathOf(installation.FileNames[^1]);
        var files = new FailingFiles((from, to) => to == last && from.Contains(".devdash-agent-update", StringComparison.Ordinal));
        var result = await _site.Updater(installation, "1.0.0", files: files).RunAsync(new UpdateRequest(), CancellationToken.None);

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("The installed version is unchanged", result.Message);
        Assert.All(installation.FileNames, name => Assert.Equal("1.0.0", FakeRelease.VersionAt(installation.PathOf(name))));
        Assert.Null(new UpdateStateStore(installation.StateFile).Read().Pending);
    }

    // ---- confirm or roll back -------------------------------------------------------------------

    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private async Task<UpdateInstallation> UpdatedAsync(ServicePlatform platform = ServicePlatform.Linux, string from = "1.0.0", string to = "1.2.0")
    {
        var installation = FakeRelease.Install(Dir(), from, platform);
        _site.Publish(to, installation);
        Assert.True((await _site.Updater(installation, from, time: new TestClock(T0)).RunAsync(new UpdateRequest(), CancellationToken.None)).Installed);
        return installation;
    }

    private static UpdateGuard Guard(UpdateInstallation installation, string runningVersion, TestClock clock, UpdateOptions? options = null, IUpdateFiles? files = null) =>
        new(new UpdateStateStore(installation.StateFile), files ?? new UpdateFiles(), clock, options ?? new UpdateOptions(), installation.Platform, runningVersion);

    [Fact]
    public void With_no_update_pending_the_guard_does_nothing()
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        var guard = Guard(installation, "1.0.0", new TestClock(T0));

        Assert.Equal(GuardStart.Nothing, guard.OnStart());
        Assert.False(guard.IsVerifying);
        Assert.Null(guard.Confirm());
        Assert.Null(guard.RollBack("nothing to do"));
        Assert.False(File.Exists(installation.StateFile));
    }

    [Fact]
    public async Task The_first_accepted_snapshot_confirms_the_update_and_removes_the_previous_version()
    {
        var installation = await UpdatedAsync();
        var clock = new TestClock(T0.AddSeconds(40));
        var guard = Guard(installation, "1.2.0", clock);

        Assert.Equal(GuardStart.Verifying, guard.OnStart());
        Assert.True(guard.IsVerifying);
        Assert.Equal(clock.Now.AddMinutes(5), guard.Deadline);
        Assert.Equal(1, guard.State.Pending!.Starts);

        clock.Now = clock.Now.AddSeconds(20);
        var report = guard.Confirm();

        Assert.NotNull(report);
        Assert.Equal((AgentUpdateOutcomes.Succeeded, "1.0.0", "1.2.0", clock.Now), (report.Outcome, report.FromVersion, report.ToVersion, report.At));
        Assert.False(guard.IsVerifying);
        Assert.Null(guard.State.Pending);
        Assert.Equal(report, guard.State.Last);
        Assert.False(File.Exists(UpdateInstallation.BackupPath(installation.ConsoleBinary)));
        Assert.Equal("1.2.0", FakeRelease.VersionAt(installation.ConsoleBinary));
        Assert.Null(guard.Confirm()); // only once
        Assert.Null(guard.RollBack("too late")); // and there is no going back after that
    }

    [Theory]
    [InlineData(ServicePlatform.Linux)]
    [InlineData(ServicePlatform.Windows)]
    public async Task A_version_that_keeps_starting_without_reaching_the_hub_is_rolled_back_on_its_next_start(ServicePlatform platform)
    {
        var installation = await UpdatedAsync(platform);
        var clock = new TestClock(T0);

        // Three starts are allowed (each one a process that crashed, or was restarted, before it reported).
        for (var start = 1; start <= 3; start++)
        {
            clock.Now = clock.Now.AddSeconds(30);
            Assert.Equal(GuardStart.Verifying, Guard(installation, "1.2.0", clock).OnStart());
            Assert.Equal(start, new UpdateStateStore(installation.StateFile).Read().Pending!.Starts);
        }

        clock.Now = clock.Now.AddSeconds(30);
        var fourth = Guard(installation, "1.2.0", clock);
        Assert.Equal(GuardStart.RolledBack, fourth.OnStart());

        Assert.All(installation.FileNames, name => Assert.Equal("1.0.0", FakeRelease.VersionAt(installation.PathOf(name))));
        Assert.All(installation.FileNames, name => Assert.False(File.Exists(UpdateInstallation.BackupPath(installation.PathOf(name)))));
        var state = fourth.State;
        Assert.Null(state.Pending);
        Assert.Equal((AgentUpdateOutcomes.RolledBack, "1.0.0", "1.2.0"), (state.Last!.Outcome, state.Last.FromVersion, state.Last.ToVersion));
        Assert.Equal("Rolled back from 1.2.0 to 1.0.0: it started 3 times without reporting to the hub.", state.Last.Detail);
        Assert.Equal(("1.2.0", clock.Now), (state.FailedVersion, state.FailedAt));

        // The restored version starts next: nothing is pending, and it carries the report for the hub.
        var restored = Guard(installation, "1.0.0", clock);
        Assert.Equal(GuardStart.Nothing, restored.OnStart());
        Assert.Equal(AgentUpdateOutcomes.RolledBack, restored.State.Last!.Outcome);

        if (platform == ServicePlatform.Windows)
        {
            // The new program was running while it was rolled back, so it was moved aside; the next start clears it.
            Assert.Empty(Directory.EnumerateFiles(installation.Directory, "*.bad"));
        }
    }

    [Fact]
    public async Task On_windows_the_rolled_back_program_is_moved_aside_and_deleted_later()
    {
        var installation = await UpdatedAsync(ServicePlatform.Windows);
        var guard = Guard(installation, "1.2.0", new TestClock(T0));
        Assert.Equal(GuardStart.Verifying, guard.OnStart());

        Assert.NotNull(guard.RollBack("it ran without reporting to the hub in time"));

        var parked = Directory.EnumerateFiles(installation.Directory, "*.bad").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["devdash-agent.exe.bad", "devdash-agentw.exe.bad"], parked);
        Assert.All(parked, name => Assert.Equal("1.2.0", FakeRelease.VersionAt(Path.Combine(installation.Directory, name!))));
        Assert.All(installation.FileNames, name => Assert.Equal("1.0.0", FakeRelease.VersionAt(installation.PathOf(name))));

        Guard(installation, "1.0.0", new TestClock(T0)).OnStart();
        Assert.Empty(Directory.EnumerateFiles(installation.Directory, "*.bad"));
    }

    [Fact]
    public async Task A_version_that_runs_but_never_reaches_the_hub_is_rolled_back_when_its_time_is_up()
    {
        var installation = await UpdatedAsync();
        var clock = new TestClock(T0);
        var guard = Guard(installation, "1.2.0", clock, new UpdateOptions { VerifyMinutes = 2 });
        Assert.Equal(GuardStart.Verifying, guard.OnStart());
        Assert.Equal(T0.AddMinutes(2), guard.Deadline);

        // What UpdateWatchdog does at the deadline.
        clock.Now = T0.AddMinutes(2);
        var report = guard.RollBack("it ran without reporting to the hub in time");

        Assert.NotNull(report);
        Assert.Equal("Rolled back from 1.2.0 to 1.0.0: it ran without reporting to the hub in time.", report.Detail);
        Assert.Equal("1.0.0", FakeRelease.VersionAt(installation.ConsoleBinary));
        Assert.False(guard.IsVerifying);
        Assert.Null(guard.Confirm()); // a snapshot that lands after the roll back confirms nothing

        // Auto mode leaves the version that failed alone for a day, then may try again.
        var state = guard.State;
        Assert.True(AutoUpdateWorker.RecentlyFailed(state, "1.2.0", clock.Now.AddHours(23)));
        Assert.False(AutoUpdateWorker.RecentlyFailed(state, "1.2.0", clock.Now.AddHours(25)));
        Assert.False(AutoUpdateWorker.RecentlyFailed(state, "1.3.0", clock.Now));
    }

    [Fact]
    public async Task The_watchdog_rolls_back_at_the_deadline_and_asks_for_a_restart()
    {
        var installation = await UpdatedAsync();
        var guard = Guard(installation, "1.2.0", new TestClock(T0), new UpdateOptions { VerifyMinutes = 0.0005 });
        Assert.Equal(GuardStart.Verifying, guard.OnStart());
        var lifetime = new FakeLifetime();
        var restart = new RestartSignal(lifetime, new UpdateContext(installation, null, "1.2.0", _site.Trust), NullLogger<RestartSignal>.Instance);
        using var watchdog = new UpdateWatchdog(guard, restart, TimeProvider.System, NullLogger<UpdateWatchdog>.Instance);

        await watchdog.StartAsync(CancellationToken.None);
        await watchdog.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(restart.Requested);
        Assert.Equal(1, lifetime.Stops);
        Assert.Equal("1.0.0", FakeRelease.VersionAt(installation.ConsoleBinary));
        Assert.Equal(AgentUpdateOutcomes.RolledBack, guard.State.Last!.Outcome);
        Assert.Equal(75, RestartSignal.ExitCode);
    }

    [Fact]
    public async Task The_watchdog_leaves_a_confirmed_update_alone()
    {
        var installation = await UpdatedAsync();
        // 3 s of real time: long enough that a slow CI runner confirms before the deadline (0.002 min
        // raced on ubuntu-latest), short enough that the test still waits out the watchdog quickly.
        var guard = Guard(installation, "1.2.0", new TestClock(T0), new UpdateOptions { VerifyMinutes = 0.05 });
        guard.OnStart();
        var lifetime = new FakeLifetime();
        var restart = new RestartSignal(lifetime, new UpdateContext(installation, null, "1.2.0", _site.Trust), NullLogger<RestartSignal>.Instance);
        using var watchdog = new UpdateWatchdog(guard, restart, TimeProvider.System, NullLogger<UpdateWatchdog>.Instance);

        await watchdog.StartAsync(CancellationToken.None);
        Assert.NotNull(guard.Confirm());
        await watchdog.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(restart.Requested);
        Assert.Equal("1.2.0", FakeRelease.VersionAt(installation.ConsoleBinary));
    }

    private sealed class FakeLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public int Stops { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stops++;
    }

    [Fact]
    public async Task If_the_swap_never_took_effect_the_old_version_reports_the_failure_and_carries_on()
    {
        var installation = await UpdatedAsync();
        // Someone (or a crash between the two steps) left the old program in place.
        File.WriteAllBytes(installation.ConsoleBinary, FakeRelease.Program("1.0.0"));
        var guard = Guard(installation, "1.0.0", new TestClock(T0));

        Assert.Equal(GuardStart.Nothing, guard.OnStart());

        Assert.Null(guard.State.Pending);
        Assert.Equal(AgentUpdateOutcomes.Failed, guard.State.Last!.Outcome);
        Assert.Contains("did not take effect", guard.State.Last.Detail);
        Assert.Equal("1.0.0", FakeRelease.VersionAt(installation.ConsoleBinary));
    }

    [Fact]
    public async Task A_program_replaced_by_hand_since_clears_the_pending_update_quietly()
    {
        var installation = await UpdatedAsync();
        var guard = Guard(installation, "1.3.0", new TestClock(T0)); // reinstalled with the install script meanwhile

        Assert.Equal(GuardStart.Nothing, guard.OnStart());
        Assert.Null(guard.State.Pending);
        Assert.Null(guard.State.Last);
    }

    [Fact]
    public async Task With_the_previous_version_gone_there_is_no_roll_back_and_the_new_one_stays()
    {
        var installation = await UpdatedAsync();
        File.Delete(UpdateInstallation.BackupPath(installation.ConsoleBinary));
        var guard = Guard(installation, "1.2.0", new TestClock(T0));
        guard.OnStart();

        Assert.Null(guard.RollBack("it ran without reporting to the hub in time"));

        Assert.Equal("1.2.0", FakeRelease.VersionAt(installation.ConsoleBinary));
        Assert.Null(guard.State.Pending);
        Assert.Equal(AgentUpdateOutcomes.Failed, guard.State.Last!.Outcome);
        Assert.Contains("no longer there to go back to", guard.State.Last.Detail);
    }

    [Fact]
    public void A_damaged_state_file_reads_as_no_state()
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        File.WriteAllText(installation.StateFile, "{ not json");

        Assert.Equal(new UpdateState(), new UpdateStateStore(installation.StateFile).Read());
        Assert.Equal(GuardStart.Nothing, Guard(installation, "1.0.0", new TestClock(T0)).OnStart());
    }

    // ---- the action, and the two separate switches ----------------------------------------------

    [Theory]
    [InlineData("0.2.0", null)]
    [InlineData("1.0.0-rc.1", null)]
    [InlineData("v0.2.0", "not a strict")]
    [InlineData("0.2", "not a strict")]
    [InlineData("0.2.0 --force", "not a strict")]
    [InlineData("https://evil.example/agent.tar.gz", "not a strict")]
    [InlineData("../../etc/passwd", "not a strict")]
    public void An_update_action_carries_a_strict_version_and_nothing_else(string version, string? problem)
    {
        var result = AgentActions.Validate(AgentAction.UpdateAgent(version));
        if (problem is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Contains(problem, result);
        }
    }

    [Fact]
    public void Update_and_git_actions_do_not_share_fields()
    {
        var update = AgentAction.UpdateAgent("0.2.0");
        Assert.Null(update.RepoPath);
        Assert.False(AgentActions.IsRepoAction(update.Kind));
        Assert.Equal("devdash-agent update --version 0.2.0", AgentActions.CommandLine(update));
        Assert.Equal("Update the agent to 0.2.0", AgentActions.Summary(update));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentActions.GitArgs(update));

        // On the wire it is a kind and a version: no path, no URL, nothing to run.
        Assert.Equal("""{"kind":"UpdateAgent","version":"0.2.0","setUpstream":false}""", AgentActions.Key(update));
        Assert.Equal(update, JsonSerializer.Deserialize<AgentAction>(AgentActions.Key(update), SnapshotSchema.JsonOptions));

        Assert.Contains("does not take a repository path", AgentActions.Validate(update with { RepoPath = "/dev/widget" }));
        Assert.Contains("do not match", AgentActions.Validate(update with { Branch = "main", Remote = "origin" }));
        Assert.Contains("do not match", AgentActions.Validate(new AgentAction { Kind = AgentActionKind.UpdateAgent }));
        Assert.Contains("do not match", AgentActions.Validate(AgentAction.Fetch("/dev/widget") with { Version = "0.2.0" }));
        Assert.Contains("Repository path is missing", AgentActions.Validate(new AgentAction { Kind = AgentActionKind.Fetch }));
    }

    private ActionExecutor Executor(AgentOptions options, UpdateInstallation installation, string version = "1.0.0")
    {
        var monitor = new TestOptions(options);
        var git = new GitRunner();
        return new ActionExecutor(git, new ActionValidator(git, monitor), monitor, NullLogger<ActionExecutor>.Instance, new AgentGate(), _site.Updater(installation, version, options));
    }

    [Fact]
    public void Updates_default_to_notify_and_actions_default_to_off()
    {
        var options = new AgentOptions();
        Assert.Equal(AgentUpdateModes.Notify, options.Updates.ResolvedMode);
        Assert.True(options.Updates.AcceptsRequests);
        Assert.False(options.Updates.IsAuto);
        Assert.False(options.Actions.Enabled);

        // So by default the agent polls, for update requests only.
        Assert.True(ActionWorker.ShouldPoll(options));
        Assert.False(ActionWorker.ShouldPoll(new AgentOptions { Updates = new UpdateOptions { Mode = "off" } }));
        Assert.True(ActionWorker.ShouldPoll(new AgentOptions { Updates = new UpdateOptions { Mode = "off" }, Actions = new ActionsOptions { Enabled = true } }));

        Assert.Equal(AgentUpdateModes.Off, new UpdateOptions { Mode = "sometimes" }.ResolvedMode); // a typo never grants anything
        Assert.Equal(AgentUpdateModes.Auto, new UpdateOptions { Mode = " AUTO " }.ResolvedMode);
    }

    [Fact]
    public async Task With_actions_off_and_updates_on_request_an_update_runs_and_a_git_action_is_still_refused()
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        _site.Publish("1.2.0", installation);
        var repo = Directory.CreateDirectory(Path.Combine(_root, "Developer", "widget")).FullName;
        var options = new AgentOptions { Roots = [Path.Combine(_root, "Developer")], Actions = new ActionsOptions { Enabled = false }, Updates = new UpdateOptions { Mode = "notify" } };
        var executor = Executor(options, installation);

        var git = await executor.ExecuteAsync(AgentAction.Fetch(repo), CancellationToken.None);
        Assert.False(git.Succeeded);
        Assert.True(git.Refused);
        Assert.Contains("agent actions are not enabled on this machine", git.Output);
        Assert.False(git.RestartRequired);

        var update = await executor.ExecuteAsync(AgentAction.UpdateAgent("1.2.0"), CancellationToken.None);
        Assert.True(update.Succeeded, update.Output);
        Assert.Equal(0, update.ExitCode);
        Assert.True(update.RestartRequired);
        Assert.Contains("Installed 1.2.0 (was 1.0.0)", update.Output);
        Assert.Empty(update.AffectedPaths);
        Assert.Equal("1.2.0", FakeRelease.VersionAt(installation.ConsoleBinary));
    }

    [Fact]
    public async Task With_updates_off_an_update_action_is_refused_even_when_git_actions_are_on()
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        _site.Publish("1.2.0", installation);
        var options = new AgentOptions { Actions = new ActionsOptions { Enabled = true }, Updates = new UpdateOptions { Mode = "off" } };

        var outcome = await Executor(options, installation).ExecuteAsync(AgentAction.UpdateAgent("1.2.0"), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.Refused);
        Assert.Contains("updates are switched off on this machine", outcome.Output);
        Assert.Empty(_site.Requests);
        AssertUntouched(installation, "1.0.0");
    }

    [Theory]
    [InlineData("1.0.0", true, "Already on 1.0.0.")]
    [InlineData("0.9.0", false, "does not downgrade")]
    [InlineData("not-a-version", false, "not a strict")]
    public async Task An_update_action_never_downgrades_or_takes_anything_but_a_version(string version, bool succeeded, string expected)
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        _site.Publish("0.9.0", installation, latest: false);

        var outcome = await Executor(new AgentOptions(), installation).ExecuteAsync(AgentAction.UpdateAgent(version), CancellationToken.None);

        Assert.Equal(succeeded, outcome.Succeeded);
        Assert.Contains(expected, outcome.Output);
        Assert.False(outcome.RestartRequired);
        Assert.Empty(_site.Requests);
        AssertUntouched(installation, "1.0.0");
    }

    [Fact]
    public async Task A_failed_update_is_reported_as_failed_not_refused()
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        _site.Publish("1.2.0", installation, tamper: files => files.Remove(AgentUpdater.SignatureFile));

        var outcome = await Executor(new AgentOptions(), installation).ExecuteAsync(AgentAction.UpdateAgent("1.2.0"), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("no signature", outcome.Output);
        Assert.False(outcome.RestartRequired);
    }

    [Fact]
    public async Task An_update_waits_for_a_running_action_to_finish()
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        _site.Publish("1.2.0", installation);
        var gate = new AgentGate();
        var monitor = new TestOptions(new AgentOptions());
        var git = new GitRunner();
        var executor = new ActionExecutor(git, new ActionValidator(git, monitor), monitor, NullLogger<ActionExecutor>.Instance, gate, _site.Updater(installation, "1.0.0"));

        var busy = await gate.EnterAsync(CancellationToken.None); // an action (or auto-update) is in progress
        var update = executor.ExecuteAsync(AgentAction.UpdateAgent("1.2.0"), CancellationToken.None);
        await Task.Delay(150);
        Assert.False(update.IsCompleted);
        Assert.Equal("1.0.0", FakeRelease.VersionAt(installation.ConsoleBinary));

        busy.Dispose();
        Assert.True((await update.WaitAsync(TimeSpan.FromSeconds(20))).Succeeded);
        Assert.Equal("1.2.0", FakeRelease.VersionAt(installation.ConsoleBinary));
    }

    // ---- settings and commands ------------------------------------------------------------------

    [Fact]
    public void Install_writes_the_update_mode()
    {
        var request = InstallRequest.Parse(["--hub-url", "https://devdash.example.ts.net", "--api-key", "ddk_x", "--updates", "AUTO"]);
        Assert.Null(request.Error);
        Assert.Equal("auto", request.Settings["Updates:Mode"]);

        var json = AgentConfigWriter.Merge(null, request, "/home/sam/.config/devdash/agent.key");
        Assert.Equal("auto", JsonDocument.Parse(json).RootElement.GetProperty("Agent").GetProperty("Updates").GetProperty("Mode").GetString());

        Assert.Contains("off, notify or auto", InstallRequest.Parse(["--updates", "always"]).Error);
        Assert.Contains("needs a value", InstallRequest.Parse(["--updates"]).Error);
        Assert.False(InstallRequest.Parse(["--hub-url", "https://x.example"]).Settings.ContainsKey("Updates:Mode")); // re-running install keeps what was set
    }

    [Fact]
    public void The_update_command_parses_its_flags()
    {
        var plain = AgentCli.Parse(["update"]);
        Assert.Equal(("update", null, false, false), (plain.Command, plain.UpdateVersion, plain.Check, plain.NoRestart));

        var full = AgentCli.Parse(["update", "--version", "v0.3.0", "--check", "--no-restart"]);
        Assert.Null(full.Error);
        Assert.Equal(("0.3.0", true, true), (full.UpdateVersion, full.Check, full.NoRestart));

        Assert.Contains("MAJOR.MINOR.PATCH", AgentCli.Parse(["update", "--version", "latest"]).Error);
        Assert.Contains("needs a value", AgentCli.Parse(["update", "--version"]).Error);
        Assert.NotNull(AgentCli.Parse(["run", "--check"]).Error); // update flags belong to update
        Assert.NotNull(AgentCli.Parse(["update", "--base-url", "http://x"]).Error); // where releases come from is not a flag

        Assert.Equal("auto", AgentCli.Parse(["run", "--updates", "auto"]).Overrides["Agent:Updates:Mode"]);
        Assert.NotNull(AgentCli.Parse(["run", "--updates", "yes"]).Error);
    }

    [Fact]
    public void The_reported_update_mode_is_off_for_an_agent_that_cannot_update_itself()
    {
        var installation = FakeRelease.Install(Dir(), "1.0.0");
        var options = new AgentOptions { Updates = new UpdateOptions { Mode = "auto" } };

        Assert.Equal("auto", MachineInfoProvider.ReportedUpdateMode(options, installation, _site.Trust));
        Assert.Equal("off", MachineInfoProvider.ReportedUpdateMode(options, null, _site.Trust)); // dotnet run
        Assert.Equal("off", MachineInfoProvider.ReportedUpdateMode(options, installation, new ReleaseTrust("")));
        Assert.Equal("notify", MachineInfoProvider.ReportedUpdateMode(new AgentOptions(), installation, _site.Trust));
    }

    [Fact]
    public void Auto_mode_checks_on_a_jittered_interval()
    {
        Assert.Equal(TimeSpan.FromHours(6), AutoUpdateWorker.NextDelay(6, 0));
        Assert.Equal(TimeSpan.FromHours(7.5), AutoUpdateWorker.NextDelay(6, 1));
        Assert.Equal(TimeSpan.FromMinutes(30), AutoUpdateWorker.NextDelay(0, 0)); // never a tight loop
        Assert.Equal(TimeSpan.FromHours(6), AutoUpdateWorker.NextDelay(double.NaN, 0));
    }

    // ---- restarting the service after a manual update -------------------------------------------

    private sealed class RecordingRunner : ICommandRunner
    {
        public List<string> Commands { get; } = [];

        public Task<CommandResult> RunAsync(ServiceCommand command, CancellationToken ct)
        {
            Commands.Add(command.ToString());
            return Task.FromResult(new CommandResult(0, ""));
        }
    }

    [Fact]
    public async Task A_manual_update_restarts_the_service_only_when_the_service_runs_the_updated_program()
    {
        var home = Dir("home");
        var installed = Path.Combine(home, ".local", "bin", "devdash-agent");
        var environment = new ServiceEnvironment { Platform = ServicePlatform.MacOS, HomeDirectory = home, ExecutablePath = installed, UserName = "sam", UserId = "501" };
        var manager = new LaunchdServiceManager(environment);
        var runner = new RecordingRunner();
        var (serviceCopy, _) = UpdateInstallation.For(installed, ServicePlatform.MacOS, "osx-arm64");
        var (otherCopy, _) = UpdateInstallation.For(Path.Combine(Dir("elsewhere"), "devdash-agent"), ServicePlatform.MacOS, "osx-arm64");
        var elsewhere = environment with { ExecutablePath = otherCopy!.ConsoleBinary };

        Assert.Contains("No background service is installed", await UpdateCli.RestartServiceAsync(serviceCopy!, environment, runner, CancellationToken.None));

        Directory.CreateDirectory(Path.GetDirectoryName(manager.PlistPath)!);
        File.WriteAllText(manager.PlistPath, manager.Definition.Content);

        // A second copy of the agent (a test install, say) must not bounce the real service.
        Assert.Contains("runs a different copy", await UpdateCli.RestartServiceAsync(otherCopy, elsewhere, runner, CancellationToken.None));
        Assert.Empty(runner.Commands);

        Assert.StartsWith("Restarted the launchd agent", await UpdateCli.RestartServiceAsync(serviceCopy!, environment, runner, CancellationToken.None));
        Assert.Equal([$"launchctl kickstart -k gui/501/{LaunchdServiceManager.Label}"], runner.Commands);
    }

    [Fact]
    public void Each_service_manager_knows_how_to_restart_into_the_new_program()
    {
        var linux = new SystemdUserServiceManager(new ServiceEnvironment { Platform = ServicePlatform.Linux, HomeDirectory = "/home/sam", ExecutablePath = "/home/sam/.local/bin/devdash-agent" });
        Assert.Equal(["systemctl --user restart devdash-agent.service"], linux.RestartCommands.Select(c => c.ToString()));

        var windows = new ScheduledTaskServiceManager(new ServiceEnvironment
        {
            Platform = ServicePlatform.Windows, HomeDirectory = @"C:\Users\Sam", ExecutablePath = @"C:\Users\Sam\AppData\Local\DevDash\bin\devdash-agent.exe", UserName = "Sam", FileExists = _ => true,
        });
        // The task ignores /Run while it is running, so it is ended first.
        Assert.Equal(["schtasks.exe /End /TN \"DevDash Agent (Sam)\"", "schtasks.exe /Run /TN \"DevDash Agent (Sam)\""], windows.RestartCommands.Select(c => c.ToString()));
        Assert.True(windows.RestartCommands[0].IgnoreFailure);
    }

    // ---- the release workflow's own check -------------------------------------------------------

    [Fact]
    public void Verify_release_accepts_a_signed_folder_and_rejects_a_changed_one()
    {
        var installation = new UpdateInstallation { Platform = ServicePlatform.Linux, Directory = Dir(), Rid = "linux-x64" };
        var folder = Directory.CreateDirectory(Path.Combine(_root, "dist")).FullName;
        var archive = FakeRelease.Archive(installation, name => FakeRelease.Program("1.2.0", name));
        File.WriteAllBytes(Path.Combine(folder, installation.AssetName), archive);
        File.WriteAllText(Path.Combine(folder, "version.txt"), "1.2.0\n");
        var checksums = $"{ReleaseVerifier.Sha256Hex(archive)}  {installation.AssetName}\n{ReleaseVerifier.Sha256Hex("1.2.0\n"u8)}  version.txt\n";
        File.WriteAllText(Path.Combine(folder, "checksums.txt"), checksums);
        File.WriteAllBytes(Path.Combine(folder, "checksums.txt.sig"), _site.Key.SignData(Encoding.ASCII.GetBytes(checksums), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        int Check(ReleaseTrust trust, out string text, params string[] extra)
        {
            var output = new StringWriter();
            var code = ReleaseCheck.Run([folder, .. extra], trust, output, output);
            text = output.ToString();
            return code;
        }

        Assert.Equal(0, Check(_site.Trust, out var ok));
        Assert.Contains("Release 1.2.0: signature valid", ok);

        // A build carrying a test key must never pass the release check, even over files signed with that key.
        Assert.Equal(1, Check(_site.Trust with { IsTestKey = true }, out var test));
        Assert.Contains("TEST signing key", test);
        Assert.Equal(0, Check(_site.Trust with { IsTestKey = true }, out _, "--allow-test-key"));

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(1, Check(new ReleaseTrust(stranger.ExportSubjectPublicKeyInfoPem()), out var wrongKey));
        Assert.Contains("not valid for this agent's release key", wrongKey);
        Assert.Equal(1, Check(new ReleaseTrust(ReleaseSigning.PublicKeyPem) is { IsConfigured: false } placeholder ? placeholder : new ReleaseTrust(""), out _));

        File.AppendAllText(Path.Combine(folder, installation.AssetName), "x");
        Assert.Equal(1, Check(_site.Trust, out var changed));
        Assert.Contains("checksum mismatch", changed);

        File.Delete(Path.Combine(folder, "checksums.txt.sig"));
        Assert.Equal(1, Check(_site.Trust, out var unsigned));
        Assert.Contains("no signature", unsigned);
    }
}
