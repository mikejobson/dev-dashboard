using DevDash.Contracts;

namespace DevDash.Agent.Scanning;

public sealed record TreeStats(DateTimeOffset? LastModified, SizeInfo? Size);

/// <summary>
/// Walks a folder without following symlinks. Always finds the newest source-file mtime; optionally
/// measures total size and the size of build/dependency folders that can be regenerated.
/// </summary>
public static class TreeWalker
{
    private static readonly HashSet<string> NoiseFiles = new(StringComparer.OrdinalIgnoreCase) { ".DS_Store", "Thumbs.db", "desktop.ini" };

    private const int Source = -1;
    private const int InsideGit = -2;

    public static TreeStats Walk(string root, bool measureSizes, DateTimeOffset now, CancellationToken ct = default)
    {
        DateTime? newest = null;
        long total = 0;
        var reclaimable = new List<ReclaimableDir>();

        // (directory, reclaimable bucket index or Source/InsideGit, counts towards mtime)
        var stack = new Stack<(string Dir, int Bucket, bool Source)>();
        stack.Push((root, Source, true));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, bucket, source) = stack.Pop();

            FileSystemInfo[] children;
            try
            {
                children = new DirectoryInfo(dir).GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var names = bucket == Source ? children.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

            foreach (var child in children)
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue; // never follow symlinks or junctions
                }

                if (child is FileInfo file)
                {
                    if (measureSizes)
                    {
                        long length;
                        try
                        {
                            length = file.Length;
                        }
                        catch (IOException)
                        {
                            continue;
                        }

                        total += length;
                        if (bucket >= 0)
                        {
                            reclaimable[bucket] = reclaimable[bucket] with { Bytes = reclaimable[bucket].Bytes + length };
                        }
                    }

                    if (source && !NoiseFiles.Contains(file.Name))
                    {
                        var time = file.LastWriteTimeUtc;
                        if (newest is null || time > newest)
                        {
                            newest = time;
                        }
                    }

                    continue;
                }

                if (bucket != -1)
                {
                    // Inside a build folder or .git: only sizes matter.
                    stack.Push((child.FullName, bucket, false));
                }
                else if (child.Name == ".git")
                {
                    if (measureSizes)
                    {
                        stack.Push((child.FullName, InsideGit, false));
                    }
                }
                else if (IsReclaimable(child.Name, names!))
                {
                    if (measureSizes)
                    {
                        reclaimable.Add(new ReclaimableDir { Path = child.FullName, Bytes = 0 });
                        stack.Push((child.FullName, reclaimable.Count - 1, false));
                    }
                }
                else
                {
                    stack.Push((child.FullName, Source, true));
                }
            }
        }

        SizeInfo? size = null;
        if (measureSizes)
        {
            var dirs = reclaimable.Where(r => r.Bytes > 0).OrderByDescending(r => r.Bytes).ToList();
            size = new SizeInfo
            {
                TotalBytes = total,
                ReclaimableBytes = dirs.Sum(d => d.Bytes),
                ReclaimableDirs = dirs.Take(25).ToList(),
                MeasuredAt = now,
            };
        }

        return new TreeStats(newest is null ? null : new DateTimeOffset(newest.Value, TimeSpan.Zero), size);
    }

    /// <summary>
    /// Build and dependency folders. Ambiguous names only count next to the manifest that produces them,
    /// so a tracked <c>bin/</c> of scripts or a hand-written <c>dist/</c> is left alone.
    /// </summary>
    internal static bool IsReclaimable(string name, IReadOnlySet<string> siblings)
    {
        switch (name.ToLowerInvariant())
        {
            case "node_modules":
            case ".venv":
            case ".next":
                return true;
            case "bin":
            case "obj":
                return siblings.Any(s => s.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || s.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || s.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase));
            case "target":
                return siblings.Contains("Cargo.toml") || siblings.Contains("pom.xml");
            case "dist":
                return siblings.Contains("package.json") || siblings.Contains("pyproject.toml") || siblings.Contains("setup.py");
            default:
                return false;
        }
    }
}
