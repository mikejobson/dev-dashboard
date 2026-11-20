using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevDash.Agent.Disk;

/// <summary>One line of <c>docker system df --format json</c>.</summary>
public sealed record DockerDfRow(string Type, int? TotalCount, int? Active, long? Size, long? Reclaimable);

/// <summary>Reads what the package and container tools print. Pure, and tested on captured output.</summary>
public static partial class DiskParsers
{
    /// <summary>
    /// <c>dotnet nuget locals all --list</c>: <c>http-cache: /Users/me/.local/share/NuGet/http-cache</c>, one per line,
    /// by kind (<c>global-packages</c>, <c>http-cache</c>, <c>temp</c>, <c>plugins-cache</c>). Older SDKs prefix lines with <c>info : </c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NuGetLocals(string output)
    {
        var locals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("info :", StringComparison.OrdinalIgnoreCase))
            {
                line = line[6..].Trim();
            }

            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var kind = line[..colon].Trim();
            var path = line[(colon + 2)..].Trim();
            if (kind.Length > 0 && !kind.Contains(' ') && path.Length > 0)
            {
                locals[kind] = path.TrimEnd('/', '\\') is { Length: > 0 } trimmed ? trimmed : path;
            }
        }

        return locals;
    }

    /// <summary>The single path a tool printed (<c>npm config get cache</c>, <c>pnpm store path</c>, <c>brew --cache</c>): the last non-empty line, when it is absolute.</summary>
    public static string? SinglePath(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return line is not null && DevServers.DevServerCollector.LooksAbsolute(line) ? line.TrimEnd('/', '\\') : null;
    }

    /// <summary><c>docker system df --format json</c>: one JSON object per line (Images, Containers, Local Volumes, Build Cache).</summary>
    public static IReadOnlyList<DockerDfRow> DockerDf(string output)
    {
        var rows = new List<DockerDfRow>();
        foreach (var line in JsonLines(output))
        {
            if (Str(line, "Type") is not { } type)
            {
                continue;
            }

            rows.Add(new DockerDfRow(
                type,
                int.TryParse(Str(line, "TotalCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) ? total : null,
                int.TryParse(Str(line, "Active"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var active) ? active : null,
                DockerSize(Str(line, "Size")),
                DockerSize(Str(line, "Reclaimable"))));
        }

        return rows;
    }

    /// <summary>
    /// <c>docker image ls --filter dangling=true --format json</c>: the sum of <c>Size</c>, an upper bound on what
    /// <c>docker image prune</c> frees (layers shared with other images stay).
    /// </summary>
    public static (int Count, long Bytes) DanglingImages(string output)
    {
        var count = 0;
        long bytes = 0;
        foreach (var line in JsonLines(output))
        {
            count++;
            bytes += DockerSize(Str(line, "Size")) ?? 0;
        }

        return (count, bytes);
    }

    [GeneratedRegex(@"^\s*([0-9]+(?:\.[0-9]+)?)\s*([kKMGTP]?i?B)\b")]
    private static partial Regex SizePattern();

    /// <summary>Docker's human sizes: <c>2.289GB</c>, <c>127kB</c>, <c>0B</c>, <c>12.59GB (99%)</c>. Decimal units, as Docker prints them.</summary>
    public static long? DockerSize(string? text)
    {
        if (text is null || SizePattern().Match(text) is not { Success: true } match)
        {
            return null;
        }

        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var unit = match.Groups[2].Value;
        var binary = unit.Contains('i');
        double step = binary ? 1024 : 1000;
        var power = char.ToUpperInvariant(unit[0]) switch
        {
            'K' => 1,
            'M' => 2,
            'G' => 3,
            'T' => 4,
            'P' => 5,
            _ => 0,
        };
        return (long)Math.Round(value * Math.Pow(step, power));
    }

    private static IEnumerable<JsonElement> JsonLines(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{'))
            {
                continue;
            }

            JsonElement element;
            try
            {
                using var doc = JsonDocument.Parse(line);
                element = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            yield return element;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}
