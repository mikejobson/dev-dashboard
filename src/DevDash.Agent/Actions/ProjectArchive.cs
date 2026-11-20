using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DevDash.Contracts;
using DevDash.Core;

namespace DevDash.Agent.Actions;

internal enum ArchiveEntryKind
{
    Directory,
    File,
    Link,
}

/// <summary>One thing to put in an archive, or found in one. <see cref="Name"/> is <c>/</c>-separated and starts with the project's folder name.</summary>
internal sealed record ArchiveItem(string Name, ArchiveEntryKind Kind, long Size, string? LinkTarget, UnixFileMode Mode, DateTimeOffset Modified)
{
    /// <summary>Where it is on disk, when it came from a walk.</summary>
    public string? Source { get; init; }
}

/// <summary>What a walk of a project folder found: what goes in the archive, and what was left out.</summary>
internal sealed record ArchivePlan(string Root, string RootName, IReadOnlyList<ArchiveItem> Items, IReadOnlyList<string> Excluded, IReadOnlyList<string> Skipped)
{
    public int FileCount => Items.Count(i => i.Kind == ArchiveEntryKind.File);
    public int DirectoryCount => Items.Count(i => i.Kind == ArchiveEntryKind.Directory);
    public int LinkCount => Items.Count(i => i.Kind == ArchiveEntryKind.Link);
    public long TotalBytes => Items.Where(i => i.Kind == ArchiveEntryKind.File).Sum(i => i.Size);
}

/// <summary>What reading an archive back (or unpacking it) found.</summary>
internal sealed record ArchiveContents(int FileCount, int DirectoryCount, int LinkCount, long TotalBytes, string ContentSha256);

/// <summary>Bytes and files done so far, for progress messages.</summary>
internal readonly record struct ArchiveProgress(int Files, long Bytes);

/// <summary>
/// Writing, reading back and unpacking project archives. tar.gz (PAX) keeps symbolic links as links and the
/// permission bits, including the executable bit; zip is used on Windows, where neither exists in that form.
/// Links are never followed, in either direction: a link is stored as a link, and unpacking creates links last,
/// so no file is ever written through one. Only links that stay inside the project are kept or unpacked
/// (<see cref="LinkProblem"/>), and nothing may be listed below a link.
/// </summary>
internal static class ProjectArchive
{
    private const int UnixTypeMask = 0xF000, UnixDirectory = 0x4000, UnixFile = 0x8000, UnixLink = 0xA000;

    private static readonly UnixFileMode DefaultFileMode = (UnixFileMode)Convert.ToInt32("644", 8);
    private static readonly UnixFileMode DefaultDirectoryMode = (UnixFileMode)Convert.ToInt32("755", 8);
    private static readonly UnixFileMode LinkMode = (UnixFileMode)Convert.ToInt32("777", 8);

    /// <summary>
    /// Walks <paramref name="root"/> without following any link. A folder is left out when
    /// <paramref name="exclude"/> says so (asked only about folders that are build output by name, never inside
    /// <c>.git</c>). On Windows a link cannot go in a zip, so it is listed in <see cref="ArchivePlan.Skipped"/>; so is
    /// a link that leads out of the project (absolute, or up past its folder), which a restore would refuse.
    /// </summary>
    public static async Task<ArchivePlan> PlanAsync(
        string root, Func<string, string, CancellationToken, Task<bool>>? exclude, bool storeLinks, CancellationToken ct)
    {
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists || rootInfo.LinkTarget is not null)
        {
            throw new IOException($"{root} is not a folder.");
        }

        var rootName = Trash.NameOf(root);
        var items = new List<ArchiveItem> { Item(rootInfo, rootName, ArchiveEntryKind.Directory) };
        var excluded = new List<string>();
        var skipped = new List<string>();
        var rootNames = Names(rootInfo);
        await WalkAsync(rootInfo, rootName, "", insideGit: false).ConfigureAwait(false);
        return new ArchivePlan(root, rootName, items, excluded, skipped);

