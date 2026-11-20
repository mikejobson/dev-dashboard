using System.Globalization;
using System.Text.Json;

namespace DevDash.Agent.DevServers;

/// <summary>One process with a listening TCP socket, as read from the operating system. <see cref="Args"/> never leaves the machine as is.</summary>
/// <param name="Args">The command line, as arguments (Linux) or split on whitespace (macOS <c>ps</c>, Windows).</param>
/// <param name="Executable">The program's full path, when the system says (Windows, Linux).</param>
public sealed record ListeningProcess(int Pid, string Name, IReadOnlyList<string> Args, string? Executable, string? Cwd, DateTimeOffset? StartedAt, IReadOnlyList<int> Ports);

/// <summary>A process's listening ports, before anything else is known about it.</summary>
public sealed record Listener(int Pid, string? Name, int Port);

/// <summary>Reads what <c>lsof</c>, <c>ps</c>, <c>/proc</c>, <c>netstat</c> and PowerShell print. Pure, tested on captured output.</summary>
public static class ListenerParsers
{
    /// <summary>
    /// <c>lsof -nP -iTCP -sTCP:LISTEN -Fpcn</c>: a <c>p&lt;pid&gt;</c> line starts a process, <c>c&lt;name&gt;</c> names it,
    /// and each <c>n&lt;address&gt;:&lt;port&gt;</c> is one listening socket (<c>*:5001</c>, <c>127.0.0.1:5173</c>, <c>[::1]:3000</c>).
    /// </summary>
    public static IReadOnlyList<Listener> Lsof(string output)
    {
        var listeners = new List<Listener>();
        int? pid = null;
        string? name = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 2)
            {
                continue;
            }

