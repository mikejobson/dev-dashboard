using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DevDash.Agent.Actions;

/// <summary>How moving a folder to the trash went.</summary>
/// <param name="Location">Where the folder is now, when that is known.</param>
public sealed record TrashResult(bool Ok, string? Location, string? Error)
{
    public static TrashResult Moved(string? location) => new(true, location, null);

    public static TrashResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Where a removed project goes. Every implementation moves the folder and nothing else: there is no
/// copy-then-delete and no delete. When a folder cannot be moved to the trash the answer is a failure,
/// and the folder stays where it was.
/// </summary>
public interface ITrash
{
    /// <summary>What to call it in a message: "the Trash", "the Recycle Bin".</summary>
    string Name { get; }

    TrashResult Move(string path);
}

public static class Trash
{
    /// <summary>
    /// The trash this machine's agent uses: the operating system's own, or the folder named by
    /// <c>Agent:Actions:TrashDirectory</c> when that is set.
    /// </summary>
    public static ITrash ForThisMachine(ActionsOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.TrashDirectory))
        {
            return new FolderTrash(Path.GetFullPath(AgentOptions.ExpandHome(options.TrashDirectory.Trim())));
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacTrash();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsRecycleBin();
        }

        return OperatingSystem.IsLinux() ? new FreedesktopTrash() : new NoTrash();
    }

    /// <summary>The folder's own name, which is what it is called in the trash.</summary>
    internal static string NameOf(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : "folder";

    /// <summary>
    /// Names to try in turn for a folder in a trash: its own, then its own with the time, then with a
    /// counter as well. Two projects with one name can both be in the trash, and neither replaces the other.
    /// </summary>
    internal static IEnumerable<string> Candidates(string name, DateTimeOffset now)
    {
        yield return name;
        var stamped = string.Create(CultureInfo.InvariantCulture, $"{name} {now.ToLocalTime():yyyy-MM-dd HH.mm.ss}");
        yield return stamped;
        for (var i = 2; i < 100; i++)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"{stamped} {i}");
        }
    }

    /// <summary>The mount point that holds <paramref name="path"/>: the longest one that is a prefix of it. Null when none is.</summary>
    internal static string? MountOf(string path)
    {
        var resolved = PathSafety.Resolve(path);
        string? best = null;
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                var mount = drive.Name;
                if ((PathSafety.SamePath(mount, resolved) || PathSafety.IsInside(mount, resolved) || mount == "/") && (best is null || mount.Length > best.Length))
                {
                    best = mount;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return best;
    }
}

/// <summary>For an operating system the agent has no trash for: removal is refused there.</summary>
public sealed class NoTrash : ITrash
{
    public string Name => "the trash";

    public TrashResult Move(string path) => TrashResult.Failed("this operating system has no trash the agent knows how to use, so nothing was removed.");
}

/// <summary>
/// A plain folder used as the trash: the removed folder is renamed into it, under a name that is not
/// taken. Used when <c>Agent:Actions:TrashDirectory</c> is set, and by the tests, which must never
/// fill a real trash.
/// </summary>
public sealed class FolderTrash(string directory, TimeProvider? time = null) : ITrash
{
    public string Name => directory;

    public TrashResult Move(string path) => MoveInto(directory, path, (time ?? TimeProvider.System).GetUtcNow(), out _);

    /// <summary>Renames <paramref name="path"/> into <paramref name="directory"/>. <paramref name="crossDevice"/> is set when that needs a copy, which is never made.</summary>
    internal static TrashResult MoveInto(string directory, string path, DateTimeOffset now, out bool crossDevice)
    {
        crossDevice = false;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TrashResult.Failed($"could not use {directory} as the trash: {ex.Message}");
        }

        foreach (var name in Trash.Candidates(Trash.NameOf(path), now))
        {
            var destination = Path.Combine(directory, name);
            if (Path.Exists(destination))
            {
                continue;
            }

            switch (AtomicMove.Rename(path, destination, out var error))
            {
                case MoveError.None:
                    return TrashResult.Moved(destination);
                case MoveError.Exists:
                    continue;
                case MoveError.CrossDevice:
                    crossDevice = true;
                    return TrashResult.Failed($"{directory} is on a different volume, and the agent only ever moves a folder to the trash, never copies and deletes it.");
                default:
                    return TrashResult.Failed($"could not move {path} to {directory}: {error}");
            }
        }

