using System.Globalization;
using System.Text.Json;
using DevDash.Agent.Scanning;

namespace DevDash.Agent.Dependencies;

/// <summary>One package at one version, as restore resolved it.</summary>
public sealed record PackageVersion(string Id, string Version);

/// <summary>
/// What restore already wrote into a project's <c>obj/project.assets.json</c>: every package it resolved
/// (direct and transitive) and the project's own (top-level) package references. Reading this file is
/// how the NuGet check works without running <c>dotnet</c> inside the repository: that would let the
/// repository's <c>global.json</c> choose the SDK (its <c>sdk.paths</c> can point inside the repo), have
/// MSBuild evaluate its <c>Directory.Build.props</c>, and send requests to whatever feeds its
/// <c>nuget.config</c> names.
/// </summary>
public static class NuGetAssets
{
    /// <summary>The largest assets file read. A large solution's are a few megabytes.</summary>
    public const int MaxBytes = 64 * 1024 * 1024;

    private const int MaxDepth = 6;
    private const int MaxDirectories = 4000;
    private const int MaxFiles = 200;

    /// <summary>The projects in the repository and the assets files restore left for them, found without following links.</summary>
    public sealed record Found(IReadOnlyList<string> Projects, IReadOnlyList<string> AssetsFiles);

    /// <summary>
    /// Walks the repository (bounded in depth and size, no links, no hidden folders or <c>node_modules</c>)
    /// for project files and for <c>project.assets.json</c> in an <c>obj</c> folder: beside the project,
    /// or one level down in <c>artifacts/obj/&lt;project&gt;/</c> when the repo uses the artifacts layout.
    /// </summary>
    public static Found Find(string repo)
    {
        var projects = new List<string>();
        var assets = new List<string>();
        var queue = new Queue<(string Dir, int Depth)>([(repo, 0)]);
        var visited = 0;
        while (queue.Count > 0 && visited++ < MaxDirectories && projects.Count < MaxFiles && assets.Count < MaxFiles)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
                {
                    if (Path.GetExtension(file).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj")
                    {
                        projects.Add(file);
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(child);
                    if (name.StartsWith('.') || name is "node_modules" or "bin" or "packages" || new DirectoryInfo(child).LinkTarget is not null)
                    {
                        continue;
                    }

                    if (name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    {
                        AddAssets(repo, child, assets);
                        foreach (var inner in Directory.EnumerateDirectories(child).Order(StringComparer.Ordinal).Take(MaxFiles))
                        {
                            AddAssets(repo, inner, assets);
                        }

                        continue;
                    }

                    if (depth + 1 <= MaxDepth)
                    {
                        queue.Enqueue((child, depth + 1));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable folder: what it holds is not counted.
            }
        }

        return new Found(projects, assets.Distinct(StringComparer.Ordinal).ToList());
    }

    private static void AddAssets(string repo, string dir, List<string> assets)
    {
        var file = Path.Combine(dir, "project.assets.json");
        if (assets.Count < MaxFiles && RepoFile.IsSafe(repo, file))
        {
            assets.Add(file);
        }
    }

    /// <summary>The packages in one assets file, or null when it is not one restore wrote (or not readable).</summary>
    public static (IReadOnlyList<PackageVersion> Resolved, IReadOnlyList<PackageVersion> TopLevel)? Parse(ReadOnlySpan<byte> json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // "libraries" holds every package restore resolved, for every framework: "Id/Version" → { "type": "package" }.
            var resolved = new List<PackageVersion>();
            foreach (var library in libraries.EnumerateObject())
            {
                var slash = library.Name.IndexOf('/');
                if (slash > 0 && library.Value.ValueKind == JsonValueKind.Object && Text(library.Value, "type") == "package"
                    && NuGetVersion.TryParse(library.Name[(slash + 1)..], out _))
                {
                    resolved.Add(new PackageVersion(library.Name[..slash], library.Name[(slash + 1)..]));
                }
            }

            // The project's own references are in "project.frameworks.<tfm>.dependencies". The SDK's implicit
            // (auto-referenced) packages are versioned by the SDK, not the project, so they are left out of "outdated".
            var topLevel = new List<PackageVersion>();
            if (root.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object
                && project.TryGetProperty("frameworks", out var frameworks) && frameworks.ValueKind == JsonValueKind.Object)
            {
                foreach (var framework in frameworks.EnumerateObject())
                {
                    if (framework.Value.ValueKind != JsonValueKind.Object || !framework.Value.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (var dependency in dependencies.EnumerateObject())
                    {
                        var value = dependency.Value;
                        if (value.ValueKind != JsonValueKind.Object
                            || !string.Equals(Text(value, "target"), "Package", StringComparison.OrdinalIgnoreCase)
                            || (value.TryGetProperty("autoReferenced", out var auto) && auto.ValueKind == JsonValueKind.True))
                        {
                            continue;
                        }

                        // The version restore chose for it (the one in "libraries" with that id; the highest, if frameworks differ).
                        var chosen = resolved.Where(p => string.Equals(p.Id, dependency.Name, StringComparison.OrdinalIgnoreCase))
                            .Select(p => (p, Version: NuGetVersion.Parse(p.Version)))
                            .OrderByDescending(x => x.Version)
                            .Select(x => x.p)
                            .FirstOrDefault();
                        if (chosen is not null)
                        {
                            topLevel.Add(chosen);
                        }
                    }
                }
            }

            return (resolved, topLevel);
        }
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// A NuGet package version (<c>1.2.3</c>, <c>1.2.3.4</c>, <c>2.0.0-beta.1</c>; build metadata ignored),
/// ordered the way NuGet orders them: numbers, then a release above its prereleases, then the
/// prerelease labels as SemVer 2 compares them.
/// </summary>
public sealed record NuGetVersion(long[] Numbers, string[] Labels) : IComparable<NuGetVersion>
{
    public bool IsPrerelease => Labels.Length > 0;

    public static NuGetVersion Parse(string text) => TryParse(text, out var version) ? version : new NuGetVersion([0, 0, 0, 0], []);

    public static bool TryParse(string? text, out NuGetVersion version)
    {
        version = new NuGetVersion([0, 0, 0, 0], []);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        var dash = value.IndexOf('-');
        var core = dash >= 0 ? value[..dash] : value;
        var labels = dash >= 0 ? value[(dash + 1)..].Split('.') : [];
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 4 || labels.Any(l => l.Length == 0))
        {
            return false;
        }

        var numbers = new long[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new NuGetVersion(numbers, labels);
        return true;
    }

    public int CompareTo(NuGetVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (var i = 0; i < 4; i++)
        {
            var c = Numbers[i].CompareTo(other.Numbers[i]);
            if (c != 0)
            {
                return c;
            }
        }

        if (Labels.Length == 0 || other.Labels.Length == 0)
        {
            return Math.Sign(other.Labels.Length.CompareTo(Labels.Length)); // a release is above its prereleases
        }

        for (var i = 0; i < Math.Min(Labels.Length, other.Labels.Length); i++)
        {
            var a = Labels[i];
            var b = other.Labels[i];
            var aNum = long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNum = long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bn);
            var c = (aNum, bNum) switch
            {
                (true, true) => an.CompareTo(bn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(a, b, StringComparison.OrdinalIgnoreCase),
            };
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return Labels.Length.CompareTo(other.Labels.Length);
    }

    public bool Equals(NuGetVersion? other) => other is not null && CompareTo(other) == 0;

    public override int GetHashCode() => HashCode.Combine(Numbers[0], Numbers[1], Numbers[2], Numbers[3], Labels.Length);
}

/// <summary>
/// A NuGet version range as the vulnerability data writes them: <c>[1.0.0, 2.0.0)</c>, <c>(, 1.2.3)</c>,
/// <c>[1.0.0]</c>, or a bare <c>1.0.0</c> (that version and up).
/// </summary>
public sealed record NuGetVersionRange(NuGetVersion? Min, bool MinInclusive, NuGetVersion? Max, bool MaxInclusive)
{
    public static bool TryParse(string? text, out NuGetVersionRange range)
    {
        range = new NuGetVersionRange(null, false, null, false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value[0] is not ('[' or '('))
        {
            if (!NuGetVersion.TryParse(value, out var floor))
            {
                return false;
            }

            range = new NuGetVersionRange(floor, true, null, false);
            return true;
        }

        if (value.Length < 2 || value[^1] is not (']' or ')'))
        {
            return false;
        }

        var minInclusive = value[0] == '[';
        var maxInclusive = value[^1] == ']';
        var inner = value[1..^1];
        var comma = inner.IndexOf(',');
        if (comma < 0)
        {
            // "[1.0.0]": exactly that version.
            if (!minInclusive || !maxInclusive || !NuGetVersion.TryParse(inner, out var exact))
            {
                return false;
            }

            range = new NuGetVersionRange(exact, true, exact, true);
            return true;
        }

        NuGetVersion? min = null, max = null;
        var low = inner[..comma].Trim();
        var high = inner[(comma + 1)..].Trim();
        if ((low.Length > 0 && !NuGetVersion.TryParse(low, out min)) || (high.Length > 0 && !NuGetVersion.TryParse(high, out max)))
        {
            return false;
        }

        range = new NuGetVersionRange(low.Length > 0 ? min : null, minInclusive, high.Length > 0 ? max : null, maxInclusive);
        return true;
    }

    public bool Contains(NuGetVersion version)
    {
        if (Min is not null && (MinInclusive ? version.CompareTo(Min) < 0 : version.CompareTo(Min) <= 0))
        {
            return false;
        }

        return Max is null || (MaxInclusive ? version.CompareTo(Max) <= 0 : version.CompareTo(Max) < 0);
    }
}
