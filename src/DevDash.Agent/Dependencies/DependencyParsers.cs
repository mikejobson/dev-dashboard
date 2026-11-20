using System.Text.Json;
using DevDash.Contracts;

namespace DevDash.Agent.Dependencies;

/// <summary>What one run of a package tool came to: counts, a few names, or why there is nothing.</summary>
/// <param name="Skipped">Why there is no result, in a few words. Null when the tool answered.</param>
/// <param name="Transient">The failure is about the moment (offline, timed out), so it is worth asking again sooner.</param>
public sealed record VulnerableResult(SeverityCounts? Counts, IReadOnlyList<VulnerablePackage> Top, string? Skipped = null, bool Transient = false);

/// <inheritdoc cref="VulnerableResult"/>
public sealed record OutdatedResult(int? Count, IReadOnlyList<OutdatedPackage> Top, string? Skipped = null, bool Transient = false);

/// <summary>
/// Reduces npm's JSON, and NuGet's restore output checked against nuget.org's data, to counts by
/// severity and the top few package names and advisory ids. Nothing else is kept or sent. Pure, so the
/// tests feed it captured text.
/// </summary>
public static class DependencyParsers
{
    public const string NotRestored = "not restored (run dotnet restore there)";
    public const string Unreachable = "the package source could not be reached";
    private const string Unreadable = "the tool's answer could not be read";

    private static readonly string[] Order = ["critical", "high", "moderate", "low"];

    // ---- project.assets.json against nuget.org's data -------------------------------------------

    /// <summary>
    /// The resolved packages (direct and transitive, as restore wrote them) that nuget.org's vulnerability
    /// data lists at their version. One package at one version counts once, at its worst severity.
    /// </summary>
    public static VulnerableResult NuGetVulnerable(IEnumerable<PackageVersion> resolved, IReadOnlyDictionary<string, List<NuGetAdvisory>> advisories, string? note)
    {
        var found = new Dictionary<string, (string Name, string? Version, int Rank, SortedSet<string> Advisories)>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in resolved)
        {
            if (!advisories.TryGetValue(package.Id, out var list) || !NuGetVersion.TryParse(package.Version, out var version))
            {
                continue;
            }

            foreach (var advisory in list.Where(a => a.Versions.Contains(version)))
            {
                var key = $"{package.Id}@{package.Version}";
                (string Name, string? Version, int Rank, SortedSet<string> Advisories) entry =
                    found.TryGetValue(key, out var existing) ? existing : (package.Id, package.Version, Order.Length - 1, new SortedSet<string>(StringComparer.Ordinal));
                entry.Rank = Math.Min(entry.Rank, 3 - Math.Clamp(advisory.Severity, 0, 3)); // NuGet: 3 critical … 0 low
                if (AdvisoryId(advisory.Url) is { } id)
                {
                    entry.Advisories.Add(id);
                }

                found[key] = entry;
            }
        }