        return TrashResult.Failed($"could not find a free name for {Trash.NameOf(path)} in {directory}.");
    }
}

/// <summary>How a run of the system's <c>trash</c> tool ended. The exit code is not trusted on its own: see <see cref="MacTrash"/>.</summary>
/// <param name="Started">False when the tool could not be run at all.</param>
/// <param name="TimedOut">It was stopped because it took too long, so what it did is not known.</param>
public sealed record TrashToolRun(bool Started, bool TimedOut, int ExitCode, string Error);

/// <summary>
/// macOS: the Trash. Where the system has <c>/usr/bin/trash</c> (recent macOS) that is asked first,
/// because a folder it moves keeps Finder's "Put Back". Otherwise, or when the tool fails with the
/// folder still in place, a straight rename into <c>~/.Trash</c>, and for a folder on another volume
/// into that volume's <c>.Trashes/&lt;uid&gt;</c>, which is where Finder puts things too. A folder
/// renamed there has no "Put Back", but it is in the Trash and can be dragged out. Nothing is ever
/// copied or deleted here either way.
/// </summary>
public sealed class MacTrash : ITrash
{
    /// <summary>The system's own tool. Only ever run from this fixed path, never looked up on <c>PATH</c>.</summary>
    public const string ToolPath = "/usr/bin/trash";

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);

    private readonly string _home;
    private readonly TimeProvider _time;
    private readonly Func<string, TrashToolRun>? _tool;

    /// <param name="homeTrash">The folder to rename into, in place of <c>~/.Trash</c>. Naming one also turns the system tool off, which only knows the real Trash.</param>
    /// <param name="tool">Stands in for <see cref="ToolPath"/>: given an absolute path, tries to move it to the trash.</param>
    public MacTrash(string? homeTrash = null, TimeProvider? time = null, Func<string, TrashToolRun>? tool = null)
    {
        _home = homeTrash ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
        _time = time ?? TimeProvider.System;
        _tool = tool ?? (homeTrash is null && File.Exists(ToolPath) ? RunTool : null);
    }

    public string Name => "the Trash";

    /// <summary>Whether the system tool (or a stand-in) is asked before the rename.</summary>
    public bool UsesTool => _tool is not null;

    public TrashResult Move(string path)
    {
        // Absolute, so the tool can never read a name that starts with a dash as an option. (It has no "--".)
        var full = Path.GetFullPath(path);
        if (_tool is not null && Path.Exists(full))
        {
            var run = _tool(full);

            // The tool's word is not taken either way: the folder has gone from its path, or it has not.
            if (!Path.Exists(full))
            {
                return TrashResult.Moved(null);
            }

            if (run.TimedOut)
            {
                return TrashResult.Failed($"{ToolPath} did not finish, and {full} is still there. Nothing else was tried.");
            }

            // It did not start, or it failed, or it claimed success: the folder is where it was, so the rename is still safe to try.
        }

        var now = _time.GetUtcNow();
        var result = FolderTrash.MoveInto(_home, full, now, out var crossDevice);
        if (result.Ok || !crossDevice)
        {
            return result;
        }

        // Another volume: its own trash, per user. The boot volume has none, and neither does a volume that refuses one.
        if (Trash.MountOf(full) is not { } volume || volume == "/" || AtomicMove.UserId() is not { } uid)
        {
            return result;
        }

        return FolderTrash.MoveInto(Path.Combine(volume, ".Trashes", uid.ToString(CultureInfo.InvariantCulture)), full, now, out _);
    }

    /// <summary>Runs <c>/usr/bin/trash &lt;path&gt;</c>: an argument list, no shell, and a time limit.</summary>
    private static TrashToolRun RunTool(string fullPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ToolPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(fullPath);

        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return new TrashToolRun(false, false, -1, "could not be started");
            }

            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ToolTimeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // it ended on its own in the meantime
                }

                return new TrashToolRun(true, true, -1, "timed out");
            }

            process.WaitForExit();
            _ = stdout.GetAwaiter().GetResult();
            return new TrashToolRun(true, false, process.ExitCode, stderr.GetAwaiter().GetResult().Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new TrashToolRun(false, false, -1, ex.Message);
        }
    }
}

