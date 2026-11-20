using System.Runtime.InteropServices;
using DevDash.Contracts;

namespace DevDash.Agent.Actions;

/// <summary>
/// Archiving a project instead of removing it (<c>Agent:Archive</c>). Off while <see cref="Folder"/> is empty.
/// Like every action it also needs <c>Agent:Actions:Enabled</c>.
/// </summary>
public sealed class ArchiveOptions
{
    /// <summary>
    /// Where archives go: a local folder (<c>~/Archive</c>) or a mounted network share. <c>~</c> expands to the
    /// home folder. The agent never creates it: a share that is not mounted must not turn into a folder on the
    /// local disk under its mount point.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>
    /// <c>tar.gz</c> or <c>zip</c>. Empty picks tar.gz on macOS and Linux (keeps symbolic links and permissions)
    /// and zip on Windows (opens in Explorer; Windows has no executable bit to keep).
    /// </summary>
    public string? Format { get; set; }

    /// <summary>How long one archive or restore may take before it is abandoned (and the partial archive deleted).</summary>
    public int TimeoutMinutes { get; set; } = 180;

    /// <summary>Free space wanted on the archive volume beyond the project's size, before starting.</summary>
    public int MarginMegabytes { get; set; } = 512;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Folder);

    /// <summary>The folder, home expanded and made absolute. Null when not configured.</summary>
    public string? ResolvedFolder(string? home = null) =>
        IsConfigured ? Path.GetFullPath(AgentOptions.ExpandHome(Folder!.Trim(), home)) : null;

    public string ResolvedFormat =>
        Format?.Trim().ToLowerInvariant() switch
        {
            "zip" => ArchiveFormats.Zip,
            "tar.gz" or "tgz" or "tar" => ArchiveFormats.TarGz,
            _ => OperatingSystem.IsWindows() ? ArchiveFormats.Zip : ArchiveFormats.TarGz,
        };

    public long MarginBytes => Math.Max(0, MarginMegabytes) * 1024L * 1024L;
}

/// <summary>What the snapshot says about archiving here, worked out from the options and the folder itself.</summary>
public static class ArchiveSetup
{
    /// <summary>Null when archiving is not configured. Writes and deletes one small probe file in the folder.</summary>
    public static ArchiveInfo? Describe(AgentOptions options, Func<string, long?>? freeSpace = null)
    {
        if (options.Archive.ResolvedFolder() is not { } folder)
        {
            return null;
        }

        var info = new ArchiveInfo { Folder = folder, Format = options.Archive.ResolvedFormat, MarginBytes = options.Archive.MarginBytes };
        if (!Directory.Exists(folder))
        {
            return info with { Problem = $"{folder} does not exist (a network share that is not mounted?)" };
        }

        var writable = CanWrite(folder, out var why);
        var free = (freeSpace ?? DiskSpace.Available)(folder);
        return info with
        {
            Available = true,
            Writable = writable,
            FreeBytes = free,
            Problem = writable ? null : $"{folder} cannot be written to: {why}",
        };
    }

    /// <summary>True when a file can be created and deleted in the folder.</summary>
    public static bool CanWrite(string folder, out string? why)
    {
        why = null;
        var probe = Path.Combine(folder, $".devdash-probe-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // DeleteOnClose has already removed it, or it was never made.
            }
        }
    }
}

/// <summary>Free space on the volume that holds a folder, including a network share.</summary>
public static class DiskSpace
{
    public static long? Available(string folder)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // GetDiskFreeSpaceEx takes any folder, a UNC share included (with a trailing separator).
                var path = folder.EndsWith('\\') ? folder : folder + "\\";
                return GetDiskFreeSpaceExW(path, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;
            }

            // On macOS and Linux this is statvfs on the folder itself, so a mounted share reports its own space.
            return new DriveInfo(folder).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
#pragma warning disable SYSLIB1054 // LibraryImport would need unsafe code switched on for the whole project
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
#pragma warning restore SYSLIB1054
}
