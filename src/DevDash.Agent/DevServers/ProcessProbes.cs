using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DevDash.Agent.Toolchains;

namespace DevDash.Agent.DevServers;

/// <summary>The listening processes, and why the list may be empty or partial.</summary>
public sealed record ListenResult(IReadOnlyList<ListeningProcess> Processes, string? Problem = null);

/// <summary>How the agent sees processes. One implementation per operating system; the tests hand in a fake.</summary>
public interface IProcessProbe
{
    /// <summary>Every process with a listening TCP socket that this user can see.</summary>
    Task<ListenResult> ListAsync(CancellationToken ct);

    /// <summary>One process as it is right now (no ports), or null when there is no such process.</summary>
    Task<ListeningProcess?> InspectAsync(int pid, CancellationToken ct);
}

public static class ProcessProbe
{
    public static IProcessProbe ForThisMachine(IToolRunner runner) =>
        OperatingSystem.IsWindows() ? new WindowsProbe(runner)
        : OperatingSystem.IsLinux() ? new LinuxProbe(new ProcFs())
        : new MacProbe(runner);
}

/// <summary>macOS (and other BSDs): <c>lsof</c> for the sockets and working folders, <c>ps</c> for start times and command lines.</summary>
public sealed class MacProbe(IToolRunner runner, TimeZoneInfo? zone = null, string lsof = "/usr/sbin/lsof", string ps = "/bin/ps") : IProcessProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Dictionary<string, string> C = new() { ["LC_ALL"] = "C" };
    private const int MaxProcesses = 200;

    public async Task<ListenResult> ListAsync(CancellationToken ct)
    {
        var listing = await runner.RunAsync(new ToolCommand(lsof, ["-nP", "-iTCP", "-sTCP:LISTEN", "-Fpcn"]) { Environment = C }, Timeout, ct).ConfigureAwait(false);

        // lsof exits 1 when it finds nothing at all, with nothing printed.
        if (!listing.Started || listing.TimedOut)
        {
            return new ListenResult([], listing.TimedOut ? "lsof did not answer in time." : $"lsof could not be run: {listing.StdErr.Trim()}");
        }

        var listeners = ListenerParsers.Lsof(listing.StdOut);
        var byPid = listeners.GroupBy(l => l.Pid).Take(MaxProcesses).ToList();
        if (byPid.Count == 0)
        {
            return new ListenResult([]);
        }

        var details = await DetailsAsync(byPid.Select(g => g.Key).ToList(), ct).ConfigureAwait(false);
        var processes = byPid.Select(g =>
        {
            details.TryGetValue(g.Key, out var detail);
            return new ListeningProcess(g.Key, g.First().Name ?? detail?.Name ?? g.Key.ToString(CultureInfo.InvariantCulture), detail?.Args ?? [], null, detail?.Cwd, detail?.Started,
                g.Select(l => l.Port).Distinct().Order().ToList());
        }).ToList();
        return new ListenResult(processes);
    }

    public async Task<ListeningProcess?> InspectAsync(int pid, CancellationToken ct)
    {
        var details = await DetailsAsync([pid], ct).ConfigureAwait(false);
        return details.TryGetValue(pid, out var detail) ? new ListeningProcess(pid, detail.Name, detail.Args, null, detail.Cwd, detail.Started, []) : null;
    }

    private sealed record Detail(string Name, IReadOnlyList<string> Args, string? Cwd, DateTimeOffset? Started);

    private async Task<Dictionary<int, Detail>> DetailsAsync(IReadOnlyList<int> pids, CancellationToken ct)
    {
        var list = string.Join(',', pids.Select(p => p.ToString(CultureInfo.InvariantCulture)));
        var psRun = await runner.RunAsync(new ToolCommand(ps, ["-o", "pid=,lstart=,command=", "-p", list]) { Environment = C }, Timeout, ct).ConfigureAwait(false);
        var cwdRun = await runner.RunAsync(new ToolCommand(lsof, ["-a", "-p", list, "-d", "cwd", "-Fpn"]) { Environment = C }, Timeout, ct).ConfigureAwait(false);
        var rows = ListenerParsers.Ps(psRun.StdOut, zone ?? TimeZoneInfo.Local);
        var cwds = ListenerParsers.LsofCwd(cwdRun.StdOut);
        var details = new Dictionary<int, Detail>();
        foreach (var (pid, row) in rows)
        {
            var args = row.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            details[pid] = new Detail(args.Length > 0 ? Path.GetFileName(args[0]) : pid.ToString(CultureInfo.InvariantCulture), args, cwds.GetValueOrDefault(pid), row.Started);
        }

        return details;
    }
}

