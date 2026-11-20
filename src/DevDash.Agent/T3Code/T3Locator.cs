using DevDash.Agent.Toolchains;
using DevDash.Contracts;

namespace DevDash.Agent.T3Code;

/// <summary>Finds T3 Code's CLI, and tells T3's own worktrees from anyone else's.</summary>
public static class T3Locator
{
    /// <summary>
    /// The CLI to run: the configured path when there is one (and nothing else), else T3 Code's <c>bin</c>
    /// folder, else <c>PATH</c>. On Windows an <c>.exe</c> is preferred over a <c>.cmd</c>, which needs
    /// cmd.exe to run it and so takes fewer kinds of argument (<see cref="IsBatchFile"/>). Null when not found.
    /// </summary>
    public static string? Find(string? configured, string t3Home, ToolEnvironment env)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.GetFullPath(AgentOptions.ExpandHome(configured.Trim(), env.Home));
            return File.Exists(path) ? path : null;
        }

        string[] names = env.IsWindows ? ["t3.exe", "t3.cmd", "t3.bat", "t3"] : ["t3"];
        IEnumerable<string> folders = [Path.Combine(t3Home, "bin"), .. env.PathEntries.Where(p => p.Length > 0 && Path.IsPathRooted(p))];
        foreach (var folder in folders)
        {
            foreach (var name in names)
            {
                try
                {
                    var file = Path.Combine(folder, name);
                    if (File.Exists(file))
                    {
                        return file;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    // A PATH entry that is not a valid folder name.
                }
            }
        }

        return null;
    }

    /// <summary>A <c>.cmd</c> or <c>.bat</c>: Windows runs it through cmd.exe, which reads some characters in its arguments as its own.</summary>
    public static bool IsBatchFile(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>Characters cmd.exe would act on even inside quotes, or that end the quoting.</summary>
    public static bool HasBatchMetacharacters(string value) => value.Any(c => c is '"' or '%' or '!' or '^' or '&' or '|' or '<' or '>' || char.IsControl(c));

    /// <summary>Marks the worktrees that live under T3 Code's worktrees folder. The others are left exactly as they were.</summary>
    public static IReadOnlyList<WorktreeInfo> MarkOwned(IReadOnlyList<WorktreeInfo> worktrees, string worktreesFolder)
    {
        if (worktrees.Count < 2)
        {
            return worktrees;
        }

        // Git reports real paths (/private/var/… on macOS), so both spellings of the folder are compared.
        string[] prefixes = [.. new[] { Path.GetFullPath(worktreesFolder), Actions.PathSafety.Resolve(worktreesFolder) }
            .Select(p => Path.TrimEndingDirectorySeparator(p) + Path.DirectorySeparatorChar).Distinct()];
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return worktrees.Select((w, i) => i > 0 && IsUnder(w.Path) ? w with { T3Owned = true } : w).ToList();

        bool IsUnder(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                return prefixes.Any(p => full.StartsWith(p, comparison) || Actions.PathSafety.Resolve(full).StartsWith(p, comparison));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }
}
