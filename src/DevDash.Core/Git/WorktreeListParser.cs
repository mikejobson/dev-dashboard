using DevDash.Contracts;

namespace DevDash.Core.Git;

/// <summary>Parses <c>git worktree list --porcelain</c>. The first entry is the main working tree.</summary>
public static class WorktreeListParser
{
    private const string BranchPrefix = "refs/heads/";

    public static IReadOnlyList<WorktreeInfo> Parse(string output)
    {
        var result = new List<WorktreeInfo>();
        string? path = null, head = null, branch = null, prunableReason = null;
        bool detached = false, bare = false, locked = false, prunable = false;

        void Flush()
        {
            if (path is not null)
            {
                result.Add(new WorktreeInfo
                {
                    Path = path,
                    Head = head,
                    Branch = branch,
                    Detached = detached,
                    Bare = bare,
                    Locked = locked,
                    Prunable = prunable,
                    PrunableReason = prunableReason,
                });
            }

            path = head = branch = prunableReason = null;
            detached = bare = locked = prunable = false;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? "" : line[(space + 1)..];

            switch (key)
            {
                case "worktree":
                    Flush();
                    path = value;
                    break;
                case "HEAD":
                    head = value;
                    break;
                case "branch":
                    branch = value.StartsWith(BranchPrefix, StringComparison.Ordinal) ? value[BranchPrefix.Length..] : value;
                    break;
                case "detached":
                    detached = true;
                    break;
                case "bare":
                    bare = true;
                    break;
                case "locked":
                    locked = true;
                    break;
                case "prunable":
                    prunable = true;
                    prunableReason = value.Length == 0 ? null : value;
                    break;
            }
        }

        Flush();
        return result;
    }
}
