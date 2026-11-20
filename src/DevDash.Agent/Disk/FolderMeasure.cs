namespace DevDash.Agent.Disk;

/// <param name="Complete">False when the walk was stopped by its time limit: the size is then a lower bound.</param>
public sealed record FolderSize(long Bytes, DateTimeOffset? NewestWrite, bool Complete);

/// <summary>
/// Adds up a folder's files. Links are counted as links and never followed (a cache that links into another
/// would otherwise be counted twice, or forever), unreadable folders are skipped, and the walk stops when its
/// token is cancelled, reporting what it had so far.
/// </summary>
public static class FolderMeasure
{
    public static FolderSize Measure(string root, CancellationToken ct)
    {
        long bytes = 0;
        DateTime? newest = null;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        var pending = new Stack<string>();
        pending.Push(root);
        var seen = 0;
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            try
            {
                foreach (var child in children)
                {
                    if ((++seen & 0x3FF) == 0 && ct.IsCancellationRequested)
                    {
                        return new FolderSize(bytes, Stamp(newest), false);
                    }

                    var write = child.LastWriteTimeUtc;
                    if (newest is null || write > newest)
                    {
                        newest = write;
                    }

                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (child is FileInfo file)
                    {
                        bytes += file.Length;
                    }
                    else if (child is DirectoryInfo sub)
                    {
                        pending.Push(sub.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that vanished or became unreadable part way: count what was read.
            }
        }

        return new FolderSize(bytes, Stamp(newest), !ct.IsCancellationRequested);
    }

    private static DateTimeOffset? Stamp(DateTime? utc) =>
        utc is { } value && value.Year > 1980 ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : null;
}
