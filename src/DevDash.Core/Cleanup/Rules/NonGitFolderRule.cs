using DevDash.Contracts;
using static DevDash.Core.Cleanup.RuleHelpers;

namespace DevDash.Core.Cleanup.Rules;

/// <summary>Folders and loose files in a scan root that are not under version control.</summary>
public sealed class NonGitFolderRule : ICleanupRule
{
    public string Id => "non-git-folder";
    public string Title => "Non-git folders in Developer";
    public string Description => "Folders and loose files that are not git repos. Move them out, or put them under version control.";

    public IEnumerable<Finding> Evaluate(CleanupContext context)
    {
        foreach (var snapshot in context.Machines)
        {
            foreach (var entry in snapshot.Entries)
            {
                if (entry.Kind == EntryKind.LooseFile)
                {
                    yield return this.ForEntry(snapshot, entry, FindingSeverity.Low,
                        $"Loose file: {entry.Name}",
                        "A file sitting directly in the scan root. It probably belongs in a project or in Documents.",
                        [
                            Inspect("Look at the file", $"ls -l {Quote(entry.Path)}"),
                            Fix("Move it to Documents", $"mv {Quote(entry.Path)} ~/Documents/"),
                        ]);
                }
                else if (entry.Kind == EntryKind.WorktreeContainer && !RepoIdentity.IsWorktreeFolderName(entry.Name))
                {
                    // A plain folder that happens to hold clones. Worktree folders are StaleWorktreeRule's job.
                    var repos = entry.Children.Where(c => c.Kind == EntryKind.GitRepo).Select(c => c.Name).ToList();
                    var other = entry.Children.Where(c => c.Kind != EntryKind.GitRepo).Select(c => c.Name).ToList();
                    yield return this.ForEntry(snapshot, entry, FindingSeverity.Low,
                        $"Not a git repo: {entry.Name}",
                        $"This folder is not a repo itself. It holds {Plural(repos.Count, "repo")} ({string.Join(", ", repos)})"
                        + (other.Count > 0 ? $" and {Plural(other.Count, "untracked folder")} ({string.Join(", ", other)})." : ".")
                        + " Move the repos up a level, or tidy the leftovers.",
                        [Inspect("List the folder", $"ls -la {Quote(entry.Path)}")]);
                }
                else if (entry.Kind == EntryKind.NonGitFolder)
                {
                    var looksLikeCode = entry.Description?.Manifests.Count > 0;
                    var contents = entry.Folder is { } f
                        ? $" It holds {Plural(f.FileCount, "file")} and {Plural(f.DirectoryCount, "folder")} at the top level"
                          + (f.SampleNames.Count > 0 ? $" ({string.Join(", ", f.SampleNames.Take(4))})." : ".")
                        : "";
                    yield return this.ForEntry(snapshot, entry, looksLikeCode ? FindingSeverity.Medium : FindingSeverity.Low,
                        $"Not a git repo: {entry.Name}",
                        (looksLikeCode
                            ? "This folder has project manifests but no git history, so nothing in it is backed up or tracked."
                            : "This folder is not a git repo. If it is reference material, it belongs outside the code folder.")
                        + contents,
                        [
                            Inspect("List the folder", $"ls -la {Quote(entry.Path)}"),
                            looksLikeCode
                                ? Fix("Start tracking it with git", $"git -C {Quote(entry.Path)} init")
                                : Fix("Move it to Documents", $"mv {Quote(entry.Path)} ~/Documents/"),
                        ]);
                }
            }
        }
    }
}