/// <summary>
/// Linux: the freedesktop.org trash, <c>$XDG_DATA_HOME/Trash</c> (normally <c>~/.local/share/Trash</c>)
/// with a <c>.trashinfo</c> beside each item so the desktop's own trash shows it and can restore it.
/// For a folder on another file system, that file system's <c>.Trash-&lt;uid&gt;</c>, as the spec says.
/// </summary>
public sealed class FreedesktopTrash(string? homeTrash = null, TimeProvider? time = null) : ITrash
{
    private readonly string _home = homeTrash ?? Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data && Path.IsPathFullyQualified(data)
            ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
        "Trash");

    public string Name => "the trash";

    public TrashResult Move(string path)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var full = Path.GetFullPath(path);
        var result = MoveInto(_home, full, full, now, out var crossDevice);
        if (result.Ok || !crossDevice)
        {
            return result;
        }

        if (Trash.MountOf(full) is not { } top || AtomicMove.UserId() is not { } uid)
        {
            return result;
        }

        // In a trash at the top of a file system, the recorded path is relative to that top.
        var relative = Path.GetRelativePath(PathSafety.Resolve(top), PathSafety.Resolve(full));
        return MoveInto(Path.Combine(top, string.Create(CultureInfo.InvariantCulture, $".Trash-{uid}")), full, relative, now, out _);
    }

    private static TrashResult MoveInto(string trash, string path, string recordedPath, DateTimeOffset now, out bool crossDevice)
    {
        crossDevice = false;
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        try
        {
            Directory.CreateDirectory(files);
            Directory.CreateDirectory(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TrashResult.Failed($"could not use {trash} as the trash: {ex.Message}");
        }

        var record = "[Trash Info]\n"
            + $"Path={string.Join('/', recordedPath.Split('/').Select(Uri.EscapeDataString))}\n"
            + string.Create(CultureInfo.InvariantCulture, $"DeletionDate={now.ToLocalTime():yyyy-MM-ddTHH:mm:ss}\n");

        foreach (var name in Trash.Candidates(Trash.NameOf(path), now))
        {
            var destination = Path.Combine(files, name);
            var infoFile = Path.Combine(info, name + ".trashinfo");
            if (Path.Exists(destination))
            {
                continue;
            }

            // The info file is written first and exclusively: it is what reserves the name.
            try
            {
                using var stream = new FileStream(infoFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream);
                writer.Write(record);
            }
            catch (IOException) when (File.Exists(infoFile))
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return TrashResult.Failed($"could not write to {info}: {ex.Message}");
            }

            var moved = AtomicMove.Rename(path, destination, out var error);
            if (moved == MoveError.None)
            {
                return TrashResult.Moved(destination);
            }

            File.Delete(infoFile);
            switch (moved)
            {
                case MoveError.Exists:
                    continue;
                case MoveError.CrossDevice:
                    crossDevice = true;
                    return TrashResult.Failed($"{trash} is on a different file system, and the agent only ever moves a folder to the trash, never copies and deletes it.");
                default:
                    return TrashResult.Failed($"could not move {path} to {trash}: {error}");
            }
        }

        return TrashResult.Failed($"could not find a free name for {Trash.NameOf(path)} in {trash}.");
    }
}