/// <summary>Reads <c>/proc</c>. An interface so the Linux probe is tested on a made-up process table.</summary>
public interface IProcFs
{
    string? ReadText(string path);
    IEnumerable<string> List(string path);
    string? ReadLink(string path);
}

public sealed class ProcFs(string root = "/proc") : IProcFs
{
    public string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(Path.Combine(root, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IEnumerable<string> List(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(Path.Combine(root, path)).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string? ReadLink(string path)
    {
        try
        {
            return new FileInfo(Path.Combine(root, path)).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Linux: listening sockets from <c>/proc/net/tcp{,6}</c>, matched to processes through <c>/proc/&lt;pid&gt;/fd</c>.</summary>
public sealed class LinuxProbe(IProcFs proc) : IProcessProbe
{
    public Task<ListenResult> ListAsync(CancellationToken ct)
    {
        var sockets = new Dictionary<long, int>();
        foreach (var table in (string[])["net/tcp", "net/tcp6"])
        {
            foreach (var (inode, port) in ListenerParsers.ProcNetTcp(proc.ReadText(table) ?? ""))
            {
                sockets[inode] = port;
            }
        }

        if (sockets.Count == 0)
        {
            return Task.FromResult(new ListenResult([], proc.ReadText("net/tcp") is null ? "/proc/net/tcp could not be read." : null));
        }

        var boot = ListenerParsers.BootTime(proc.ReadText("stat") ?? "");
        var processes = new List<ListeningProcess>();
        foreach (var name in proc.List(""))
        {
            ct.ThrowIfCancellationRequested();
            if (!int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            var ports = new SortedSet<int>();
            foreach (var fd in proc.List($"{pid}/fd"))
            {
                if (ListenerParsers.SocketInode(proc.ReadLink($"{pid}/fd/{fd}")) is { } inode && sockets.TryGetValue(inode, out var port))
                {
                    ports.Add(port);
                }
            }

            if (ports.Count > 0 && Read(pid, boot) is { } process)
            {
                processes.Add(process with { Ports = ports.ToList() });
            }
        }

        return Task.FromResult(new ListenResult(processes));
    }

    public Task<ListeningProcess?> InspectAsync(int pid, CancellationToken ct) =>
        Task.FromResult(Read(pid, ListenerParsers.BootTime(proc.ReadText("stat") ?? "")));

    private ListeningProcess? Read(int pid, long? boot)
    {
        if (proc.ReadText($"{pid}/stat") is not { } stat)
        {
            return null;
        }

        var args = ListenerParsers.ProcCmdline(proc.ReadText($"{pid}/cmdline") ?? "");
        var name = (proc.ReadText($"{pid}/comm") ?? "").Trim();
        return new ListeningProcess(
            pid,
            name.Length > 0 ? name : args.Count > 0 ? Path.GetFileName(args[0]) : pid.ToString(CultureInfo.InvariantCulture),
            args,
            proc.ReadLink($"{pid}/exe"),
            proc.ReadLink($"{pid}/cwd"),
            boot is { } b ? ListenerParsers.ProcStart(stat, b) : null,
            []);
    }
}

/// <summary>
/// Windows: one PowerShell call (<c>Get-NetTCPConnection</c> and <c>Win32_Process</c>) for sockets, command lines and
/// start times; <c>netstat -ano</c> when PowerShell does not answer. A process's working folder cannot be read from
/// outside it on Windows, so repositories are matched by the paths on its command line.
/// </summary>
public sealed class WindowsProbe(IToolRunner runner) : IProcessProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private const string Projection =
        "ForEach-Object { [pscustomobject]@{ Pid = [int]$_.ProcessId; Name = $_.Name; CommandLine = $_.CommandLine; Path = $_.ExecutablePath; " +
        "Started = $(if ($_.CreationDate) { $_.CreationDate.ToUniversalTime().ToString('o') } else { $null }) } }";

    private const string ListScript =
        "$ErrorActionPreference = 'SilentlyContinue'; " +
        "$l = @(Get-NetTCPConnection -State Listen | ForEach-Object { [pscustomobject]@{ Port = [int]$_.LocalPort; Pid = [int]$_.OwningProcess } }); " +
        "$ids = @($l | ForEach-Object { $_.Pid } | Sort-Object -Unique); " +
        "$p = @(Get-CimInstance Win32_Process | Where-Object { $ids -contains [int]$_.ProcessId } | " + Projection + "); " +
        "[pscustomobject]@{ Listen = $l; Processes = $p } | ConvertTo-Json -Depth 4 -Compress";

    private static string PowerShell => Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    private static ToolCommand Script(string script) => new(PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script]);

    public async Task<ListenResult> ListAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(Script(ListScript), Timeout, ct).ConfigureAwait(false);
        if (result.Ok && result.StdOut.TrimStart().StartsWith('{'))
        {
            return new ListenResult(ListenerParsers.WindowsJson(result.StdOut));
        }

        // No PowerShell answer: netstat for the sockets, .NET for names and start times. No command lines, so nothing can be stopped.
        var netstat = await runner.RunAsync(new ToolCommand(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(PowerShell))!)!, "NETSTAT.EXE"), ["-ano", "-p", "TCP"]), Timeout, ct).ConfigureAwait(false);
        if (!netstat.Ok)
        {
            return new ListenResult([], "Neither PowerShell nor netstat answered.");
        }

        var processes = ListenerParsers.Netstat(netstat.StdOut).GroupBy(l => l.Pid).Select(g =>
        {
            string name = g.Key.ToString(CultureInfo.InvariantCulture);
            DateTimeOffset? started = null;
            try
            {
                using var process = Process.GetProcessById(g.Key);
                name = process.ProcessName;
                started = process.StartTime.ToUniversalTime();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Gone, or another user's: the name stays the pid.
            }

            return new ListeningProcess(g.Key, name, [], null, null, started, g.Select(l => l.Port).Distinct().Order().ToList());
        }).ToList();
        return new ListenResult(processes, "PowerShell did not answer, so command lines are missing.");
    }

    public async Task<ListeningProcess?> InspectAsync(int pid, CancellationToken ct)
    {
        var script = "$ErrorActionPreference = 'SilentlyContinue'; @(Get-CimInstance Win32_Process -Filter \"ProcessId = " + pid.ToString(CultureInfo.InvariantCulture) + "\" | " + Projection + ") | ConvertTo-Json -Depth 3 -Compress";
        var result = await runner.RunAsync(Script(script), Timeout, ct).ConfigureAwait(false);
        return result.Ok ? ListenerParsers.WindowsProcessJson(result.StdOut) : null;
    }
}

/// <summary>Asks a process to stop. Never a hard kill on macOS or Linux.</summary>
public interface IProcessSignal
{
    /// <summary>Returns null when the request was delivered, or why it was not.</summary>
    string? Terminate(int pid);