        async Task WalkAsync(DirectoryInfo dir, string name, string relative, bool insideGit)
        {
            ct.ThrowIfCancellationRequested();
            var children = dir.EnumerateFileSystemInfos().OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            HashSet<string>? siblings = null;
            IReadOnlySet<string> Siblings() => siblings ??= children.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var child in children)
            {
                var childName = name + "/" + child.Name;
                var childRelative = relative.Length == 0 ? child.Name : relative + "/" + child.Name;
                if (child.LinkTarget is not null)
                {
                    if (storeLinks && LinkProblem(childRelative, child.LinkTarget) is { } leaves)
                    {
                        skipped.Add($"{childRelative} -> {child.LinkTarget} ({leaves})");
                    }
                    else if (storeLinks)
                    {
                        items.Add(Item(child, childName, ArchiveEntryKind.Link));
                    }
                    else
                    {
                        skipped.Add(childRelative);
                    }

                    continue;
                }

                if (child is DirectoryInfo sub)
                {
                    var git = insideGit || child.Name.Equals(".git", StringComparison.OrdinalIgnoreCase);
                    if (!git && exclude is not null && IsBuildOutputName(child.Name, Siblings, () => rootNames)
                        && await exclude(dir.FullName, child.Name, ct).ConfigureAwait(false))
                    {
                        excluded.Add(childRelative);
                        continue;
                    }

                    items.Add(Item(sub, childName, ArchiveEntryKind.Directory));
                    await WalkAsync(sub, childName, childRelative, git).ConfigureAwait(false);
                    continue;
                }

                items.Add(Item(child, childName, ArchiveEntryKind.File));
            }
        }
    }

    /// <summary>A folder name that is regenerable build output or a tool cache by the scanner's own lists.</summary>
    internal static bool IsBuildOutputName(string name, Func<IReadOnlySet<string>> siblings, Func<IReadOnlySet<string>> repoRoot) =>
        BuildOutput.IsReclaimable(name, siblings) || BuildOutput.IsRecreated(name, siblings, repoRoot) || BuildOutput.IsCache(name);

    private static HashSet<string> Names(DirectoryInfo dir) =>
        dir.EnumerateFileSystemInfos().Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static ArchiveItem Item(FileSystemInfo info, string name, ArchiveEntryKind kind)
    {
        // A link's own mode and time are not kept (asking for them can reach its target, which may not exist).
        if (kind == ArchiveEntryKind.Link)
        {
            return new ArchiveItem(name, kind, 0, info.LinkTarget, LinkMode, DateTimeOffset.UnixEpoch) { Source = info.FullName };
        }

        var mode = OperatingSystem.IsWindows() ? (kind == ArchiveEntryKind.Directory ? DefaultDirectoryMode : DefaultFileMode) : info.UnixFileMode;
        return new ArchiveItem(
            name,
            kind,
            kind == ArchiveEntryKind.File ? ((FileInfo)info).Length : 0,
            null,
            mode,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)) { Source = info.FullName };
    }

    /// <summary>
    /// Names and sizes (and link targets, and modification times) that differ between two walks of the same
    /// folder: anything written while it was being archived. Empty when nothing changed.
    /// </summary>
    public static IReadOnlyList<string> Differences(ArchivePlan before, ArchivePlan after)
    {
        var then = before.Items.ToDictionary(i => i.Name, StringComparer.Ordinal);
        var now = after.Items.ToDictionary(i => i.Name, StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (var (name, item) in now)
        {
            if (!then.TryGetValue(name, out var old))
            {
                changed.Add($"{name} (new)");
            }
            else if (old.Kind != item.Kind || old.Size != item.Size || old.LinkTarget != item.LinkTarget
                || (item.Kind == ArchiveEntryKind.File && old.Modified != item.Modified))
            {
                changed.Add($"{name} (changed)");
            }
        }

        changed.AddRange(then.Keys.Where(name => !now.ContainsKey(name)).Select(name => $"{name} (gone)"));
        changed.AddRange(after.Excluded.Except(before.Excluded, StringComparer.Ordinal).Select(name => $"{name} (new build output)"));
        return changed;
    }

    // ---- writing ------------------------------------------------------------------------------------

    /// <summary>
    /// Writes every item of <paramref name="plan"/> to a new file (it must not exist) and returns the content
    /// digest of what was written. Throws when a file's size is not what the walk saw: it changed under us.
    /// </summary>
    public static string Write(ArchivePlan plan, string file, string format, Action<ArchiveProgress>? progress, CancellationToken ct)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            if (format == ArchiveFormats.Zip)
            {
                WriteZip(plan, stream, digest, progress, ct);
            }
            else
            {
                WriteTar(plan, stream, digest, progress, ct);
            }

            stream.Flush(flushToDisk: true);
        }

        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static void WriteTar(ArchivePlan plan, Stream stream, IncrementalHash digest, Action<ArchiveProgress>? progress, CancellationToken ct)
    {
        using var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
        var done = new ArchiveProgress(0, 0);
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            switch (item.Kind)
            {
                case ArchiveEntryKind.Directory:
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, item.Name + "/") { Mode = item.Mode, ModificationTime = item.Modified });
                    Add(digest, item, null);
                    break;
                case ArchiveEntryKind.Link:
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, item.Name) { LinkName = item.LinkTarget!, Mode = item.Mode, ModificationTime = item.Modified });
                    Add(digest, item, null);
                    break;
                default:
                    using (var data = ExactFile.Open(item))
                    {
                        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, item.Name) { DataStream = data, Mode = item.Mode, ModificationTime = item.Modified });
                        Add(digest, item, data.Finish());
                    }

                    done = new ArchiveProgress(done.Files + 1, done.Bytes + item.Size);
                    progress?.Invoke(done);
                    break;
            }
        }
    }

    private static void WriteZip(ArchivePlan plan, Stream stream, IncrementalHash digest, Action<ArchiveProgress>? progress, CancellationToken ct)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        var done = new ArchiveProgress(0, 0);
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            switch (item.Kind)
            {
                case ArchiveEntryKind.Directory:
                    {
                        var entry = zip.CreateEntry(item.Name + "/", CompressionLevel.NoCompression);
                        entry.LastWriteTime = ZipTime(item.Modified);
                        entry.ExternalAttributes = UnixAttributes(UnixDirectory, item.Mode) | 0x10; // and the MS-DOS directory bit
                        Add(digest, item, null);
                        break;
                    }

                case ArchiveEntryKind.Link:
                    {
                        // The Info-ZIP convention: the link's target is its content, and the Unix mode says it is a link.
                        var entry = zip.CreateEntry(item.Name, CompressionLevel.NoCompression);
                        entry.ExternalAttributes = UnixAttributes(UnixLink, item.Mode);
                        using (var output = entry.Open())
                        {
                            output.Write(Encoding.UTF8.GetBytes(item.LinkTarget!));
                        }

                        Add(digest, item, null);
                        break;
                    }

                default:
                    {
                        var entry = zip.CreateEntry(item.Name, CompressionLevel.Optimal);
                        entry.LastWriteTime = ZipTime(item.Modified);
                        entry.ExternalAttributes = UnixAttributes(UnixFile, item.Mode);
                        using (var data = ExactFile.Open(item))
                        {
                            using (var output = entry.Open())
                            {
                                data.CopyTo(output);
                            }

                            Add(digest, item, data.Finish());
                        }

                        done = new ArchiveProgress(done.Files + 1, done.Bytes + item.Size);
                        progress?.Invoke(done);
                        break;
                    }
            }
        }
    }

    private static int UnixAttributes(int type, UnixFileMode mode) => (int)((uint)(type | ((int)mode & 0xFFF)) << 16);

    /// <summary>Zip keeps MS-DOS times, 1980 to 2107.</summary>
    private static DateTimeOffset ZipTime(DateTimeOffset time)
    {
        var low = new DateTimeOffset(1980, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var high = new DateTimeOffset(2107, 12, 30, 0, 0, 0, TimeSpan.Zero);
        return time < low ? low : time > high ? high : time;
    }

    /// <summary>One entry's line in the content digest.</summary>
    private static void Add(IncrementalHash digest, ArchiveItem item, string? contentSha)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{item.Name}\0{(int)item.Kind}\0{(item.Kind == ArchiveEntryKind.File ? item.Size : 0)}\0{item.LinkTarget}\0{contentSha}\n");
        digest.AppendData(Encoding.UTF8.GetBytes(line));
    }

    // ---- reading back and unpacking ----------------------------------------------------------------

    /// <summary>Reads every entry of an archive and its data, and returns what it holds. Throws for anything this code would not have written.</summary>
    public static ArchiveContents Read(string file, string format, string rootName, CancellationToken ct) =>
        Walk(file, format, rootName, destination: null, ct);

    /// <summary>
    /// Unpacks an archive into <paramref name="destination"/>, which must not exist. Entries go in under their
    /// names below <paramref name="rootName"/>; anything that would land outside, or is not a folder, file or
    /// link, stops it. Links are made last. Returns what it unpacked.
    /// </summary>
    public static ArchiveContents Extract(string file, string format, string rootName, string destination, CancellationToken ct)
    {
        if (Path.Exists(destination))
        {
            throw new IOException($"{destination} already exists.");
        }

        Directory.CreateDirectory(destination);
        return Walk(file, format, rootName, destination, ct);
    }

    private static ArchiveContents Walk(string file, string format, string rootName, string? destination, CancellationToken ct)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int files = 0, directories = 0, links = 0;
        long bytes = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Every link's name, and every folder some entry is below. Nothing may be below a link, whichever comes first
        // in the archive: unpacking it would write through the link. Without case on the file systems that ignore it.
        var nameComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var linkNames = new HashSet<string>(nameComparer);
        var parents = new HashSet<string>(nameComparer);
        var folders = new List<(string Path, UnixFileMode Mode, DateTimeOffset Modified)>();
        var pendingLinks = new List<(string Path, string Target)>();

        void Visit(string rawName, ArchiveEntryKind kind, long size, string? linkTarget, UnixFileMode mode, DateTimeOffset modified, Func<Stream?> open)
        {
            ct.ThrowIfCancellationRequested();
            var name = rawName.TrimEnd('/');
            var relative = Relative(name, rootName);
            if (!seen.Add(name))
            {
                throw new InvalidDataException($"The archive lists {name} twice.");
            }

            for (var cut = relative.LastIndexOf('/'); cut > 0; cut = relative.LastIndexOf('/', cut - 1))
            {
                var parent = relative[..cut];
                if (linkNames.Contains(parent))
                {
                    throw new InvalidDataException($"{name} is listed below {rootName}/{parent}, which is a link.");
                }

                if (!parents.Add(parent))
                {
                    break; // its own parents were added with it
                }
            }

            if (kind == ArchiveEntryKind.Link)
            {
                if (relative.Length == 0 || parents.Contains(relative))
                {
                    throw new InvalidDataException($"{name} is a link, and the archive lists entries below it.");
                }

                if (LinkProblem(relative, linkTarget) is { } problem)
                {
                    throw new InvalidDataException($"{name} is a link to {linkTarget}, which {problem}.");
                }

                linkNames.Add(relative);
            }

            var target = destination is null ? null : relative.Length == 0 ? destination : Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            string? contentSha = null;
            switch (kind)
            {
                case ArchiveEntryKind.Directory:
                    directories++;
                    if (target is not null)
                    {
                        Directory.CreateDirectory(target);
                        folders.Add((target, mode, modified));
                    }

                    break;
                case ArchiveEntryKind.Link:
                    links++;
                    if (target is not null)
                    {
                        pendingLinks.Add((target, linkTarget!));
                    }

                    break;
                default:
                    files++;
                    bytes += size;
                    using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        using var data = open();
                        FileStream? output = null;
                        if (target is not null)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                        }

                        long count = 0;
                        using (output)
                        {
                            if (data is not null)
                            {
                                var buffer = new byte[1 << 16];
                                int read;
                                while ((read = data.Read(buffer)) > 0)
                                {
                                    hash.AppendData(buffer, 0, read);
                                    output?.Write(buffer, 0, read);
                                    count += read;
                                }
                            }
                        }

                        if (count != size)
                        {
                            throw new InvalidDataException($"{name} holds {count} bytes, but its entry says {size}.");
                        }

                        contentSha = Convert.ToHexStringLower(hash.GetHashAndReset());
                    }

                    if (target is not null)
                    {
                        File.SetLastWriteTimeUtc(target, modified.UtcDateTime);
                        if (!OperatingSystem.IsWindows())
                        {
                            File.SetUnixFileMode(target, mode);
                        }
                    }

                    break;
            }

            Add(digest, new ArchiveItem(name, kind, size, linkTarget, mode, modified), contentSha);
        }

        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        {
            if (format == ArchiveFormats.Zip)
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
                foreach (var entry in zip.Entries)
                {
                    var unix = (entry.ExternalAttributes >> 16) & 0xFFFF;
                    var mode = (UnixFileMode)(unix & 0xFFF);
                    if (entry.FullName.EndsWith('/'))
                    {
                        Visit(entry.FullName, ArchiveEntryKind.Directory, 0, null, mode == 0 ? DefaultDirectoryMode : mode, entry.LastWriteTime, () => null);
                    }
                    else if ((unix & UnixTypeMask) == UnixLink)
                    {
                        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                        Visit(entry.FullName, ArchiveEntryKind.Link, 0, reader.ReadToEnd(), mode, entry.LastWriteTime, () => null);
                    }
                    else
                    {
                        Visit(entry.FullName, ArchiveEntryKind.File, entry.Length, null, mode == 0 ? DefaultFileMode : mode, entry.LastWriteTime, entry.Open);
                    }
                }
            }
            else
            {
                using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
                using var tar = new TarReader(gzip, leaveOpen: true);
                while (tar.GetNextEntry(copyData: false) is { } entry)
                {
                    var kind = entry.EntryType switch
                    {
                        TarEntryType.Directory => ArchiveEntryKind.Directory,
                        TarEntryType.SymbolicLink => ArchiveEntryKind.Link,
                        TarEntryType.RegularFile or TarEntryType.V7RegularFile => ArchiveEntryKind.File,
                        _ => throw new InvalidDataException($"The archive holds {entry.Name}, a {entry.EntryType}, which DevDash never writes."),
                    };
                    Visit(entry.Name, kind, kind == ArchiveEntryKind.File ? entry.Length : 0, kind == ArchiveEntryKind.Link ? entry.LinkName : null, entry.Mode, entry.ModificationTime, () => entry.DataStream);
                }
            }
        }

        if (destination is not null)
        {
            // Links last, so nothing above was written through one. A link is made as it was: its target is not looked at
            // (it was checked to stay inside the project). Nothing on the way to it may be a link either, whatever put it there.
            foreach (var (path, target) in pendingLinks)
            {
                RefuseLinkedParents(destination, path);
                File.CreateSymbolicLink(path, target);
            }

            // Folders' own times and modes after their contents, deepest first: a read-only folder would have stopped its files being written.
            foreach (var (path, mode, modified) in folders.OrderByDescending(f => f.Path.Length))
            {
                Directory.SetLastWriteTimeUtc(path, modified.UtcDateTime);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, mode);
                }
            }
        }

        return new ArchiveContents(files, directories, links, bytes, Convert.ToHexStringLower(digest.GetHashAndReset()));
    }

    /// <summary>The part of an entry's name below the project folder; refuses anything that is not plainly inside it.</summary>
    internal static string Relative(string name, string rootName)
    {
        if (name == rootName)
        {
            return "";
        }

        if (!name.StartsWith(rootName + "/", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{name} is not inside {rootName}/.");
        }

        var relative = name[(rootName.Length + 1)..];
        var segments = relative.Split('/');
        if (segments.Any(s => s.Length == 0 || s is "." or ".." || s.Contains('\\') || s.Contains('\0') || (OperatingSystem.IsWindows() && s.Contains(':'))))
        {
            throw new InvalidDataException($"{name} is not a plain name inside {rootName}/.");
        }

        return relative;
    }

    /// <summary>
    /// Why a link at <paramref name="relative"/> (below the project folder, <c>/</c>-separated) pointing at
    /// <paramref name="target"/> could not be unpacked, or null when it stays inside the project. The target must be
    /// relative, and in the plain form <c>../../a/b</c>: any <c>..</c> first, and no further up than the link's own
    /// folder is deep. A <c>..</c> after a name is refused because that name may itself be a link
    /// (<c>x -&gt; .</c> then <c>y -&gt; x/..</c> leaves the folder although each looks inside it on paper).
    /// </summary>
    internal static string? LinkProblem(string relative, string? target)
    {
        if (string.IsNullOrEmpty(target) || target.Contains('\0'))
        {
            return "is empty or not a plain path";
        }

        if (target[0] is '/' or '\\' || Path.IsPathRooted(target) || (OperatingSystem.IsWindows() && target.Contains(':')))
        {
            return "is an absolute path, outside the project";
        }

        char[] separators = OperatingSystem.IsWindows() ? ['/', '\\'] : ['/'];
        var depth = relative.Count(c => c == '/'); // folders between the project folder and the link
        var named = false;
        foreach (var segment in target.Split(separators))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (named)
                {
                    return "goes up after going down (only ../../name forms are unpacked)";
                }

                if (--depth < 0)
                {
                    return "leads out of the project";
                }

                continue;
            }

            named = true;
        }

        return null;
    }

    /// <summary>Refuses when any folder between <paramref name="root"/> and <paramref name="path"/> is a link (asked of the link itself, never followed).</summary>
    internal static void RefuseLinkedParents(string root, string path)
    {
        var relative = Path.GetRelativePath(root, Path.GetDirectoryName(path)!);
        if (relative == ".")
        {
            return;
        }

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"{path} is not inside {root}.");
        }

        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException($"{current} is a link; nothing is unpacked through one.");
            }

            if (!info.Exists)
            {
                throw new InvalidDataException($"{current} is not a folder.");
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="copy"/> (which must not exist; only this user may read
    /// or write it) and returns the SHA-256 of exactly the bytes copied, so what is then unpacked from the copy is
    /// what was checked, whatever happens to the source meanwhile.
    /// </summary>
    public static string CopyAndHash(string source, string copy, CancellationToken ct)
    {
        var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 1 << 16 };
        if (!OperatingSystem.IsWindows())
        {
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var output = new FileStream(copy, create);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }

        output.Flush();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>SHA-256 of a whole file, lower-case hex.</summary>
    public static string Sha256Of(string file, CancellationToken ct)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// A file read for an archive: exactly the size the walk saw, hashed as it goes. Shorter or longer than
    /// that means it changed while being archived, and the archive is abandoned.
    /// </summary>
    private sealed class ExactFile(FileStream inner, ArchiveItem item) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _position;

        public static ExactFile Open(ArchiveItem item) =>
            new(new FileStream(item.Source!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16), item);

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => item.Size;

        public override long Position
        {
            get => _position;
            set
            {
                if (value != _position)
                {
                    throw new NotSupportedException("Archive data is read once, in order.");
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var wanted = (int)Math.Min(buffer.Length, item.Size - _position);
            if (wanted <= 0)
            {
                return 0;
            }

            var read = inner.Read(buffer[..wanted]);
            if (read == 0)
            {
                throw new IOException($"{item.Source} got shorter while it was being archived.");
            }

            _hash.AppendData(buffer[..read]);
            _position += read;
            return read;
        }

        /// <summary>The SHA-256 of what was read. Throws when the file has more in it than the walk saw.</summary>
        public string Finish()
        {
            if (_position != item.Size || inner.ReadByte() != -1)
            {
                throw new IOException($"{item.Source} changed size while it was being archived.");
            }

            return Convert.ToHexStringLower(_hash.GetHashAndReset());
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => item.Size + offset,
            };
            Position = target;
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
