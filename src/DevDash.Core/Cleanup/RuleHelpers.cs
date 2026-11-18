using System.Globalization;
using DevDash.Contracts;

namespace DevDash.Core.Cleanup;

internal static class RuleHelpers
{
    /// <summary>POSIX single-quote escaping for suggested commands.</summary>
    public static string Quote(string value) => AgentActions.ShellQuote(value);

    public static string GitCmd(string repoPath, string args) => $"git -C {Quote(repoPath)} {args}";

    /// <summary>A quoted revision range: <c>'a'..'b'</c>, or <c>'a'...'b'</c> for both sides.</summary>
    public static string Range(string from, string to, bool bothSides = false) => Quote(from) + (bothSides ? "..." : "..") + Quote(to);

    public static FindingCommand Inspect(string label, string command, AgentAction? action = null) =>
        new() { Label = label, Command = command, Kind = CommandKind.Inspect, Action = action };

    public static FindingCommand Fix(string label, string command, AgentAction? action = null) =>
        new() { Label = label, Command = command, Kind = CommandKind.Fix, Action = action };

    /// <summary>Shortcuts to get into a folder: a terminal and VS Code.</summary>
    public static IReadOnlyList<FindingCommand> Open(string path) =>
    [
        new() { Label = "cd", Command = $"cd {Quote(path)}", Kind = CommandKind.Open },
        new() { Label = "code", Command = $"code {Quote(path)}", Kind = CommandKind.Open },
    ];

    /// <summary>The main working tree, where per-repository commands (branches, stashes, worktrees) are run.</summary>
    public static string MainPath(LocalClone repo) => repo.Git.Worktrees.Count > 0 ? repo.Git.Worktrees[0].Path : repo.Entry.Path;

    /// <summary>Splits <c>origin/feature/x</c> into remote and branch, using the repo's remote names. Null when the upstream is not on a remote.</summary>
    public static (string Remote, string Branch)? SplitUpstream(GitState git, string? upstream)
    {
        if (upstream is null)
        {
            return null;
        }

        foreach (var remote in git.Remotes.Select(r => r.Name).Distinct(StringComparer.Ordinal).OrderByDescending(n => n.Length))
        {
            if (upstream.Length > remote.Length + 1 && upstream.StartsWith(remote + "/", StringComparison.Ordinal))
            {
                return (remote, upstream[(remote.Length + 1)..]);
            }
        }

        return null;
    }

    /// <summary>Where a branch is checked out, if anywhere.</summary>
    public static string? WorktreeOf(GitState git, string branch) =>
        git.Worktrees.FirstOrDefault(w => w.Branch == branch && !w.Prunable && !w.Bare)?.Path;

    public static int DaysSince(DateTimeOffset now, DateTimeOffset then) => (int)Math.Floor((now - then).TotalDays);

    public static string Plural(int n, string singular, string? plural = null) =>
        n == 1 ? $"1 {singular}" : string.Create(CultureInfo.InvariantCulture, $"{n} {plural ?? singular + "s"}");

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    public static Finding ForClone(
        this ICleanupRule rule,
        LocalClone clone,
        FindingSeverity severity,
        string title,
        string explanation,
        IEnumerable<FindingCommand> commands,
        params string?[] fingerprintParts) => new()
        {
            RuleId = rule.Id,
            Severity = severity,
            Title = title,
            Explanation = explanation,
            Commands = [.. commands, .. Open(clone.Entry.Path)],
            MachineId = clone.Machine.MachineId,
            MachineName = clone.Machine.Hostname,
            Path = clone.Entry.Path,
            RepoKey = clone.RepoKey,
            Fingerprint = Finding.MakeFingerprint(rule.Id, [clone.Machine.MachineId, clone.Entry.Path, .. fingerprintParts]),
        };

    public static Finding ForEntry(
        this ICleanupRule rule,
        MachineSnapshot snapshot,
        DevEntry entry,
        FindingSeverity severity,
        string title,
        string explanation,
        IEnumerable<FindingCommand> commands,
        params string?[] fingerprintParts) => new()
        {
            RuleId = rule.Id,
            Severity = severity,
            Title = title,
            Explanation = explanation,
            // A loose file has no folder of its own to open.
            Commands = [.. commands, .. entry.Kind == EntryKind.LooseFile ? [] : Open(entry.Path)],
            MachineId = snapshot.Machine.MachineId,
            MachineName = snapshot.Machine.Hostname,
            Path = entry.Path,
            Fingerprint = Finding.MakeFingerprint(rule.Id, [snapshot.Machine.MachineId, entry.Path, .. fingerprintParts]),
        };

    /// <summary>Branches that are checked out somewhere and so cannot be deleted.</summary>
    public static HashSet<string> CheckedOutBranches(GitState git)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var wt in git.Worktrees)
        {
            if (wt.Branch is not null)
            {
                set.Add(wt.Branch);
            }
        }

        if (git.Branch is not null)
        {
            set.Add(git.Branch);
        }

        foreach (var b in git.Branches.Where(b => b.IsCurrent))
        {
            set.Add(b.Name);
        }

        return set;
    }
}
