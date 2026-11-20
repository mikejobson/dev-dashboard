using System.Runtime.InteropServices;
using System.Text;

namespace DevDash.Agent.Scanning;

/// <summary>
/// The one way the scanner reads a file that a repository supplies (README, manifests, pin files,
/// <c>obj/project.assets.json</c>). A repository is someone else's content: a <c>README.md</c> that is a
/// link to <c>~/.ssh/id_ed25519</c> would otherwise put a key into the snapshot, and one that is a FIFO
/// would block the scan for ever. So only a plain, non-empty regular file is read, reached without
/// following a link anywhere below the repository's own folder, and only up to a size cap.
/// </summary>
public static class RepoFile
{
    /// <summary>
    /// The first <paramref name="maxBytes"/> bytes of the file as UTF-8 (byte order mark dropped), or null
    /// when it is missing, not a safe regular file inside <paramref name="root"/>, or unreadable.
    /// </summary>
    public static string? ReadText(string root, string file, int maxBytes)
    {
        var bytes = Read(root, file, maxBytes, truncate: true);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes).TrimStart('﻿');
    }

    /// <summary>The whole file, or null when it is larger than <paramref name="maxBytes"/> or not safe to read (see <see cref="ReadText"/>).</summary>
    public static byte[]? ReadAll(string root, string file, int maxBytes) => Read(root, file, maxBytes, truncate: false);

    /// <summary>
    /// True when <paramref name="file"/> is a non-empty regular file under <paramref name="root"/> and
    /// neither it nor any folder between it and <paramref name="root"/> is a link (symlink, junction).
    /// </summary>
    public static bool IsSafe(string root, string file)
    {
        try
        {
            var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var full = Path.GetFullPath(file);
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (!full.StartsWith(top + Path.DirectorySeparatorChar, comparison))
            {
                return false;
            }

            var info = new FileInfo(full);
            // A FIFO, socket or device reports no length; so does an empty file, which has nothing to say.
            if (!info.Exists || info.LinkTarget is not null || info.Length <= 0 || !IsRegular(full))
            {
                return false;
            }

            for (var dir = info.Directory; dir is not null && !string.Equals(Path.TrimEndingDirectorySeparator(dir.FullName), top, comparison); dir = dir.Parent)
            {
                if (dir.LinkTarget is not null)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static byte[]? Read(string root, string file, int maxBytes, bool truncate)
    {
        if (!IsSafe(root, file))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            if (stream.Length > maxBytes && !truncate)
            {
                return null;
            }

            var buffer = new byte[(int)Math.Min(stream.Length, maxBytes)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // ---- file type ------------------------------------------------------------------------------

    /// <summary>
    /// On macOS and Linux, whether <c>lstat</c> calls it a regular file. .NET has no public way to tell a
    /// FIFO or device from a file (both are "Normal" with length 0), so this asks the runtime's own
    /// native shim, which the base library uses for the same purpose. Should that ever be missing, the
    /// length check in <see cref="IsSafe"/> still keeps FIFOs and devices out.
    /// </summary>
    private static bool IsRegular(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true; // no FIFOs in the file system; links were ruled out by LinkTarget
        }

        try
        {
            return LStat(path, out var status) == 0 && (status.Mode & FileTypeMask) == RegularFile;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }

    private const int FileTypeMask = 0xF000;
    private const int RegularFile = 0x8000;

    /// <summary>The start of System.Native's <c>FileStatus</c>; only <see cref="Mode"/> is used. Sized generously for the rest.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct FileStatus
    {
        public int Flags;
        public int Mode;
    }

#pragma warning disable CA2101, SYSLIB1054 // UTF-8 is given explicitly; LibraryImport would need unsafe code switched on for the whole project
    [DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out FileStatus output);
#pragma warning restore CA2101, SYSLIB1054
}
