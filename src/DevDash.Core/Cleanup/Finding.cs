using System.Security.Cryptography;
using System.Text;
using DevDash.Contracts;

namespace DevDash.Core.Cleanup;

public enum FindingSeverity
{
    Info,
    Low,
    Medium,
    High,
}

public enum CommandKind
{
    /// <summary>Read-only: look before you act.</summary>
    Inspect,

    /// <summary>Changes something. The only kind that goes into a copied script.</summary>
    Fix,

    /// <summary>A shortcut to get to the folder (<c>cd</c>, <c>code</c>).</summary>
    Open,
}

/// <summary>One command a finding suggests, in the order to run them: inspect first, then fix.</summary>
public sealed record FindingCommand
{
    /// <summary>A few words on what the command does.</summary>
    public required string Label { get; init; }

    /// <summary>The command line, quoted for a POSIX shell.</summary>
    public required string Command { get; init; }
    public CommandKind Kind { get; init; }

    /// <summary>
    /// The allowlisted action an agent can run instead of the user pasting <see cref="Command"/>.
    /// Null for anything that is not on the allowlist, which is most commands.
    /// </summary>
    public AgentAction? Action { get; init; }

    /// <summary>Set when the command belongs to a different machine than the finding (findings that span machines).</summary>
    public string? MachineId { get; init; }
    public string? MachineName { get; init; }
}

/// <summary>
/// Something worth tidying up. Commands are suggestions to copy. The few that carry an
/// <see cref="FindingCommand.Action"/> can also be queued for the machine's agent, if that machine allows it.
/// </summary>
public sealed record Finding
{
    public required string RuleId { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required string Title { get; init; }
    public required string Explanation { get; init; }

    /// <summary>Suggested commands in the order to run them.</summary>
    public IReadOnlyList<FindingCommand> Commands { get; init; } = [];
    public string? MachineId { get; init; }
    public string? MachineName { get; init; }
    public string? Path { get; init; }
    public string? RepoKey { get; init; }
    public string? Url { get; init; }

    /// <summary>
    /// When the thing the finding is about last changed: the branch's last commit, the newest edited
    /// file, the PR's last update. Used to tell live work from abandoned work. Null when unknown.
    /// Not part of the fingerprint.
    /// </summary>
    public DateTimeOffset? LastActivityAt { get; init; }

    /// <summary>Stable id used to remember snooze and dismiss decisions.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The machine a command is meant for: its own, else the finding's. Null for GitHub-only findings.</summary>
    public string? MachineIdFor(FindingCommand command) => command.MachineId ?? MachineId;

    public string? MachineNameFor(FindingCommand command) => command.MachineName ?? (command.MachineId is null ? MachineName : command.MachineId);

    /// <summary>
    /// Builds a fingerprint from the parts that identify a finding. Leave out anything volatile
    /// (counts, dates) so the fingerprint survives rescans.
    /// </summary>
    public static string MakeFingerprint(string ruleId, params string?[] parts)
    {
        var text = ruleId + "\n" + string.Join("\n", parts.Select(p => p ?? ""));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash.AsSpan(0, 12));
    }
}
