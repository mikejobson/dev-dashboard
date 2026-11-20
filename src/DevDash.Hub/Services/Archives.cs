using System.Text.RegularExpressions;
using DevDash.Contracts;
using DevDash.Core.Safety;

namespace DevDash.Hub.Services;

/// <summary>
/// The hub's side of archiving a project (§23): what to say in the dialog, and the checks made before queueing.
/// The agent repeats every one of them against the folder itself.
/// </summary>
public static partial class Archives
{
    [GeneratedRegex(@"^[0-9a-f]{64}$")]
    private static partial Regex Sha256();

    /// <summary>
    /// What the archive will hold, before compression: the folder's measured size, less the build output the agent
    /// leaves out unless asked to keep it. Null when the folder has not been measured.
    /// </summary>
    public static long? Estimate(SafetyAssessment safety, bool includeBuildOutput) =>
        safety.TotalBytes is { } total ? Math.Max(0, includeBuildOutput ? total : total - (safety.ReclaimableBytes ?? 0)) : null;

    /// <summary>Why this project cannot be archived at all, whatever is in it: a linked worktree, or one with linked worktrees. Null when it can.</summary>
    public static string? Blocked(SafetyAssessment safety) =>
        safety.IsLinkedWorktree ? "A linked worktree: its commits are in the main repository. Archive that instead."
        : safety.Reasons.FirstOrDefault(r => r.Kind == SafetyReasonKind.LinkedWorktrees) is { } linked ? $"It has linked worktrees ({linked.Text}). Remove them first; moving the repository would break them."
        : null;

    /// <summary>What the archive will be called, with the time it is made left as a pattern.</summary>
    public static string Destination(ArchiveInfo archive, string name) =>
        $"{archive.Folder.TrimEnd('/', '\\')}{(archive.Folder.Contains('\\') ? '\\' : '/')}{name}-<yyyyMMdd-HHmmss>.{ArchiveFormats.Extension(archive.Format)}";

    /// <summary>Why the hub will not ask for this archive, or null. Unlike a removal, any verdict is fine.</summary>
    public static string? Refusal(MachineView machine, AgentAction action, CloneSafety? assessed)
    {
        if (assessed is null)
        {
            return $"{machine.Machine.Hostname} no longer reports a repository at {action.RepoPath}.";
        }

        if (Blocked(assessed.Safety) is { } blocked)
        {
            return $"{action.RepoPath} cannot be archived. {blocked}";
        }

        if (assessed.Safety.Fingerprint != action.ExpectedFingerprint)
        {
            return $"{action.RepoPath} has changed since this page was drawn. Look at it again before archiving it.";
        }

        if (machine.Machine.Archive is not { Available: true, Writable: true } archive)
        {
            return machine.Machine.Archive?.Problem is { } problem
                ? $"Archiving is not available on {machine.Machine.Hostname}: {problem}."
                : $"{machine.Machine.Hostname} has no archive folder.";
        }

        if (archive.FreeBytes is { } free && Estimate(assessed.Safety, action.IncludeBuildOutput) is { } estimate && free < estimate + archive.MarginBytes)
        {
            return $"{archive.Folder} on {machine.Machine.Hostname} has {Fmt.Size(free)} free; this needs about {Fmt.Size(estimate)} plus {Fmt.Size(archive.MarginBytes)} to spare.";
        }

        return null;
    }

    /// <summary>The archive an agent reported, when it looks like one; null otherwise (the tombstone then offers no restore).</summary>
    public static ArchiveResult? Recorded(ArchiveResult? reported) =>
        reported is { ArchivePath.Length: > 0 and <= 1024, Manifest: { } manifest } && Sha256().IsMatch(manifest.ArchiveSha256)
            && (reported.ArchivePath[0] == '/' || (reported.ArchivePath.Length >= 3 && reported.ArchivePath[1] == ':'))
            ? reported
            : null;

    /// <summary>True when <paramref name="path"/> is strictly inside one of the machine's scan roots (as it reported them).</summary>
    public static bool InsideARoot(IEnumerable<string> roots, string path) =>
        roots.Any(root =>
        {
            var trimmed = root.TrimEnd('/', '\\');
            return trimmed.Length > 0 && path.Length > trimmed.Length + 1 && path.StartsWith(trimmed, StringComparison.Ordinal) && path[trimmed.Length] is '/' or '\\';
        });
}