/// <summary>
/// Windows: the Recycle Bin, through the shell (<c>SHFileOperation</c> with <c>FOF_ALLOWUNDO</c>), with
/// no dialogs. The shell deletes outright, without asking, when the Recycle Bin is switched off for a
/// drive or the folder is bigger than the bin, so both are checked first and removal is refused then.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBin : ITrash
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const string BitBucket = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";

    public string Name => "the Recycle Bin";

    public TrashResult Move(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            if (new DriveInfo(Path.GetPathRoot(full)!) is not { DriveType: DriveType.Fixed })
            {
                return TrashResult.Failed($"{full} is not on a fixed local drive, where the Recycle Bin does not keep what is deleted. Nothing was removed.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return TrashResult.Failed($"could not tell what kind of drive {full} is on, so nothing was removed.");
        }

        if (WhyNotRecycled(SizeOf(full)) is { } why)
        {
            return TrashResult.Failed(why);
        }

        var operation = new ShFileOpStruct
        {
            Func = FoDelete,
            From = full + "\0", // the list ends with two nulls; the marshaller adds the second
            Flags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent,
        };
        var code = SHFileOperationW(ref operation);
        if (code != 0 || operation.AnyOperationsAborted)
        {
            return TrashResult.Failed(string.Create(CultureInfo.InvariantCulture, $"Windows could not move {full} to the Recycle Bin (code 0x{code:X})."));
        }

        return Directory.Exists(full)
            ? TrashResult.Failed($"Windows reported success, but {full} is still there.")
            : TrashResult.Moved("the Recycle Bin");
    }

    /// <summary>
    /// Why the shell would delete instead of recycling, from the user's Recycle Bin settings, or null.
    /// Any drive with the bin switched off, or a folder larger than the smallest bin, is a refusal: the
    /// settings are per volume, and refusing on the strictest of them cannot be wrong in the unsafe direction.
    /// </summary>
    private static string? WhyNotRecycled(long bytes)
    {
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(BitBucket);
            if (root?.GetValue("NukeOnDelete") is int all && all != 0)
            {
                return "the Recycle Bin is switched off for this user (files are removed immediately), so nothing was removed.";
            }

            using var volumes = root?.OpenSubKey("Volume");
            foreach (var name in volumes?.GetSubKeyNames() ?? [])
            {
                using var volume = volumes!.OpenSubKey(name);
                if (volume?.GetValue("NukeOnDelete") is int nuke && nuke != 0)
                {
                    return "the Recycle Bin is switched off for a drive on this machine (files are removed immediately), so nothing was removed.";
                }

                if (volume?.GetValue("MaxCapacity") is int megabytes && megabytes > 0 && bytes > megabytes * 1024L * 1024L)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"the folder is larger than the Recycle Bin ({megabytes} MB), so Windows would delete it for good. Nothing was removed.");
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return "could not read the Recycle Bin settings, so nothing was removed.";
        }

        return null;
    }

    private static long SizeOf(string path)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            try
            {
                foreach (var child in new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos())
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (child is FileInfo file)
                    {
                        total += file.Length;
                    }
                    else
                    {
                        stack.Push(child.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Counted as nothing: the shell reports its own failure for a folder it cannot read.
            }
        }

        return total;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHFileOperationW(ref ShFileOpStruct operation);
}

internal enum MoveError
{
    None,

    /// <summary>Something is already at the destination.</summary>
    Exists,

    /// <summary>The destination is on another volume: moving there would mean copying.</summary>
    CrossDevice,
    Other,
}

/// <summary>
/// A rename and nothing more. The framework's own move may fall back to copy-and-delete across
/// volumes; a folder on its way to the trash must never be half copied, so this asks the operating
/// system for a rename and reports what it said.
/// </summary>
internal static class AtomicMove
{
    private const int Eexist = 17, Exdev = 18, EnotemptyLinux = 39, EnotemptyMac = 66;

    public static MoveError Rename(string from, string to, out string? error)
    {
        error = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase))
                {
                    return MoveError.CrossDevice;
                }

                Directory.Move(from, to); // MoveFile: a rename on one volume, never a copy
                return MoveError.None;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return Path.Exists(to) && Path.Exists(from) ? MoveError.Exists : MoveError.Other;
            }
        }

        if (rename(from, to) == 0)
        {
            return MoveError.None;
        }

        var errno = Marshal.GetLastPInvokeError();
        error = Marshal.GetPInvokeErrorMessage(errno);
        return errno switch
        {
            Exdev => MoveError.CrossDevice,
            Eexist or EnotemptyLinux or EnotemptyMac => MoveError.Exists,
            _ => MoveError.Other,
        };
    }

    /// <summary>The user the agent runs as, for a per-user trash on another volume. Null on Windows.</summary>
    public static uint? UserId() => OperatingSystem.IsWindows() ? null : geteuid();

#pragma warning disable CA2101, SYSLIB1054 // UTF-8 is given explicitly; LibraryImport would need unsafe code switched on for the whole project
    [DllImport("libc", SetLastError = true)]
    private static extern int rename([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);

    [DllImport("libc")]
    private static extern uint geteuid();
#pragma warning restore CA2101, SYSLIB1054
}