        return Vulnerable(found.Values.Select(f => (f.Name, f.Version, f.Rank, (IEnumerable<string>)f.Advisories)), note);
    }

    /// <summary>
    /// The top-level packages with a newer version on nuget.org: the newest stable one, or the newest of
    /// all when the project already uses a prerelease (as <c>dotnet list package --outdated</c> decides).
    /// </summary>
    public static OutdatedResult NuGetOutdated(IEnumerable<(PackageVersion Package, IReadOnlyList<string> Available)> packages, string? note)
    {
        var found = new SortedDictionary<string, OutdatedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var (package, available) in packages)
        {
            if (!NuGetVersion.TryParse(package.Version, out var current))
            {
                continue;
            }

            var latest = available
                .Select(text => (Text: text, Ok: NuGetVersion.TryParse(text, out var v), Version: v))
                .Where(x => x.Ok && (current.IsPrerelease || !x.Version.IsPrerelease))
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();
            if (latest.Text is not null && latest.Version.CompareTo(current) > 0)
            {
                found.TryAdd(package.Id, new OutdatedPackage { Name = Clip(package.Id)!, Current = Clip(package.Version), Latest = Clip(latest.Text) });
            }
        }

        return new OutdatedResult(found.Count, found.Values.Take(DependencyHealth.MaxNames).ToList(), note);
    }

    // ---- npm --json -------------------------------------------------------------------------------

    public static VulnerableResult NpmAudit(string output)
    {
        if (Json(output) is not { } root)
        {
            return new VulnerableResult(null, [], Unreadable);
        }

        if (NpmError(root) is { } error)
        {
            return new VulnerableResult(null, [], error.Reason, error.Transient);
        }

        var found = new List<(string Name, string? Version, int Rank, IEnumerable<string> Advisories)>();
        if (root.TryGetProperty("vulnerabilities", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            // npm 7 and later: one entry per affected package.
            foreach (var item in map.EnumerateObject().Where(i => i.Value.ValueKind == JsonValueKind.Object))
            {
                var advisories = new SortedSet<string>(StringComparer.Ordinal);
                if (item.Value.TryGetProperty("via", out var via) && via.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cause in via.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object))
                    {
                        if (AdvisoryId(Text(cause, "url")) is { } id)
                        {
                            advisories.Add(id);
                        }
                    }
                }

                found.Add((item.Name, null, Rank(Text(item.Value, "severity")), advisories));
            }
        }
        else if (root.TryGetProperty("advisories", out var old) && old.ValueKind == JsonValueKind.Object)
        {
            // npm 6: one entry per advisory.
            foreach (var item in old.EnumerateObject().Where(i => i.Value.ValueKind == JsonValueKind.Object))
            {
                var id = AdvisoryId(Text(item.Value, "url")) ?? AdvisoryId(Text(item.Value, "github_advisory_id"));
                found.Add((Text(item.Value, "module_name") ?? item.Name, null, Rank(Text(item.Value, "severity")), id is null ? [] : [id]));
            }
        }
        else if (!root.TryGetProperty("metadata", out _))
        {
            return new VulnerableResult(null, [], Unreadable);
        }

        var result = Vulnerable(found, null);

        // npm's own totals are the ones `npm audit` prints; prefer them when they are there.
        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("vulnerabilities", out var totals) && totals.ValueKind == JsonValueKind.Object)
        {
            int Of(string name) => totals.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var value) ? Math.Max(0, value) : 0;
            result = result with { Counts = new SeverityCounts { Critical = Of("critical"), High = Of("high"), Moderate = Of("moderate"), Low = Of("low") } };
        }

        return result;
    }

    public static OutdatedResult NpmOutdated(string output)
    {
        // Nothing outdated prints "{}", or with some versions nothing at all.
        if (string.IsNullOrWhiteSpace(output))
        {
            return new OutdatedResult(0, []);
        }

        if (Json(output) is not { } root)
        {
            return new OutdatedResult(null, [], Unreadable);
        }

        if (NpmError(root) is { } error)
        {
            return new OutdatedResult(null, [], error.Reason, error.Transient);
        }

        var found = new SortedDictionary<string, OutdatedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root.EnumerateObject())
        {
            // In a workspace one name can be listed once per place it is used.
            var value = item.Value.ValueKind == JsonValueKind.Array ? item.Value.EnumerateArray().FirstOrDefault() : item.Value;
            if (value.ValueKind != JsonValueKind.Object || Text(value, "latest") is not { } latest)
            {
                continue;
            }

            var current = Text(value, "current");
            if (!string.Equals(current, latest, StringComparison.Ordinal))
            {
                found.TryAdd(item.Name, new OutdatedPackage { Name = Clip(item.Name)!, Current = Clip(current), Latest = Clip(latest) });
            }
        }

        return new OutdatedResult(found.Count, found.Values.Take(DependencyHealth.MaxNames).ToList());
    }

    private static (string Reason, bool Transient)? NpmError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var code = Text(error, "code") ?? "";
        return code switch
        {
            "ENOTFOUND" or "EAI_AGAIN" or "ECONNREFUSED" or "ECONNRESET" or "ETIMEDOUT" or "ENETUNREACH" => (Unreachable, true),
            "ENOAUDIT" => ("the registry does not answer audit requests", false),
            "ENOLOCK" => ("no lockfile", false),
            "E401" or "E403" => ("the registry refused the request", false),
            _ => (code.Length is > 0 and <= 24 && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? $"npm reported {code}" : "npm reported an error", false),
        };
    }

    // ---- shared -----------------------------------------------------------------------------------

    private static VulnerableResult Vulnerable(IEnumerable<(string Name, string? Version, int Rank, IEnumerable<string> Advisories)> found, string? note)
    {
        var all = found.OrderBy(f => f.Rank).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new VulnerableResult(
            new SeverityCounts
            {
                Critical = all.Count(f => f.Rank == 0),
                High = all.Count(f => f.Rank == 1),
                Moderate = all.Count(f => f.Rank == 2),
                Low = all.Count(f => f.Rank == 3),
            },
            all.Take(DependencyHealth.MaxNames)
                .Select(f => new VulnerablePackage { Name = Clip(f.Name)!, Version = Clip(f.Version), Severity = Order[f.Rank], Advisories = f.Advisories.Take(3).ToList() })
                .ToList(),
            note);
    }

    /// <summary>0 critical … 3 low. NuGet says "Moderate", npm "moderate"; anything unrecognised counts as low.</summary>
    private static int Rank(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "critical" => 0,
        "high" => 1,
        "moderate" or "medium" => 2,
        _ => 3,
    };

    /// <summary>The advisory id at the end of an advisory address, when it looks like one (<c>GHSA-…</c>, <c>CVE-…</c>). Never the address itself.</summary>
    private static string? AdvisoryId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var id = url.TrimEnd('/');
        id = id[(id.LastIndexOf('/') + 1)..];
        return id.Length is > 4 and <= 40 && (id.StartsWith("GHSA-", StringComparison.OrdinalIgnoreCase) || id.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
            && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? id
            : null;
    }

    /// <summary>The first JSON object in the text: a tool may print a notice before it.</summary>
    private static JsonElement? Json(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(output.AsMemory(start), new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 64 });
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Clip(string? text) => text is { Length: > 100 } ? text[..100] : text;
}