    /// <summary>The word for what <see cref="Terminate"/> sends, for messages.</summary>
    string Name { get; }
}

public static class ProcessSignal
{
    public static IProcessSignal ForThisMachine() => OperatingSystem.IsWindows() ? new WindowsTerminate() : new UnixTerminate();
}

/// <summary><c>kill(pid, SIGTERM)</c>: the server can shut down cleanly. SIGKILL is never sent.</summary>
public sealed class UnixTerminate : IProcessSignal
{
    private const int SigTerm = 15;

    public string Name => "SIGTERM";

    public string? Terminate(int pid) => kill(pid, SigTerm) == 0 ? null : $"kill failed (errno {Marshal.GetLastPInvokeError()}).";

#pragma warning disable SYSLIB1054 // LibraryImport would need unsafe code switched on for the whole project
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
#pragma warning restore SYSLIB1054
}

/// <summary>Windows has no SIGTERM: ask the main window to close, and when there is none or it does not close in 5 s, terminate that one process (not its children).</summary>
public sealed class WindowsTerminate : IProcessSignal
{
    public string Name => "a close request";

    public string? Terminate(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.CloseMainWindow() && process.WaitForExit(5000))
            {
                return null;
            }

            process.Kill(entireProcessTree: false);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return ex.Message;
        }
    }
}
