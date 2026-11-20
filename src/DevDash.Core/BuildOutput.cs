namespace DevDash.Core;

/// <summary>
/// The one list of folders and files a build, an install or the operating system recreates. The
/// agent's size scan uses it to find reclaimable space, and the safety analysis uses it to tell an
/// ignored build folder (nothing lost by deleting it) from an ignored file somebody wrote.
/// </summary>
public static class BuildOutput
{
    private static readonly HashSet<string> NoiseFiles = new(StringComparer.OrdinalIgnoreCase) { ".DS_Store", "Thumbs.db", "desktop.ini" };

    private static readonly HashSet<string> Caches = new(StringComparer.OrdinalIgnoreCase)
    {
        "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", ".vs", ".turbo", ".parcel-cache", ".eslintcache", ".gradle", ".nyc_output",
    };

    /// <summary>Files the operating system scatters about: never anybody's work.</summary>
    public static bool IsNoiseFile(string name) => NoiseFiles.Contains(name);

    /// <summary>
    /// Build and dependency folders. Ambiguous names only count next to the manifest that produces them,
    /// so a tracked <c>bin/</c> of scripts or a hand-written <c>dist/</c> is left alone.
    /// </summary>
    public static bool IsReclaimable(string name, IReadOnlySet<string> siblings) => IsReclaimable(name, () => siblings);

    /// <summary>As above, reading the sibling names only for a name that needs them.</summary>
    public static bool IsReclaimable(string name, Func<IReadOnlySet<string>> siblings)
    {
        switch (name.ToLowerInvariant())
        {
            case "node_modules":
            case ".venv":
            case ".next":
            // Framework caches and generated folders: dot-names no one writes by hand, rebuilt by the next build.
            case ".angular":
            case ".nuxt":
            case ".svelte-kit":
            case ".astro":
            case ".dart_tool":
                return true;
            case "bin":
            case "obj":
                return siblings().Any(s => s.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || s.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || s.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase));
            case "target":
                return siblings() is var t && (t.Contains("Cargo.toml") || t.Contains("pom.xml"));
            case "dist":
                return siblings() is var d && (d.Contains("package.json") || d.Contains("pyproject.toml") || d.Contains("setup.py"));
            default:
                return false;
        }
    }

    /// <summary>
    /// Folders a restore or a test run writes again, under names too ordinary to delete on sight:
    /// NuGet's solution-level <c>packages</c>, test reports, coverage reports. Only the safety analysis
    /// asks this, and only about paths git ignores, so that they are not listed as ignored files worth
    /// keeping. They are deliberately not <see cref="IsReclaimable(string, IReadOnlySet{string})"/>: the
    /// size scan does not count them and the agent never deletes them (a JavaScript monorepo keeps its
    /// source in <c>packages/</c>).
    /// </summary>
    /// <param name="name">The folder's name.</param>
    /// <param name="siblings">The names beside it.</param>
    /// <param name="repoRoot">The names in the repository's root, when that is another folder.</param>
    public static bool IsRecreated(string name, Func<IReadOnlySet<string>> siblings, Func<IReadOnlySet<string>>? repoRoot = null)
    {
        switch (name.ToLowerInvariant())
        {
            case "packages":
                return IsNuGetHome(siblings()) || (repoRoot is not null && IsNuGetHome(repoRoot()));
            case "test-results":
            case "playwright-report":
                return siblings().Contains("package.json");
            case "coverage":
                return siblings() is var c && (c.Contains("package.json") || c.Contains("pyproject.toml") || c.Contains("setup.py")
                    || c.Any(s => s.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
            default:
                return false;
        }
    }

    /// <summary>A folder with a solution or a <c>packages.config</c>: where NuGet's old-style restore puts <c>packages/</c>.</summary>
    private static bool IsNuGetHome(IReadOnlySet<string> names) =>
        names.Any(s => s.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || s.Equals("packages.config", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tool caches and OS noise that are recreated wherever they are, whatever sits beside them. Not
    /// counted as reclaimable space (they are small); only used so they do not show up as ignored
    /// files worth keeping. Editor settings (<c>.idea</c>, <c>.vscode</c>) are deliberately not here.
    /// </summary>
    public static bool IsCache(string name) =>
        NoiseFiles.Contains(name)
        || Caches.Contains(name)
        || name.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tsbuildinfo", StringComparison.OrdinalIgnoreCase);
}
