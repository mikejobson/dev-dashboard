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
/// Reduces the package tools' JSON to counts by severity and the top few package names and advisory
/// ids. Nothing else of their output is kept or sent. Pure, so the tests feed it captured text.
/// </summary>
public static class DependencyParsers
{
    public const string NotRestored = "not restored (run dotnet restore there)";
    public const string Unreachable = "the package source could not be reached";
    private const string Unreadable = "the tool's answer could not be read";

    private static readonly string[] Order = ["critical", "high", "moderate", "low"];

    // ---- dotnet list package --format json --------------------------------------------------------

    public static VulnerableResult DotnetVulnerable(string output)
    {
        if (!TryParseDotnet(output, out var packages, out var skipped, out var transient))
        {
            return new VulnerableResult(null, [], skipped, transient);
        }

        // One package can be in many projects and frameworks: it counts once, at its worst severity.
        var found = new Dictionary<string, (string Name, string? Version, int Rank, SortedSet<string> Advisories)>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (!package.TryGetProperty("vulnerabilities", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0 || Text(package, "id") is not { } id)
            {
                continue;
            }

            var version = Text(package, "resolvedVersion");
            var key = $"{id}@{version}";
            (string Name, string? Version, int Rank, SortedSet<string> Advisories) entry =
                found.TryGetValue(key, out var existing) ? existing : (id, version, Order.Length - 1, new SortedSet<string>(StringComparer.Ordinal));
            foreach (var vulnerability in list.EnumerateArray())
            {
                if (vulnerability.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                entry.Rank = Math.Min(entry.Rank, Rank(Text(vulnerability, "severity")));
                if (AdvisoryId(Text(vulnerability, "advisoryurl")) is { } advisory)
                {
                    entry.Advisories.Add(advisory);
                }
            }

            found[key] = entry;
        }

        return Vulnerable(found.Values.Select(f => (f.Name, f.Version, f.Rank, (IEnumerable<string>)f.Advisories)), skipped);
    }

    public static OutdatedResult DotnetOutdated(string output)
    {
        if (!TryParseDotnet(output, out var packages, out var skipped, out var transient))
        {
            return new OutdatedResult(null, [], skipped, transient);
        }

        var found = new SortedDictionary<string, OutdatedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (Text(package, "id") is { } id && Text(package, "latestVersion") is { } latest && Text(package, "resolvedVersion") is var current && !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase))
            {
                found.TryAdd(id, new OutdatedPackage { Name = Clip(id)!, Current = Clip(current), Latest = Clip(latest) });
            }
        }

        return new OutdatedResult(found.Count, found.Values.Take(DependencyHealth.MaxNames).ToList(), skipped);
    }

    /// <summary>Every package element of every project and framework. False when there is nothing usable, with the reason.</summary>
    private static bool TryParseDotnet(string output, out List<JsonElement> packages, out string? skipped, out bool transient)
    {
        packages = [];
        skipped = null;
        transient = false;
        if (Json(output) is not { } root)
        {
            skipped = Unreadable;
            return false;
        }

        var projects = 0;
        var withPackages = 0;
        if (root.TryGetProperty("projects", out var projectList) && projectList.ValueKind == JsonValueKind.Array)
        {
            foreach (var project in projectList.EnumerateArray())
            {
                projects++;
                if (project.ValueKind != JsonValueKind.Object || !project.TryGetProperty("frameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                withPackages++;
                foreach (var framework in frameworks.EnumerateArray())
                {
                    foreach (var list in (string[])["topLevelPackages", "transitivePackages"])
                    {
                        if (framework.ValueKind == JsonValueKind.Object && framework.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                        {
                            packages.AddRange(items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(i => i.Clone()));
                        }
                    }
                }
            }
        }

        var problems = new List<string>();
        if (root.TryGetProperty("problems", out var problemList) && problemList.ValueKind == JsonValueKind.Array)
        {
            problems.AddRange(problemList.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(p => Text(p, "text") ?? ""));
        }

        var notRestored = problems.Count(p => p.Contains("No assets file", StringComparison.OrdinalIgnoreCase) || p.Contains("run restore", StringComparison.OrdinalIgnoreCase));
        var unreachable = problems.Any(p => p.Contains("Unable to load the service index", StringComparison.OrdinalIgnoreCase)
            || p.Contains("No such host", StringComparison.OrdinalIgnoreCase)
            || p.Contains("could not be resolved", StringComparison.OrdinalIgnoreCase)
            || p.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        if (unreachable)
        {
            skipped = Unreachable;
            transient = true;
            return false;
        }

        if (notRestored > 0 && withPackages == 0)
        {
            skipped = NotRestored;
            return false;
        }

        if (projects == 0)
        {
            skipped = problems.Count > 0 ? "dotnet list reported a problem" : "no projects found";
            return false;
        }

        // Some projects answered: their numbers stand, with a note about the rest.
        skipped = notRestored > 0 ? $"{notRestored} of {projects} projects not restored" : null;
        return true;
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