            var value = line[1..];
            switch (line[0])
            {
                case 'p':
                    pid = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
                    name = null;
                    break;
                case 'c':
                    name = value;
                    break;
                case 'n' when pid is { } owner && Port(value) is { } port:
                    listeners.Add(new Listener(owner, name, port));
                    break;
            }
        }

        return listeners;
    }

    /// <summary><c>lsof -a -p 1,2 -d cwd -Fpn</c>: each process's working folder.</summary>
    public static IReadOnlyDictionary<int, string> LsofCwd(string output)
    {
        var cwd = new Dictionary<int, string>();
        int? pid = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 2)
            {
                continue;
            }

            if (line[0] == 'p')
            {
                pid = int.TryParse(line[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
            }
            else if (line[0] == 'n' && pid is { } owner && line[1] == '/')
            {
                cwd[owner] = line[1..];
            }
        }

        return cwd;
    }

    /// <summary>
    /// <c>LC_ALL=C ps -o pid=,lstart=,command= -p 1,2</c>: <c>  1673 Mon Nov 16 07:32:33 2026     /opt/homebrew/bin/syncthing --no-browser</c>.
    /// The start is five words in the machine's local time, read in <paramref name="zone"/>.
    /// </summary>
    public static IReadOnlyDictionary<int, (DateTimeOffset? Started, string Command)> Ps(string output, TimeZoneInfo zone)
    {
        var rows = new Dictionary<int, (DateTimeOffset?, string)>();
        foreach (var raw in output.Split('\n'))
        {
            var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 7 || !int.TryParse(words[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            var started = LocalStart(string.Join(' ', words[1..6]), zone);

            // The command is everything after the fifth word of the date, spacing inside it kept as ps printed it.
            var rest = raw.TrimStart();
            for (var i = 0; i < 6; i++)
            {
                var space = rest.IndexOf(' ');
                rest = space < 0 ? "" : rest[space..].TrimStart();
            }

            rows[pid] = (started, rest.TrimEnd('\r'));
        }

        return rows;
    }

    private static DateTimeOffset? LocalStart(string text, TimeZoneInfo zone)
    {
        // "Mon Nov 16 07:32:33 2026"; a one-digit day may come padded with a second space, which the split has already folded.
        if (!DateTime.TryParseExact(text, ["ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>/proc/net/tcp</c> and <c>tcp6</c>: the inode of every listening socket (state <c>0A</c>) and its port, from the
    /// hex <c>local_address</c> (<c>0100007F:1F90</c> is 127.0.0.1:8080).
    /// </summary>
    public static IReadOnlyDictionary<long, int> ProcNetTcp(string content)
    {
        var sockets = new Dictionary<long, int>();
        foreach (var raw in content.Split('\n').Skip(1))
        {
            var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 10 || words[3] != "0A")
            {
                continue;
            }

            var colon = words[1].LastIndexOf(':');
            if (colon < 0
                || !int.TryParse(words[1][(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port)
                || !long.TryParse(words[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inode)
                || inode == 0)
            {
                continue;
            }

            sockets[inode] = port;
        }

        return sockets;
    }

    /// <summary>A <c>/proc/&lt;pid&gt;/fd/*</c> link target that is a socket: <c>socket:[12345]</c>.</summary>
    public static long? SocketInode(string? linkTarget) =>
        linkTarget is not null && linkTarget.StartsWith("socket:[", StringComparison.Ordinal) && linkTarget.EndsWith(']')
            && long.TryParse(linkTarget[8..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inode)
            ? inode
            : null;

    /// <summary>The <c>btime</c> line of <c>/proc/stat</c>: when the machine booted, in Unix seconds.</summary>
    public static long? BootTime(string procStat) =>
        procStat.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("btime ", StringComparison.Ordinal)) is { } line
            && long.TryParse(line[6..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;

    /// <summary>
    /// <c>/proc/&lt;pid&gt;/stat</c>'s field 22, <c>starttime</c>, in clock ticks after boot. The name (field 2) is in
    /// parentheses and may hold spaces and parentheses itself, so the fields are counted from the last <c>)</c>.
    /// </summary>
    public static DateTimeOffset? ProcStart(string stat, long bootTime, long ticksPerSecond = 100)
    {
        var close = stat.LastIndexOf(')');
        if (close < 0)
        {
            return null;
        }

        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 19 && long.TryParse(fields[19], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            ? DateTimeOffset.FromUnixTimeSeconds(bootTime).AddSeconds((double)ticks / ticksPerSecond)
            : null;
    }

    /// <summary><c>/proc/&lt;pid&gt;/cmdline</c>: arguments separated (and ended) by NUL.</summary>
    public static IReadOnlyList<string> ProcCmdline(string content) => content.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// <c>netstat -ano -p TCP</c> (Windows), the fallback when PowerShell does not answer: <c>  TCP    0.0.0.0:5001    0.0.0.0:0    LISTENING    4242</c>.
    /// The state is localised on some systems, so a row counts when its remote address is the "any" address and port 0.
    /// </summary>
    public static IReadOnlyList<Listener> Netstat(string output)
    {
        var listeners = new List<Listener>();
        foreach (var raw in output.Split('\n'))
        {
            var words = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 5 || !words[0].StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remote = words[2];
            var anyRemote = remote.EndsWith(":0", StringComparison.Ordinal) && (remote.StartsWith("0.0.0.0", StringComparison.Ordinal) || remote.StartsWith("[::]", StringComparison.Ordinal));
            if (!anyRemote || Port(words[1]) is not { } port || !int.TryParse(words[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            {
                continue;
            }

            listeners.Add(new Listener(pid, null, port));
        }

        return listeners;
    }

    /// <summary>
    /// What the agent's PowerShell script prints on Windows: <c>{"Listen":[{"Port":5001,"Pid":4242}],"Processes":[{"Pid":4242,"Name":"dotnet.exe",
    /// "CommandLine":"…","Path":"C:\\…\\dotnet.exe","Started":"2026-11-20T09:00:00.0000000Z"}]}</c>. A one-element list may come as a bare object.
    /// </summary>
    public static IReadOnlyList<ListeningProcess> WindowsJson(string output)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(output.Trim().Length == 0 ? "{}" : output);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return [];
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var ports = new Dictionary<int, SortedSet<int>>();
        foreach (var row in Items(root, "Listen"))
        {
            if (Int(row, "Pid") is { } pid and > 0 && Int(row, "Port") is { } port)
            {
                (ports.TryGetValue(pid, out var set) ? set : ports[pid] = []).Add(port);
            }
        }

        var processes = new List<ListeningProcess>();
        foreach (var row in Items(root, "Processes"))
        {
            if (Int(row, "Pid") is not { } pid || !ports.TryGetValue(pid, out var set))
            {
                continue;
            }

            processes.Add(WindowsProcess(row, pid, set.ToList()));
        }

        return processes;
    }

    /// <summary>One process from the inspect script: the same fields as <see cref="WindowsJson"/>'s, no ports.</summary>
    public static ListeningProcess? WindowsProcessJson(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(output.Trim().Length == 0 ? "null" : output);
            var row = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().FirstOrDefault() : doc.RootElement;
            return row.ValueKind == JsonValueKind.Object && Int(row, "Pid") is { } pid ? WindowsProcess(row, pid, []) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ListeningProcess WindowsProcess(JsonElement row, int pid, IReadOnlyList<int> ports)
    {
        var name = Str(row, "Name") ?? pid.ToString(CultureInfo.InvariantCulture);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        var command = Str(row, "CommandLine");
        DateTimeOffset? started = DateTimeOffset.TryParse(Str(row, "Started"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUniversalTime() : null;
        return new ListeningProcess(pid, name, command is null ? [] : command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries), Str(row, "Path"), null, started, ports);
    }

    /// <summary>The port after the last colon of an address: <c>*:5001</c>, <c>127.0.0.1:5173</c>, <c>[::1]:3000</c>.</summary>
    private static int? Port(string address)
    {
        var colon = address.LastIndexOf(':');
        return colon >= 0 && int.TryParse(address[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 ? port : null;
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string name) =>
        !root.TryGetProperty(name, out var value) ? []
        : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList()
        : value.ValueKind == JsonValueKind.Object ? [value]
        : [];

    private static int? Int(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static string? Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
