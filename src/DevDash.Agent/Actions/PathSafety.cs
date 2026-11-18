namespace DevDash.Agent.Actions;

/// <summary>Path checks for actions: where a path really is, and whether that is inside a scan root.</summary>
public static class PathSafety
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The path with every symbolic link along it followed, so that two spellings of one folder compare
    /// equal and a link inside a scan root cannot stand in for a folder outside it. Works for paths
    /// that do not exist (the missing tail is kept as written).
    /// </summary>
    public static string Resolve(string path) => Resolve(path, depth: 0);

    public static bool SamePath(string a, string b) => string.Equals(Trim(a), Trim(b), Comparison);

    /// <summary>True when <paramref name="path"/> is strictly below <paramref name="root"/>. Both must already be resolved.</summary>
    public static bool IsInside(string root, string path)
    {
        var prefix = Trim(root) + Path.DirectorySeparatorChar;
        return Trim(path).StartsWith(prefix, Comparison);
    }

    /// <summary>The segments of <paramref name="path"/> below <paramref name="root"/>.</summary>
    public static string[] SegmentsBelow(string root, string path) =>
        Trim(path)[Trim(root).Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when a path contains <c>.</c> or <c>..</c> segments, which no path the agent reported ever does.</summary>
    public static bool HasDotSegments(string path) =>
        path.Split('/', '\\').Any(segment => segment is "." or "..");

    private static string Trim(string path) =>
        path.Length > 1 && Path.GetPathRoot(path) != path ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;

    private static string Resolve(string path, int depth)
    {
        var full = Path.GetFullPath(path);
        if (depth > 8)
        {
            return full;
        }

        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    // The target may itself sit under a linked folder.
                    current = Resolve(target.FullName, depth + 1);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable or a link loop: keep the path as written. It then fails the later checks on its own.
            }
        }

        return current;
    }
}
