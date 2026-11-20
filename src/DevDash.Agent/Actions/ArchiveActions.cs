using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DevDash.Agent.Git;
using DevDash.Agent.Scanning;
using DevDash.Contracts;
using DevDash.Core.Git;
using DevDash.Core.Safety;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevDash.Agent.Actions;

/// <summary>
/// Archive a project instead of removing it, and put an archive back. <see cref="ActionExecutor"/> calls this
/// after its own checks (actions on, one action at a time, <see cref="ActionValidator"/>).
/// <para>
/// The order is what makes it safe: check the project as for a removal, write the archive under a temporary
/// name, read it back and compare every entry with what was walked, walk the folder again to see that nothing
/// changed meanwhile, give the archive its name and write its manifest, and only then move the folder to the
/// trash. Any failure before the last step deletes what was written and leaves the folder alone; a failed move
/// to the trash deletes the archive too, so the action either did everything or nothing.
/// </para>
/// </summary>
public sealed class ArchiveActions(
    GitRunner git,
    IOptionsMonitor<AgentOptions> options,
    ILogger<ArchiveActions> logger,
    MachineScanner? scanner = null,
    ITrash? trash = null,
    RemovalLog? removals = null,
    TimeProvider? time = null,
    Func<string, long?>? freeSpace = null)
{
    private static readonly string[] Prefix = ["-c", "core.fsmonitor=false", "-c", "gc.auto=0", "-c", "maintenance.auto=false"];

    /// <summary>How often progress is reported while an archive is written or unpacked.</summary>
    internal static TimeSpan ProgressEvery { get; set; } = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions ManifestJson = new(SnapshotSchema.JsonOptions) { WriteIndented = true };

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, long?> _freeSpace = freeSpace ?? DiskSpace.Available;

    private TimeSpan Local => TimeSpan.FromSeconds(options.CurrentValue.GitTimeoutSeconds);

    private TimeSpan Network => TimeSpan.FromSeconds(Math.Max(10, options.CurrentValue.Actions.NetworkTimeoutSeconds));

    /// <summary>Test hook: runs between writing the archive and reading it back.</summary>
    internal Action<string>? AfterWrite { get; set; }

    public Task<ActionOutcome> RunAsync(AgentAction action, Stopwatch stopwatch, Action<string>? progress, CancellationToken ct) => action.Kind switch
    {
        AgentActionKind.ArchiveProject => ArchiveAsync(action, stopwatch, progress, ct),
        AgentActionKind.RestoreArchive => RestoreAsync(action, stopwatch, progress, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "Not an archive action."),
    };

    private static ActionOutcome Refuse(Stopwatch stopwatch, string why) => new(false, null, "Refused: " + why, [], stopwatch.ElapsedMilliseconds);

    private static ActionOutcome Fail(Stopwatch stopwatch, string path, string why) => new(false, 1, AgentActions.Truncate(why), [path], stopwatch.ElapsedMilliseconds);

    /// <summary>The archive folder, or why it cannot be used.</summary>
    private string? Folder(out string? refusal)
    {
        refusal = null;
        if (options.CurrentValue.Archive.ResolvedFolder() is not { } folder)
        {
            refusal = "archiving is not set up on this machine (Agent:Archive:Folder).";
            return null;
        }

        if (!Directory.Exists(folder))
        {
            // Never created here: a share that is not mounted would otherwise fill the local disk under its mount point.
            refusal = $"the archive folder {folder} does not exist. The agent does not create it; is the share mounted?";
            return null;
        }

        return folder;
    }

    // ---- archive --------------------------------------------------------------------------------

    private async Task<ActionOutcome> ArchiveAsync(AgentAction action, Stopwatch stopwatch, Action<string>? progress, CancellationToken ct)
    {
        var path = action.RepoPath!;
        if (scanner is null || trash is null)
        {
            return Refuse(stopwatch, "this agent cannot archive projects.");
        }

        if (Folder(out var noFolder) is not { } folder)
        {
            return Refuse(stopwatch, noFolder!);
        }

        var resolvedFolder = PathSafety.Resolve(folder);
        var resolvedPath = PathSafety.Resolve(path);
        if (PathSafety.SamePath(resolvedFolder, resolvedPath) || PathSafety.IsInside(resolvedPath, resolvedFolder))
        {
            return Refuse(stopwatch, $"the archive folder {folder} is inside {path}.");
        }

        if (PathSafety.IsInside(resolvedFolder, resolvedPath))
        {
            return Refuse(stopwatch, $"{path} is inside the archive folder {folder}.");
        }

        if (!ArchiveSetup.CanWrite(folder, out var unwritable))
        {
            return Refuse(stopwatch, $"the archive folder {folder} cannot be written to: {unwritable}");
        }

        if (RescanRequests.Find(options.CurrentValue.ResolveRoots(), path) is not { } found)
        {
            return Refuse(stopwatch, $"{path} is outside this agent's scan roots.");
        }

        // As for a removal: fetch so the scan is as fresh as it can be. Unlike a removal, a failed fetch does not
        // stop an archive (a project whose remote is gone is exactly what gets archived); the archive keeps every
        // ref there is, and the fingerprint below still has to match what the dashboard showed.
        var remotes = RemoteListParser.Parse((await git.RunAsync(path, ["remote", "-v"], Local, ct).ConfigureAwait(false)).StdOut);
        var fetched = false;
        var notes = new List<string>();
        if (remotes.Count > 0)
        {
            var fetch = await git.ExecuteAsync(path, [.. Prefix, "fetch", "--all", "--prune", "--quiet"], Network, ct).ConfigureAwait(false);
            fetched = fetch.Ok;
            if (!fetch.Ok)
            {
                notes.Add($"Could not fetch first ({AgentActions.Truncate(fetch.StdErr).Split('\n')[0]}); the archive keeps every local ref anyway.");
            }
        }

        var entry = await scanner.ScanRepoAsync(found.Root, path, new ScanRequest(AllowFetch: false, SizeMode.CachedOnly) { JustFetched = fetched }, ct).ConfigureAwait(false);
        if (entry is not { Kind: EntryKind.GitRepo, Git: { } state })
        {
            return Refuse(stopwatch, $"{path} could not be read as a git repository{(entry.Error is { } error ? ": " + error : "")}. Nothing was archived.");
        }

        if (state.IsLinkedWorktree)
        {
            return Refuse(stopwatch, $"{path} is a linked worktree: its commits live in {state.MainWorktreePath ?? "its main repository"}. Archive the main repository instead.");
        }

        var safety = RepoSafety.Assess(entry, _time.GetUtcNow(), includeRepository: true);
        if (safety.Reasons.FirstOrDefault(r => r.Kind == SafetyReasonKind.LinkedWorktrees) is { } linked)
        {
            return Refuse(stopwatch, $"{path} has linked worktrees ({linked.Text}). Moving it would break them; remove them first. Nothing was archived.");
        }

        if (!string.Equals(safety.Fingerprint, action.ExpectedFingerprint, StringComparison.Ordinal))
        {
            return Refuse(stopwatch, $"{path} has changed since it was shown in the dashboard (a commit, a branch or its files). Nothing was archived. Refresh it and look again.");
        }

        var archive = options.CurrentValue.Archive;
        var format = archive.ResolvedFormat;
        var storeLinks = !(format == ArchiveFormats.Zip && OperatingSystem.IsWindows());
        Func<string, string, CancellationToken, Task<bool>>? exclude = action.IncludeBuildOutput ? null : UntrackedAsync;

        progress?.Invoke($"Looking through {path}.");
        ArchivePlan plan;
        try
        {
            plan = await ProjectArchive.PlanAsync(path, exclude, storeLinks, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse(stopwatch, $"could not read all of {path}: {ex.Message}. Nothing was archived.");
        }

        // The uncompressed size is the estimate: the archive is smaller unless the files are already compressed.
        var free = _freeSpace(folder);
        var needed = plan.TotalBytes + archive.MarginBytes;
        if (free is null)
        {
            return Refuse(stopwatch, $"could not tell how much space is free in {folder}. Nothing was archived.");
        }

        if (free < needed)
        {
            return Refuse(stopwatch, $"{folder} has {Size(free.Value)} free; archiving {path} needs up to {Size(plan.TotalBytes)} plus {Size(archive.MarginBytes)} to spare. Nothing was archived.");
        }

        var (archivePath, manifestPath) = Names(folder, plan.RootName, format, _time.GetLocalNow());
        var partial = archivePath + ".partial";
        var finished = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, archive.TimeoutMinutes)));
        try
        {
            logger.LogWarning("Archiving {Path} to {Archive}: asked for from the dashboard", path, archivePath);
            var clock = Stopwatch.StartNew();
            var last = TimeSpan.Zero;
            void Report(ArchiveProgress done)
            {
                if (clock.Elapsed - last >= ProgressEvery)
                {
                    last = clock.Elapsed;
                    progress?.Invoke($"Archiving {path}: {done.Files:N0} of {plan.FileCount:N0} files, {Size(done.Bytes)} of {Size(plan.TotalBytes)}.");
                }
            }

            var digest = await Task.Run(() => ProjectArchive.Write(plan, partial, format, Report, timeout.Token), timeout.Token).ConfigureAwait(false);
            AfterWrite?.Invoke(partial);

            // Read back: every entry, its size and the SHA-256 of its data, against what was written.
            progress?.Invoke($"Checking the archive of {path}.");
            var sha = await Task.Run(() => ProjectArchive.Sha256Of(partial, timeout.Token), timeout.Token).ConfigureAwait(false);
            var contents = await Task.Run(() => ProjectArchive.Read(partial, format, plan.RootName, timeout.Token), timeout.Token).ConfigureAwait(false);
            if (Mismatch(contents, plan.FileCount, plan.DirectoryCount, plan.LinkCount, plan.TotalBytes, digest) is { } bad)
            {
                return Fail(stopwatch, path, $"The archive did not read back as written ({bad}). It was deleted; {path} was not touched.");
            }

            // Anything written to the folder while it was archived would be lost with the folder: look again.
            var after = await ProjectArchive.PlanAsync(path, exclude, storeLinks, timeout.Token).ConfigureAwait(false);
            if (ProjectArchive.Differences(plan, after) is { Count: > 0 } changed)
            {
                return Fail(stopwatch, path, $"{path} changed while it was being archived ({string.Join(", ", changed.Take(5))}{(changed.Count > 5 ? $" and {changed.Count - 5} more" : "")}). The archive was deleted and the folder left as it is; try again when nothing is writing to it.");
            }

            File.Move(partial, archivePath, overwrite: false);
            var manifest = new ArchiveManifest
            {
                RootName = plan.RootName,
                SourcePath = path,
                MachineId = string.IsNullOrWhiteSpace(options.CurrentValue.MachineId) ? Environment.MachineName.ToLowerInvariant() : options.CurrentValue.MachineId.Trim(),
                Hostname = Environment.MachineName,
                ArchiveFile = Path.GetFileName(archivePath),
                Format = format,
                ArchiveSha256 = sha,
                ArchiveBytes = new FileInfo(archivePath).Length,
                FileCount = contents.FileCount,
                DirectoryCount = contents.DirectoryCount,
                LinkCount = contents.LinkCount,
                TotalBytes = contents.TotalBytes,
                ContentSha256 = contents.ContentSha256,
                ExcludedBuildOutput = plan.Excluded,
                IncludesBuildOutput = action.IncludeBuildOutput,
                Skipped = plan.Skipped,
                Remotes = state.Remotes,
                Branches = state.Branches.Select(b => new ArchivedBranch(b.Name, b.Sha.Length > 0 ? b.Sha : null)).ToList(),
                HeadBranch = state.Branch,
                HeadSha = state.HeadSha,
                Verdict = safety.Verdict.ToString(),
                Fingerprint = safety.Fingerprint,
                CreatedAt = _time.GetUtcNow(),
                DevDashVersion = MachineInfoProvider.AgentVersion,
            };
            WriteManifest(manifestPath, manifest);

            logger.LogWarning("Archived {Path} to {Archive} ({Files} files, {Bytes} bytes, sha256 {Sha}); moving the folder to {Trash}", path, archivePath, manifest.FileCount, manifest.TotalBytes, sha, trash.Name);
            var moved = trash.Move(path);
            if (!moved.Ok)
            {
                logger.LogWarning("Could not move {Path} to {Trash}: {Error}. Deleting the archive again.", path, trash.Name, moved.Error);
                return Fail(stopwatch, path, $"Archived, but could not move {path} to {trash.Name}: {moved.Error}. The archive was deleted again so that nothing is half done; the folder is as it was.");
            }

            finished = true;
            removals?.Record(path, state.Remotes.Select(r => r.Url), _time.GetUtcNow());
            var where = moved.Location is null || moved.Location == trash.Name ? trash.Name : $"{trash.Name} ({moved.Location})";
            List<string> lines =
            [
                $"Archived {path} to {archivePath} ({manifest.FileCount:N0} files, {Size(manifest.TotalBytes)}; the archive is {Size(manifest.ArchiveBytes)}).",
                $"Checked: read back {manifest.FileCount + manifest.DirectoryCount + manifest.LinkCount:N0} entries against the folder; SHA-256 {sha}.",
                plan.Excluded.Count > 0 ? $"Left out as regenerable build output: {string.Join(", ", plan.Excluded.Take(10))}{(plan.Excluded.Count > 10 ? $" and {plan.Excluded.Count - 10} more" : "")}." : "",
                plan.Skipped.Count > 0 ? $"Links not kept (a zip on Windows cannot hold them, or they lead out of the project): {string.Join(", ", plan.Skipped.Take(10))}." : "",
                $"Then moved {path} to {where}.",
                .. notes,
            ];
            return new ActionOutcome(true, 0, AgentActions.Truncate(string.Join('\n', lines.Where(l => l.Length > 0))), [path], stopwatch.ElapsedMilliseconds)
            {
                Archive = new ArchiveResult { ArchivePath = archivePath, ManifestPath = manifestPath, Manifest = manifest },
            };
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Fail(stopwatch, path, $"Archiving {path} took longer than {archive.TimeoutMinutes} minutes (Agent:Archive:TimeoutMinutes) and was stopped. The partial archive was deleted; the folder was not touched.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning("Archiving {Path} failed: {Error}", path, ex.Message);
            return Fail(stopwatch, path, $"Archiving {path} failed: {ex.Message} The partial archive was deleted; the folder was not touched.");
        }
        finally
        {
            if (!finished)
            {
                foreach (var file in (string[])[partial, archivePath, manifestPath])
                {
                    TryDelete(file);
                }
            }
        }
    }

    /// <summary>A build folder is only left out when git tracks nothing in it (asked from its parent, so a submodule answers for itself).</summary>
    private async Task<bool> UntrackedAsync(string parent, string name, CancellationToken ct)
    {
        var tracked = await git.RunAsync(parent, ["ls-files", "--", name], Local, ct).ConfigureAwait(false);
        return tracked.Ok && tracked.StdOut.Trim().Length == 0;
    }

    /// <summary><c>&lt;folder&gt;/&lt;name&gt;-&lt;yyyyMMdd-HHmmss&gt;.tar.gz</c> and its <c>.json</c>, with a counter when that is taken.</summary>
    internal static (string Archive, string Manifest) Names(string folder, string name, string format, DateTimeOffset now)
    {
        var stem = string.Create(CultureInfo.InvariantCulture, $"{name}-{now:yyyyMMdd-HHmmss}");
        var extension = ArchiveFormats.Extension(format);
        for (var i = 1; ; i++)
        {
            var candidate = i == 1 ? stem : string.Create(CultureInfo.InvariantCulture, $"{stem}-{i}");
            var archive = Path.Combine(folder, $"{candidate}.{extension}");
            var manifest = Path.Combine(folder, $"{candidate}.json");
            if (!Path.Exists(archive) && !Path.Exists(manifest) && !Path.Exists(archive + ".partial"))
            {
                return (archive, manifest);
            }
        }
    }

    /// <summary>The manifest beside an archive: the same name with <c>.json</c> in place of <c>.tar.gz</c> or <c>.zip</c>.</summary>
    internal static string ManifestPathOf(string archivePath)
    {
        foreach (var extension in (string[])[".tar.gz", ".zip"])
        {
            if (archivePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return archivePath[..^extension.Length] + ".json";
            }
        }

        return archivePath + ".json";
    }

    private static void WriteManifest(string path, ArchiveManifest manifest)
    {
        var temporary = path + ".partial";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, ManifestJson));
        File.Move(temporary, path, overwrite: false);
    }

    private static string? Mismatch(ArchiveContents contents, int files, int directories, int links, long bytes, string digest)
    {
        var problems = new List<string>();
        if (contents.FileCount != files)
        {
            problems.Add($"{contents.FileCount} files, expected {files}");
        }

        if (contents.DirectoryCount != directories)
        {
            problems.Add($"{contents.DirectoryCount} folders, expected {directories}");
        }

        if (contents.LinkCount != links)
        {
            problems.Add($"{contents.LinkCount} links, expected {links}");
        }

        if (contents.TotalBytes != bytes)
        {
            problems.Add($"{contents.TotalBytes} bytes, expected {bytes}");
        }

        if (problems.Count == 0 && contents.ContentSha256 != digest)
        {
            problems.Add("the contents differ");
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    internal static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a partial archive left behind has the .partial name, so it is never taken for a real one.
        }
    }

    // ---- restore --------------------------------------------------------------------------------

    private async Task<ActionOutcome> RestoreAsync(AgentAction action, Stopwatch stopwatch, Action<string>? progress, CancellationToken ct)
    {
        var target = action.RepoPath!;
        var archivePath = action.ArchivePath!;
        if (Folder(out var noFolder) is not { } folder)
        {
            return Refuse(stopwatch, noFolder!);
        }

        if (!Path.IsPathFullyQualified(archivePath) || PathSafety.HasDotSegments(archivePath)
            || !PathSafety.IsInside(PathSafety.Resolve(folder), PathSafety.Resolve(archivePath)))
        {
            return Refuse(stopwatch, $"{archivePath} is not in this machine's archive folder ({folder}).");
        }

        var file = new FileInfo(archivePath);
        if (!file.Exists || file.LinkTarget is not null)
        {
            return Refuse(stopwatch, $"the archive {archivePath} is missing. Nothing was restored.");
        }

        var manifestPath = ManifestPathOf(archivePath);
        ArchiveManifest? manifest;
        try
        {
            manifest = File.Exists(manifestPath) ? JsonSerializer.Deserialize<ArchiveManifest>(await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false), SnapshotSchema.JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Refuse(stopwatch, $"could not read the manifest {manifestPath}: {ex.Message}");
        }

        if (manifest is null || manifest.ArchiveFile != file.Name || manifest.Format is not (ArchiveFormats.TarGz or ArchiveFormats.Zip) || manifest.RootName.Length == 0)
        {
            return Refuse(stopwatch, $"{archivePath} has no manifest that describes it ({manifestPath}). Nothing was restored.");
        }

        if (!string.Equals(manifest.ArchiveSha256, action.ArchiveSha256, StringComparison.Ordinal))
        {
            return Refuse(stopwatch, $"the manifest of {archivePath} records a different checksum from the one the dashboard has. Nothing was restored.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.Archive.TimeoutMinutes)));
        // The archive is copied, and hashed as it is copied, into a folder only this user can open, and unpacked from that
        // copy: what is unpacked is exactly what was checked, even if the archive (on a share, say) changes meanwhile.
        // The manifest's machine is not compared with this one: the hub only restores onto the machine whose tombstone
        // it is, and the machine id defaults to the host name, which a rename would change and so strand every archive.
        progress?.Invoke($"Checking the checksum of {archivePath}.");
        DirectoryInfo privateCopy;
        try
        {
            privateCopy = Directory.CreateTempSubdirectory("devdash-restore-");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse(stopwatch, $"could not make a temporary folder for the archive: {ex.Message}");
        }

        try
        {
            var copy = Path.Combine(privateCopy.FullName, "archive");
            string sha;
            try
            {
                sha = await Task.Run(() => ProjectArchive.CopyAndHash(archivePath, copy, timeout.Token), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refuse(stopwatch, $"could not read {archivePath}: {ex.Message}");
            }

            if (sha != manifest.ArchiveSha256)
            {
                return Refuse(stopwatch, $"{archivePath} does not match its manifest's SHA-256 (it is damaged or was changed). Nothing was restored.");
            }

            return await UnpackAsync(action, stopwatch, progress, archivePath, copy, folder, manifest, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Fail(stopwatch, target, $"Restoring took longer than {options.CurrentValue.Archive.TimeoutMinutes} minutes and was stopped. Nothing was restored.");
        }
        finally
        {
            try
            {
                privateCopy.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not delete the temporary copy {Path}: {Error}", privateCopy.FullName, ex.Message);
            }
        }
    }

    /// <summary>Unpacks the checked private <paramref name="copy"/> of <paramref name="archivePath"/> beside the target and renames it into place.</summary>
    private async Task<ActionOutcome> UnpackAsync(
        AgentAction action, Stopwatch stopwatch, Action<string>? progress, string archivePath, string copy, string folder, ArchiveManifest manifest, CancellationToken ct)
    {
        var target = action.RepoPath!;

        // Unpacked beside the target under a dot name (the scanner skips those), checked, then renamed into place in one step.
        // Nothing in it is run, git included: the restored repository's own config is never read here.
        var parent = Path.GetDirectoryName(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        var temporary = Path.Combine(parent, $".devdash-restore-{Guid.NewGuid():N}");
        var done = false;
        try
        {
            logger.LogWarning("Restoring {Archive} into {Target}: asked for from the dashboard", archivePath, target);
            progress?.Invoke($"Unpacking {archivePath}.");
            var contents = await Task.Run(() => ProjectArchive.Extract(copy, manifest.Format, manifest.RootName, temporary, ct), ct).ConfigureAwait(false);
            if (Mismatch(contents, manifest.FileCount, manifest.DirectoryCount, manifest.LinkCount, manifest.TotalBytes, manifest.ContentSha256) is { } bad)
            {
                return Fail(stopwatch, target, $"{archivePath} did not unpack as its manifest says ({bad}). Nothing was restored.");
            }

            if (Path.Exists(target) || new FileInfo(target).LinkTarget is not null)
            {
                return Fail(stopwatch, target, $"Something appeared at {target} while the archive was unpacked. Nothing was restored.");
            }

            Directory.Move(temporary, target);
            done = true;
            return new ActionOutcome(true, 0, AgentActions.Truncate(
                $"Restored {archivePath} into {target} ({contents.FileCount:N0} files, {Size(contents.TotalBytes)}); every file checked against the manifest. The archive is still in {folder}."
                + (manifest.ExcludedBuildOutput.Count > 0 ? $"\nBuild output was not archived and needs rebuilding: {string.Join(", ", manifest.ExcludedBuildOutput.Take(10))}." : "")),
                [target], stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning("Restoring {Archive} failed: {Error}", archivePath, ex.Message);
            return Fail(stopwatch, target, $"Restoring {archivePath} failed: {ex.Message} Nothing was restored.");
        }
        finally
        {
            if (!done && Directory.Exists(temporary))
            {
                try
                {
                    // All of it came out of the archive a moment ago, and the archive is still there.
                    ProjectActions.DeleteTree(new DirectoryInfo(temporary));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Could not delete the unfinished restore {Path}: {Error}", temporary, ex.Message);
                }
            }
        }
    }
}
