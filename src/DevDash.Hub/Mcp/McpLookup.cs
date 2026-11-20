using System.Text.Encodings.Web;
using System.Text.Json;
using DevDash.Contracts;
using DevDash.Core;
using DevDash.Hub.Services;
using ModelContextProtocol;

namespace DevDash.Hub.Mcp;

/// <summary>Tool output: compact JSON, capped so one call cannot flood the model's context.</summary>
public static class McpOutput
{
    /// <summary>The most characters one tool result may hold.</summary>
    public const int MaxChars = 60_000;

    /// <summary>The largest <c>limit</c> any list tool accepts.</summary>
    public const int MaxLimit = 200;

    /// <summary>The snapshot's options, without HTML-safe escaping: the reader is a model, and <c>\u0027</c> for every quote in a git command only costs tokens.</summary>
    private static readonly JsonSerializerOptions Options = new(SnapshotSchema.JsonOptions) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Json(object value)
    {
        var text = JsonSerializer.Serialize(value, Options);
        return text.Length <= MaxChars
            ? text
            : text[..MaxChars] + $"\n…[cut: {text.Length - MaxChars} more characters. Narrow the request with the tool's filters or a lower limit.]";
    }

    public static int Limit(int? requested, int fallback) => Math.Clamp(requested ?? fallback, 1, MaxLimit);

    public static List<T> Cap<T>(IEnumerable<T> items, int max) => items.Take(max).ToList();
}

/// <summary>Finds machines, projects and clones from what a model typed: a name, a key or a path.</summary>
public static class McpLookup
{
    public static bool MachineMatches(MachineView machine, string? query) =>
        string.IsNullOrWhiteSpace(query)
        || string.Equals(machine.Machine.Hostname, query.Trim(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(machine.MachineId, query.Trim(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(machine.Machine.TailscaleName, query.Trim(), StringComparison.OrdinalIgnoreCase)
        // "work-mac" for a hostname reported as "work-mac.local".
        || machine.Machine.Hostname.StartsWith(query.Trim() + ".", StringComparison.OrdinalIgnoreCase);

    /// <summary>The machine named, or an error that lists the ones there are.</summary>
    public static MachineView Machine(SnapshotStore snapshots, string query)
    {
        var matches = snapshots.Machines.Where(m => MachineMatches(m, query)).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new McpException(matches.Count == 0
                ? $"No machine called '{query}'. Known machines: {string.Join(", ", snapshots.Machines.Select(m => m.Machine.Hostname))}."
                : $"'{query}' matches several machines: {string.Join(", ", matches.Select(m => $"{m.Machine.Hostname} ({m.MachineId})"))}. Use the machine id.");
    }

    /// <summary>
    /// The project <paramref name="query"/> names: its key (<c>github.com/owner/repo</c>), <c>owner/repo</c>,
    /// the path of one of its clones, or its name. Tries those in that order and stops at the first that
    /// matches anything; more than one match at that step is an error listing them.
    /// </summary>
    public static RepoRow Project(IReadOnlyList<RepoRow> rows, string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            throw new McpException("Name a project: its name, owner/name, key, or the path of a clone.");
        }

        Func<RepoRow, bool>[] steps =
        [
            r => string.Equals(r.Key, query, StringComparison.OrdinalIgnoreCase),
            r => r.Clones.Any(c => RepoIdentity.SamePath(c.Entry.Path, query)),
            r => r.Owner is not null && string.Equals($"{r.Owner}/{r.Name}", query, StringComparison.OrdinalIgnoreCase),
            r => string.Equals(r.Name, query, StringComparison.OrdinalIgnoreCase),
            r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Key.Contains(query, StringComparison.OrdinalIgnoreCase),
        ];

        foreach (var step in steps)
        {
            var matches = rows.Where(step).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                throw new McpException($"'{query}' matches {matches.Count} projects: {string.Join(", ", matches.Take(15).Select(r => r.Key))}{(matches.Count > 15 ? ", …" : "")}. Pass one of these keys.");
            }
        }

        throw new McpException($"No project matches '{query}'. Use list_projects to see what there is.");
    }

    /// <summary>
    /// The one working tree an action should run in: the clone at <paramref name="query"/> when it is a path,
    /// else the project's main working tree on the named machine (the only machine, when it is on one).
    /// </summary>
    public static CloneView Clone(SnapshotStore snapshots, RepoRow row, string query, string? machine)
    {
        var git = row.Clones.Where(c => c.Git is not null).ToList();
        if (git.Count == 0)
        {
            throw new McpException($"{row.Name} is not a git repository on any machine.");
        }

        if (git.FirstOrDefault(c => RepoIdentity.SamePath(c.Entry.Path, query.Trim()) && McpLookup.MachineMatches(c.Machine, machine)) is { } atPath)
        {
            return atPath;
        }

        if (string.IsNullOrWhiteSpace(machine))
        {
            var machines = git.Select(c => c.Machine).DistinctBy(m => m.MachineId).ToList();
            if (machines.Count > 1)
            {
                throw new McpException($"{row.Name} is on {machines.Count} machines ({string.Join(", ", machines.Select(m => m.Machine.Hostname))}). Say which with 'machine'.");
            }
        }
        else
        {
            var named = Machine(snapshots, machine);
            git = git.Where(c => c.Machine.MachineId == named.MachineId).ToList();
            if (git.Count == 0)
            {
                throw new McpException($"{row.Name} has no clone on {named.Machine.Hostname}.");
            }
        }

        // The main working tree, which is the path the agent is sure to accept; else the first linked one.
        var mains = git.Where(c => !c.IsLinkedWorktree).ToList();
        if (mains.Count > 1)
        {
            throw new McpException($"{row.Name} has {mains.Count} clones there: {string.Join(", ", mains.Select(c => c.Entry.Path))}. Pass the path as 'project'.");
        }

        return mains.Count == 1 ? mains[0] : git[0];
    }
}
