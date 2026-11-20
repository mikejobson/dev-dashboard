using System.Diagnostics;
using System.Text.Json;
using DevDash.Agent;
using DevDash.Agent.Actions;
using DevDash.Agent.Git;
using DevDash.Agent.Scanning;
using DevDash.Contracts;
using DevDash.Core.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDash.Core.Tests.Agent;

/// <summary>
/// Archiving a project and restoring it, against throwaway repositories under the system temp folder. The
/// archive folder and the trash are folders inside the test's own temp folder: nothing here reaches a real
/// trash, a real archive folder or a real working copy.
/// </summary>
public sealed class ArchiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("devdash-archive-").FullName;
    private readonly string _dev;
    private readonly string _origin;
    private readonly string _trash;
    private readonly string _archive;
    private readonly AgentOptions _options;
    private readonly StaticOptions _monitor;
    private readonly GitRunner _git = new();
    private readonly MachineScanner _scanner;
    private readonly RemovalLog _removals;

    public ArchiveTests()
    {
        _dev = Directory.CreateDirectory(Path.Combine(_root, "Developer")).FullName;
        _trash = Path.Combine(_root, "trash");
        _archive = Directory.CreateDirectory(Path.Combine(_root, "archive")).FullName;
        _origin = Path.Combine(_root, "origin", "widget.git");

        var seed = Directory.CreateDirectory(Path.Combine(_root, "seed")).FullName;
        Git(seed, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(seed, "README.md"), "# Widget\n");
        File.WriteAllText(Path.Combine(seed, ".gitignore"), "node_modules/\n.env\n");
        File.WriteAllText(Path.Combine(seed, "package.json"), "{}\n");
        Git(seed, "add", ".");
        Git(seed, "commit", "-q", "-m", "initial");
        Directory.CreateDirectory(Path.GetDirectoryName(_origin)!);
        Git(_root, "clone", "-q", "--bare", seed, _origin);
        Git(_dev, "clone", "-q", _origin, "widget");

        _options = new AgentOptions
        {
            Roots = [_dev],
            MachineId = "test-machine",
            Watch = false,
            Actions = new ActionsOptions { Enabled = true },
            Archive = new ArchiveOptions { Folder = _archive, MarginMegabytes = 0 },
            AutoFetch = new AutoFetchOptions { Enabled = true, IntervalHours = 4, TimeoutSeconds = 30 },
        };
        _monitor = new StaticOptions(_options);
        _scanner = new MachineScanner(
            new RepoScanner(_git, new FetchService(_git, _monitor, NullLogger<FetchService>.Instance), _monitor),
            new SizeCache(Path.Combine(_root, "sizes.json")), _monitor, NullLogger<MachineScanner>.Instance);
        _removals = new RemovalLog(Path.Combine(_root, "removed.json"));
    }

    private string Widget => Path.Combine(_dev, "widget");

    private ArchiveActions Archives(ITrash? trash = null, Func<string, long?>? freeSpace = null) =>
        new(_git, _monitor, NullLogger<ArchiveActions>.Instance, _scanner, trash ?? new FolderTrash(_trash), _removals, freeSpace: freeSpace ?? (_ => long.MaxValue));

    private Task<ActionOutcome> Run(AgentAction action, ArchiveActions? archives = null, Action<string>? progress = null)
    {
        var validator = new ActionValidator(_git, _monitor, _removals);
        var executor = new ActionExecutor(_git, validator, _monitor, NullLogger<ActionExecutor>.Instance, archives: archives ?? Archives());
        return executor.ExecuteAsync(action, progress, CancellationToken.None);
    }

    private async Task<SafetyAssessment> AssessAsync(string path)
    {
        var entry = await _scanner.ScanRepoAsync(_dev, path, new ScanRequest(AllowFetch: true, SizeMode.CachedOnly), CancellationToken.None);
        return RepoSafety.Assess(entry, DateTimeOffset.UtcNow, includeRepository: true);
    }

    private async Task<AgentAction> ArchiveOf(string path, bool includeBuildOutput = false) =>
        AgentAction.ArchiveProject(path, (await AssessAsync(path)).Fingerprint, includeBuildOutput);

    /// <summary>Work that exists only here: a commit not pushed, a changed file, an untracked file, an ignored one, build output.</summary>
    private void MakeAtRisk()
    {
        File.WriteAllText(Path.Combine(Widget, "local.txt"), "only here\n");
        Git(Widget, "add", "local.txt");
        Git(Widget, "commit", "-q", "-m", "unpushed");
        File.AppendAllText(Path.Combine(Widget, "README.md"), "changed\n");
        File.WriteAllText(Path.Combine(Widget, "notes.md"), "untracked\n");
        File.WriteAllText(Path.Combine(Widget, ".env"), "SECRET=1\n");
        Directory.CreateDirectory(Path.Combine(Widget, "node_modules", "left-pad"));
        File.WriteAllText(Path.Combine(Widget, "node_modules", "left-pad", "index.js"), "module.exports = 1;\n");
    }

    private ArchiveManifest ManifestOf(ActionOutcome outcome)
    {
        Assert.NotNull(outcome.Archive);
        var json = File.ReadAllText(outcome.Archive!.ManifestPath);
        return JsonSerializer.Deserialize<ArchiveManifest>(json, SnapshotSchema.JsonOptions)!;
    }

    private string[] ArchiveFolderFiles() => Directory.GetFiles(_archive).Select(Path.GetFileName).ToArray()!;

    private static void AssertRefused(ActionOutcome outcome, string because)
    {
        Assert.False(outcome.Succeeded, outcome.Output);
        Assert.True(outcome.Refused, outcome.Output);
        Assert.Contains(because, outcome.Output);
    }

    // ---- shape ------------------------------------------------------------------------------------

    public static TheoryData<AgentAction> Malformed => new()
    {
        new AgentAction { Kind = AgentActionKind.ArchiveProject, RepoPath = "/dev/x" }, // no fingerprint
        AgentAction.ArchiveProject("/dev/x", "not-a-fingerprint"),
        AgentAction.ArchiveProject("relative/x", "0123456789abcdef01234567"),
        AgentAction.ArchiveProject("/dev/x", "0123456789abcdef01234567") with { AcknowledgedCaveats = [".env"] },
        AgentAction.ArchiveProject("/dev/x", "0123456789abcdef01234567") with { ArchivePath = "/a/x.tar.gz" },
        AgentAction.RemoveProject("/dev/x", "0123456789abcdef01234567") with { IncludeBuildOutput = true },
        AgentAction.Fetch("/dev/x") with { IncludeBuildOutput = true },
        new AgentAction { Kind = AgentActionKind.RestoreArchive, RepoPath = "/dev/x", ArchivePath = "/a/x.tar.gz" }, // no checksum
        AgentAction.RestoreArchive("/a/x.tar.gz", "ABC", "/dev/x"),
        AgentAction.RestoreArchive("/a/x.tar.gz", new string('A', 64), "/dev/x"),
        AgentAction.RestoreArchive("relative.tar.gz", new string('a', 64), "/dev/x"),
        AgentAction.RestoreArchive("/a/x.tar.gz", new string('a', 64), "relative"),
        AgentAction.RestoreArchive("/a/x.tar.gz", new string('a', 64), "/dev/x") with { ExpectedFingerprint = "0123456789abcdef01234567" },
        AgentAction.CloneInto("/dev/x", "https://example.invalid/x.git") with { ArchiveSha256 = new string('a', 64) },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task Malformed_actions_are_refused_before_anything_is_looked_at(AgentAction action)
    {
        Assert.NotNull(AgentActions.Validate(action));
        var outcome = await Run(action);
        Assert.True(outcome.Refused, outcome.Output);
        Assert.True(Directory.Exists(Widget));
        Assert.Empty(ArchiveFolderFiles());
    }

    [Fact]
    public void Well_formed_actions_say_what_they_do()
    {
        var archive = AgentAction.ArchiveProject("/dev/x", "0123456789abcdef01234567", includeBuildOutput: true);
        var restore = AgentAction.RestoreArchive("/archive/x-20261120-101010.tar.gz", new string('a', 64), "/dev/x");
        Assert.Null(AgentActions.Validate(archive));
        Assert.Null(AgentActions.Validate(restore));
        Assert.Equal("archive --include-build-output '/dev/x' && trash '/dev/x'", AgentActions.CommandLine(archive));
        Assert.Equal("restore '/archive/x-20261120-101010.tar.gz' '/dev/x'", AgentActions.CommandLine(restore));
        Assert.Contains("read back", AgentActions.Safeguard(archive));
        Assert.Contains("SHA-256", AgentActions.Safeguard(restore));
        Assert.True(AgentActions.IsRepoAction(AgentActionKind.ArchiveProject) && AgentActions.IsRepoAction(AgentActionKind.RestoreArchive));
        Assert.True(AgentActions.IsLongRunning(AgentActionKind.ArchiveProject) && AgentActions.IsLongRunning(AgentActionKind.RestoreArchive));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentActions.GitArgs(archive));
    }

    // ---- what the snapshot says ------------------------------------------------------------------

    [Fact]
    public async Task The_snapshot_says_where_archives_go_and_whether_they_can()
    {
        var on = await MachineInfoProvider.GetAsync(_options, CancellationToken.None);
        Assert.NotNull(on.Archive);
        Assert.Equal(_archive, on.Archive!.Folder);
        Assert.True(on.Archive is { Available: true, Writable: true, Problem: null });
        Assert.True(on.Archive.FreeBytes > 0);
        Assert.Equal(OperatingSystem.IsWindows() ? ArchiveFormats.Zip : ArchiveFormats.TarGz, on.Archive.Format);
        Assert.True(on is { AcceptsArchiveProject: true, AcceptsRestoreArchive: true });
        Assert.True(AgentActions.Accepts(on, AgentActionKind.ArchiveProject));
        Assert.Empty(ArchiveFolderFiles()); // the write probe cleaned up after itself

        // A share that is not mounted: reported, not accepted, and not created.
        var missing = Path.Combine(_root, "nas", "archive");
        var unmounted = await MachineInfoProvider.GetAsync(new AgentOptions { Actions = new ActionsOptions { Enabled = true }, Archive = new ArchiveOptions { Folder = missing } }, CancellationToken.None);
        Assert.True(unmounted is { AcceptsArchiveProject: false, AcceptsRestoreArchive: false, Archive.Available: false });
        Assert.Contains("does not exist", AgentActions.WhyNotAccepted(unmounted with { AgentVersion = "0.8.0" }, AgentActionKind.ArchiveProject));
        Assert.False(Directory.Exists(missing));

        // Not configured: nothing reported. Before 0.8.0: told to update.
        var off = await MachineInfoProvider.GetAsync(new AgentOptions { Actions = new ActionsOptions { Enabled = true } }, CancellationToken.None);
        Assert.True(off is { Archive: null, AcceptsArchiveProject: false });
        Assert.Contains("Agent:Archive:Folder", AgentActions.WhyNotAccepted(off with { AgentVersion = "0.8.0" }, AgentActionKind.ArchiveProject));
        Assert.Contains("0.8.0", AgentActions.WhyNotAccepted(off with { AgentVersion = "0.7.0" }, AgentActionKind.RestoreArchive));
    }

    // ---- archive and restore -----------------------------------------------------------------------

    [Fact]
    public async Task An_at_risk_project_is_archived_checked_and_trashed_then_restored_exactly()
    {
        MakeAtRisk();
        var posix = !OperatingSystem.IsWindows();
        var script = Path.Combine(Widget, "build.sh");
        File.WriteAllText(script, "#!/bin/sh\necho hi\n");
        if (posix)
        {
            File.SetUnixFileMode(script, File.GetUnixFileMode(script) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
            File.CreateSymbolicLink(Path.Combine(Widget, "docs-link"), "README.md");
            // A link that leads out of the project is kept as a link; what it points at is not archived.
            File.WriteAllText(Path.Combine(_root, "outside.txt"), "not part of the project\n");
            File.CreateSymbolicLink(Path.Combine(Widget, "outside-link"), Path.Combine(_root, "outside.txt"));
            Directory.CreateSymbolicLink(Path.Combine(Widget, "dir-link"), _root);
        }

        var head = Git(Widget, "rev-parse", "HEAD");
        var safety = await AssessAsync(Widget);
        Assert.Equal(SafetyVerdict.AtRisk, safety.Verdict);
        var progress = new List<string>();
        ArchiveActions.ProgressEvery = TimeSpan.Zero;
        var outcome = await Run(AgentAction.ArchiveProject(Widget, safety.Fingerprint), progress: progress.Add);

        Assert.True(outcome.Succeeded, outcome.Output);
        Assert.False(Directory.Exists(Widget));
        Assert.Single(Directory.GetDirectories(_trash));
        Assert.Contains(progress, p => p.StartsWith("Archiving ", StringComparison.Ordinal));
        Assert.Contains(progress, p => p.StartsWith("Checking the archive", StringComparison.Ordinal));

        var manifest = ManifestOf(outcome);
        var archivePath = outcome.Archive!.ArchivePath;
        Assert.Equal(_archive, Path.GetDirectoryName(archivePath));
        Assert.Matches(@"^widget-\d{8}-\d{6}\.(tar\.gz|zip)$", Path.GetFileName(archivePath));
        Assert.Equal(ArchiveActions.ManifestPathOf(archivePath), outcome.Archive.ManifestPath);
        Assert.Equal(2, ArchiveFolderFiles().Length); // the archive and its manifest: no .partial left behind
        Assert.Equal(ProjectArchive.Sha256Of(archivePath, CancellationToken.None), manifest.ArchiveSha256);
        Assert.Equal(new FileInfo(archivePath).Length, manifest.ArchiveBytes);
        Assert.Equal(Widget, manifest.SourcePath);
        Assert.Equal("widget", manifest.RootName);
        Assert.Equal("test-machine", manifest.MachineId);
        Assert.Equal(_origin, Assert.Single(manifest.Remotes).Url);
        Assert.Equal("main", manifest.HeadBranch);
        Assert.Equal(head, manifest.HeadSha);
        Assert.Contains(manifest.Branches, b => b.Name == "main" && b.Sha == manifest.HeadSha);
        Assert.Equal("AtRisk", manifest.Verdict);
        Assert.Equal(safety.Fingerprint, manifest.Fingerprint);
        Assert.Equal(["node_modules"], manifest.ExcludedBuildOutput);
        Assert.False(manifest.IncludesBuildOutput);
        Assert.Equal(MachineInfoProvider.AgentVersion, manifest.DevDashVersion);
        Assert.Equal(posix ? 3 : 0, manifest.LinkCount);
        Assert.True(manifest.FileCount > 10, "the .git folder is in the archive");
        Assert.Contains("node_modules", outcome.Output);

        if (posix)
        {
            // An ordinary tar reads it: the link is a link and the script keeps its executable bit.
            var listing = Tool("tar", "-tzvf", archivePath);
            Assert.Contains("widget/docs-link -> README.md", listing);
            Assert.Matches(@"(?m)^-rwx\S*\s.*widget/build\.sh$", listing);
            Assert.DoesNotContain("node_modules", listing);
            // Links out of the project stay links: nothing they lead to is in the archive.
            Assert.Contains($"widget/outside-link -> {Path.Combine(_root, "outside.txt")}", listing);
            Assert.Contains($"widget/dir-link -> {_root}", listing);
            Assert.DoesNotContain("widget/dir-link/", listing);
        }

        // The agent remembers the project's remotes for a re-clone into the same folder.
        Assert.True(_removals.Allows(Widget, _origin));

        // Restore into where it was.
        var restored = await Run(AgentAction.RestoreArchive(archivePath, manifest.ArchiveSha256, Widget));
        Assert.True(restored.Succeeded, restored.Output);
        Assert.Equal("only here\n", File.ReadAllText(Path.Combine(Widget, "local.txt")));
        Assert.EndsWith("changed\n", File.ReadAllText(Path.Combine(Widget, "README.md")));
        Assert.Equal("untracked\n", File.ReadAllText(Path.Combine(Widget, "notes.md")));
        Assert.Equal("SECRET=1\n", File.ReadAllText(Path.Combine(Widget, ".env")));
        Assert.False(Directory.Exists(Path.Combine(Widget, "node_modules")));
        Assert.Equal(manifest.HeadSha, Git(Widget, "rev-parse", "HEAD"));
        Assert.Contains("M README.md", Git(Widget, "status", "--porcelain"));
        Assert.True(File.Exists(archivePath), "the archive is kept");
        Assert.DoesNotContain(Directory.GetDirectories(_dev), d => Path.GetFileName(d).StartsWith('.'));

        if (posix)
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(Widget, "build.sh")).HasFlag(UnixFileMode.UserExecute));
            Assert.False(File.GetUnixFileMode(Path.Combine(Widget, "README.md")).HasFlag(UnixFileMode.UserExecute));
            Assert.Equal("README.md", new FileInfo(Path.Combine(Widget, "docs-link")).LinkTarget);
            Assert.Equal(Path.Combine(_root, "outside.txt"), new FileInfo(Path.Combine(Widget, "outside-link")).LinkTarget);
            Assert.Equal(_root, new DirectoryInfo(Path.Combine(Widget, "dir-link")).LinkTarget);
            Assert.Equal("# Widget\nchanged\n", File.ReadAllText(Path.Combine(Widget, "docs-link")));
        }
    }

    [Fact]
    public async Task A_zip_round_trip_keeps_files_and_on_posix_links_and_modes()
    {
        _options.Archive.Format = "zip";
        MakeAtRisk();
        var posix = !OperatingSystem.IsWindows();
        if (posix)
        {
            var script = Path.Combine(Widget, "run.sh");
            File.WriteAllText(script, "#!/bin/sh\n");
            File.SetUnixFileMode(script, File.GetUnixFileMode(script) | UnixFileMode.UserExecute);
            File.CreateSymbolicLink(Path.Combine(Widget, "docs-link"), "README.md");
        }

        var outcome = await Run(await ArchiveOf(Widget, includeBuildOutput: true));
        Assert.True(outcome.Succeeded, outcome.Output);
        var manifest = ManifestOf(outcome);
        Assert.Equal(ArchiveFormats.Zip, manifest.Format);
        Assert.EndsWith(".zip", outcome.Archive!.ArchivePath, StringComparison.Ordinal);
        Assert.True(manifest.IncludesBuildOutput);
        Assert.Empty(manifest.ExcludedBuildOutput);

        var elsewhere = Path.Combine(_dev, "widget-restored");
        var restored = await Run(AgentAction.RestoreArchive(outcome.Archive.ArchivePath, manifest.ArchiveSha256, elsewhere));
        Assert.True(restored.Succeeded, restored.Output);
        Assert.Equal("module.exports = 1;\n", File.ReadAllText(Path.Combine(elsewhere, "node_modules", "left-pad", "index.js")));
        Assert.Equal("only here\n", File.ReadAllText(Path.Combine(elsewhere, "local.txt")));
        if (posix)
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(elsewhere, "run.sh")).HasFlag(UnixFileMode.UserExecute));
            Assert.Equal("README.md", new FileInfo(Path.Combine(elsewhere, "docs-link")).LinkTarget);
        }
    }

    // ---- what stops it, and what is left behind ---------------------------------------------------

    [Fact]
    public async Task A_damaged_archive_fails_verification_and_leaves_the_project_alone()
    {
        MakeAtRisk();
        var archives = Archives();
        archives.AfterWrite = partial =>
        {
            // Flip bytes in the middle of the compressed data, as a bad disk or a dropped connection to a share might.
            using var stream = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = stream.Length / 2;
            var buffer = new byte[64];
            stream.ReadExactly(buffer);
            stream.Position = stream.Length / 2;
            stream.Write(buffer.Select(b => (byte)~b).ToArray());
        };

        var outcome = await Run(await ArchiveOf(Widget), archives);
        Assert.False(outcome.Succeeded, outcome.Output);
        Assert.False(outcome.Refused);
        Assert.Contains("was not touched", outcome.Output);
        Assert.True(Directory.Exists(Widget));
        Assert.True(File.Exists(Path.Combine(Widget, "local.txt")));
        Assert.False(Directory.Exists(_trash) && Directory.EnumerateFileSystemEntries(_trash).Any());
        Assert.Empty(ArchiveFolderFiles());
        Assert.Null(outcome.Archive);
    }

    [Fact]
    public async Task A_file_written_while_archiving_stops_it()
    {
        var archives = Archives();
        archives.AfterWrite = _ => File.WriteAllText(Path.Combine(Widget, "late.txt"), "written during the archive\n");

        var outcome = await Run(await ArchiveOf(Widget), archives);
        Assert.False(outcome.Succeeded, outcome.Output);
        Assert.Contains("changed while it was being archived", outcome.Output);
        Assert.Contains("late.txt", outcome.Output);
        Assert.True(File.Exists(Path.Combine(Widget, "late.txt")));
        Assert.Empty(ArchiveFolderFiles());
    }

    [Fact]
    public async Task Not_enough_free_space_is_refused_before_anything_is_written()
    {
        _options.Archive.MarginMegabytes = 1;
        var outcome = await Run(await ArchiveOf(Widget), Archives(freeSpace: _ => 1024 * 1024));
        AssertRefused(outcome, "free");
        Assert.True(Directory.Exists(Widget));
        Assert.Empty(ArchiveFolderFiles());

        AssertRefused(await Run(await ArchiveOf(Widget), Archives(freeSpace: _ => null)), "could not tell how much space");
    }

    [Fact]
    public async Task A_project_that_changed_since_it_was_shown_is_refused()
    {
        var action = await ArchiveOf(Widget);
        File.WriteAllText(Path.Combine(Widget, "new.txt"), "new\n");
        Git(Widget, "add", "new.txt");
        Git(Widget, "commit", "-q", "-m", "since");

        AssertRefused(await Run(action), "has changed since it was shown");
        Assert.True(Directory.Exists(Widget));
        Assert.Empty(ArchiveFolderFiles());
    }

    [Fact]
    public async Task A_failed_move_to_the_trash_deletes_the_archive_again()
    {
        var outcome = await Run(await ArchiveOf(Widget), Archives(trash: new BrokenTrash()));
        Assert.False(outcome.Succeeded, outcome.Output);
        Assert.Contains("archive was deleted again", outcome.Output);
        Assert.True(Directory.Exists(Widget));
        Assert.Empty(ArchiveFolderFiles());
    }

    [Fact]
    public async Task Archiving_needs_its_folder_and_never_creates_it()
    {
        var action = await ArchiveOf(Widget);
        _options.Archive.Folder = null;
        AssertRefused(await Run(action), "not set up");

        var missing = Path.Combine(_root, "nas", "archive");
        _options.Archive.Folder = missing;
        AssertRefused(await Run(action), "does not exist");
        Assert.False(Directory.Exists(missing));

        // An archive folder inside the project would end up in its own archive, and in the trash.
        var inside = Directory.CreateDirectory(Path.Combine(Widget, "archive-here")).FullName;
        _options.Archive.Folder = inside;
        AssertRefused(await Run(await ArchiveOf(Widget)), "is inside");
        Assert.True(Directory.Exists(Widget));
    }

    [Fact]
    public async Task A_linked_worktree_and_a_repo_with_worktrees_are_refused()
    {
        var worktree = Path.Combine(_dev, "widget-feature");
        Git(Widget, "worktree", "add", "-q", "-b", "feature", worktree);

        AssertRefused(await Run(AgentAction.ArchiveProject(worktree, (await AssessAsync(worktree)).Fingerprint)), "linked worktree");
        AssertRefused(await Run(await ArchiveOf(Widget)), "linked worktrees");
        Assert.True(Directory.Exists(Widget) && Directory.Exists(worktree));
        Assert.Empty(ArchiveFolderFiles());
    }

    [Fact]
    public async Task Restore_refuses_a_target_that_exists_a_missing_archive_and_a_wrong_checksum()
    {
        var outcome = await Run(await ArchiveOf(Widget));
        Assert.True(outcome.Succeeded, outcome.Output);
        var manifest = ManifestOf(outcome);
        var archivePath = outcome.Archive!.ArchivePath;

        // Something is at the target already.
        Directory.CreateDirectory(Widget);
        File.WriteAllText(Path.Combine(Widget, "keep.txt"), "mine\n");
        AssertRefused(await Run(AgentAction.RestoreArchive(archivePath, manifest.ArchiveSha256, Widget)), "already exists");
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(Widget, "keep.txt")));

        var target = Path.Combine(_dev, "widget-back");
        // Not the checksum the dashboard recorded.
        AssertRefused(await Run(AgentAction.RestoreArchive(archivePath, new string('0', 64), target)), "different checksum");
        // Outside the archive folder.
        var copy = Path.Combine(_root, Path.GetFileName(archivePath));
        File.Copy(archivePath, copy);
        AssertRefused(await Run(AgentAction.RestoreArchive(copy, manifest.ArchiveSha256, target)), "not in this machine's archive folder");
        // Outside the scan roots.
        AssertRefused(await Run(AgentAction.RestoreArchive(archivePath, manifest.ArchiveSha256, Path.Combine(_root, "elsewhere"))), "outside this agent's scan roots");

        // Damaged since: the checksum no longer matches the manifest.
        var bytes = File.ReadAllBytes(archivePath);
        bytes[^10] ^= 0xFF;
        File.WriteAllBytes(archivePath, bytes);
        AssertRefused(await Run(AgentAction.RestoreArchive(archivePath, manifest.ArchiveSha256, target)), "does not match its manifest");

        // Gone.
        File.Delete(archivePath);
        AssertRefused(await Run(AgentAction.RestoreArchive(archivePath, manifest.ArchiveSha256, target)), "is missing");
        Assert.False(Directory.Exists(target));
        Assert.DoesNotContain(Directory.GetDirectories(_dev), d => Path.GetFileName(d).StartsWith('.'));
    }

    [Fact]
    public void Entry_names_that_would_leave_the_folder_are_refused()
    {
        Assert.Equal("", ProjectArchive.Relative("widget", "widget"));
        Assert.Equal("src/a.txt", ProjectArchive.Relative("widget/src/a.txt", "widget"));
        foreach (var bad in (string[])["other/a.txt", "widget/../a.txt", "widget/./a", "widget//a", "/widget/a", "widgetx/a", "widget/a\\..\\b"])
        {
            Assert.Throws<InvalidDataException>(() => ProjectArchive.Relative(bad, "widget"));
        }
    }

    [Fact]
    public void Archive_names_never_replace_an_existing_one()
    {
        var now = new DateTimeOffset(2026, 11, 20, 10, 11, 12, TimeSpan.Zero);
        var (first, manifest) = ArchiveActions.Names(_archive, "widget", ArchiveFormats.TarGz, now);
        Assert.Equal(Path.Combine(_archive, "widget-20261120-101112.tar.gz"), first);
        Assert.Equal(Path.Combine(_archive, "widget-20261120-101112.json"), manifest);
        Assert.Equal(manifest, ArchiveActions.ManifestPathOf(first));
        File.WriteAllText(first, "");
        Assert.Equal(Path.Combine(_archive, "widget-20261120-101112-2.tar.gz"), ArchiveActions.Names(_archive, "widget", ArchiveFormats.TarGz, now).Archive);
    }

    private static string Tool(string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{program} failed: {stderr}");
        return stdout;
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in (string[])["-c", "user.name=DevDash Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "-c", "init.defaultBranch=main", .. args])
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                file.Attributes = FileAttributes.Normal;
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private sealed class BrokenTrash : ITrash
    {
        public string Name => "the trash";

        public TrashResult Move(string path) => TrashResult.Failed("the trash is on another volume.");
    }

    private sealed class StaticOptions(AgentOptions value) : IOptionsMonitor<AgentOptions>
    {
        public AgentOptions CurrentValue => value;

        public AgentOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AgentOptions, string?> listener) => null;
    }
}
