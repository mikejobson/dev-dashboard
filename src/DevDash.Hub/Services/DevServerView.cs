using DevDash.Contracts;
using DevDash.Core;

namespace DevDash.Hub.Services;

/// <summary>One listening dev server, joined to its machine and repository.</summary>
/// <param name="RepoKey">The repository's key (a linked worktree's main repository), or null for a tool outside every scanned repository.</param>
/// <param name="Clashes">Other repositories' servers, on any machine, that use one of the same ports: run both on one machine and one will not start.</param>
public sealed record DevServerRow(MachineView Machine, DevServer Server, string? RepoKey, string? RepoName, bool Old, IReadOnlyList<DevServerRow> Clashes)
{
    /// <summary>":5001", ":3000, :3001".</summary>
    public string Ports => string.Join(", ", Server.Ports.Select(p => ":" + p));

    /// <summary>The stop action, when the server can be stopped from here: it is in a repository and the agent read its start time and command.</summary>
    public AgentAction? StopAction =>
        Server is { RepoPath: { } repo, StartedAt: { } started, Command: { Length: > 0 } command, Pid: > 1 }
            ? AgentAction.StopDevServer(repo, Server.Pid, started, command)
            : null;
}

/// <summary>The dev servers across machines, with clashes and old ones marked. Pure over the snapshots.</summary>
public static class DevServerView
{
    /// <summary>A server running longer than this is "old": probably forgotten.</summary>
    public static readonly TimeSpan OldAfter = TimeSpan.FromDays(7);

    public static IReadOnlyList<DevServerRow> Rows(IEnumerable<MachineView> machines, DateTimeOffset now)
    {
        var rows = new List<DevServerRow>();
        foreach (var machine in machines)
        {
            if (machine.Machine.DevServers is not { Servers.Count: > 0 } report)
            {
                continue;
            }

            var entries = RepoIdentity.Flatten(machine.Snapshot.Entries).ToList();
            var mains = RepoIdentity.MainWorkingTrees(machine.Snapshot.Entries);
            foreach (var server in report.Servers)
            {
                string? key = null, name = null;
                if (server.RepoPath is { } path && entries.FirstOrDefault(e => e.Path == path) is { } entry)
                {
                    var main = RepoIdentity.MainOf(entry, mains) ?? entry;
                    key = RepoIdentity.KeyFor(machine.MachineId, main);
                    name = main.Name;
                }

                var old = server.StartedAt is { } started && now - started > OldAfter;
                rows.Add(new DevServerRow(machine, server, key, name, old, []));
            }
        }

        // A clash is the same port used by servers of different repositories (or a repository and a stray tool).
        return rows.Select(row => row with { Clashes = rows.Where(other => Clash(row, other)).ToList() })
        .OrderBy(r => r.RepoKey is null)
        .ThenBy(r => r.RepoName ?? r.Server.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(r => r.Server.Ports.FirstOrDefault())
        .ToList();
    }

    /// <summary>Two servers clash when they share a port and are not the same repository (the same repo on two machines on its usual port is expected).</summary>
    private static bool Clash(DevServerRow a, DevServerRow b) =>
        !(string.Equals(a.Machine.MachineId, b.Machine.MachineId, StringComparison.OrdinalIgnoreCase) && a.Server.Pid == b.Server.Pid)
        && a.Server.Ports.Intersect(b.Server.Ports).Any()
        && (a.RepoKey is null || b.RepoKey is null || !string.Equals(a.RepoKey, b.RepoKey, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<DevServerRow> ForRepo(IReadOnlyList<DevServerRow> rows, string key) =>
        rows.Where(r => string.Equals(r.RepoKey, key, StringComparison.OrdinalIgnoreCase)).ToList();

    public static IReadOnlyList<DevServerRow> ForMachine(IReadOnlyList<DevServerRow> rows, string machineId) =>
        rows.Where(r => string.Equals(r.Machine.MachineId, machineId, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>"akin — dotnet on :5001, 3 days".</summary>
    public static string Line(DevServerRow row, DateTimeOffset now)
    {
        var age = row.Server.StartedAt is { } started ? ", " + Running(started, now) : "";
        return $"{row.RepoName ?? row.Server.Name} — {row.Server.Name} on {row.Ports}{age}";
    }

    /// <summary>How long it has been running: "40 min", "5 h", "3 days".</summary>
    public static string Running(DateTimeOffset started, DateTimeOffset now)
    {
        var span = now - started;
        return span.TotalMinutes < 1 ? "just started"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h"
            : (int)span.TotalDays == 1 ? "1 day" : $"{(int)span.TotalDays} days";
    }

    /// <summary>"port 5001 is also used by widget on laptop".</summary>
    public static string ClashText(DevServerRow row) =>
        string.Join("; ", row.Clashes.Select(c =>
            $"port {string.Join(", ", c.Server.Ports.Intersect(row.Server.Ports))} is also used by {c.RepoName ?? c.Server.Name} on {c.Machine.Machine.Hostname}"));
}
