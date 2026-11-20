using System.Globalization;
using System.Numerics;

namespace DevDash.Core.Toolchains;

/// <summary>
/// A tool's version, read leniently: <c>v20.11.0</c>, <c>3.12</c>, <c>21</c>, <c>10.0.100-rc.2.25502.107</c>,
/// <c>17.0.11+9</c>. Parts that are not written are remembered as missing, so <c>3.12</c> can be used as a
/// prefix; for ordering they count as zero.
/// </summary>
public sealed record ToolVersion : IComparable<ToolVersion>
{
    private ToolVersion(int major, int? minor, int? patch, string? prerelease)
    {
        Major = major;
        MinorPart = minor;
        PatchPart = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int? MinorPart { get; }
    public int? PatchPart { get; }
    public int Minor => MinorPart ?? 0;
    public int Patch => PatchPart ?? 0;

    /// <summary>Null for a release.</summary>
    public string? Prerelease { get; }

    public bool IsPrerelease => Prerelease is not null;

    /// <summary>True when major, minor and patch were all written.</summary>
    public bool IsFull => MinorPart is not null && PatchPart is not null;

    public static ToolVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (value.Length > 64)
        {
            return null;
        }

        if (value[0] is 'v' or 'V')
        {
            value = value[1..];
        }

        // Build metadata never takes part in a comparison.
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        string? prerelease = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..];
            value = value[..dash];
            if (prerelease.Length == 0 || !prerelease.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            {
                return null;
            }
        }

        var parts = value.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return null;
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length is 0 or > 9 || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        // A fourth part (Java's 17.0.11.1) is dropped: nothing here compares that finely.
        return new ToolVersion(numbers[0], numbers.Length > 1 ? numbers[1] : null, numbers.Length > 2 ? numbers[2] : null, prerelease);
    }

    /// <summary>
    /// True when this version, read as a prefix, covers <paramref name="other"/>: <c>3.12</c> covers
    /// <c>3.12.4</c>, <c>20</c> covers <c>20.11.0</c>, and a full version covers only itself.
    /// </summary>
    public bool IsPrefixOf(ToolVersion other) =>
        Major == other.Major
        && (MinorPart is null || MinorPart == other.Minor)
        && (PatchPart is null || PatchPart == other.Patch)
        && (Prerelease is null ? !other.IsPrerelease || !IsFull : Prerelease == other.Prerelease);

    /// <summary>Semantic Versioning precedence: a pre-release sorts before its release.</summary>
    public int CompareTo(ToolVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Major.CompareTo(other.Major);
        result = result != 0 ? result : Minor.CompareTo(other.Minor);
        result = result != 0 ? result : Patch.CompareTo(other.Patch);
        if (result != 0 || Prerelease == other.Prerelease)
        {
            return result;
        }

        if (Prerelease is null)
        {
            return 1;
        }

        if (other.Prerelease is null)
        {
            return -1;
        }

        var mine = Prerelease.Split('.');
        var theirs = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(mine.Length, theirs.Length); index++)
        {
            var a = mine[index];
            var b = theirs[index];
            var aNumeric = a.Length > 0 && a.All(char.IsAsciiDigit);
            var bNumeric = b.Length > 0 && b.All(char.IsAsciiDigit);
            var part = aNumeric && bNumeric ? BigInteger.Parse(a, CultureInfo.InvariantCulture).CompareTo(BigInteger.Parse(b, CultureInfo.InvariantCulture))
                : aNumeric ? -1
                : bNumeric ? 1
                : string.CompareOrdinal(a, b);
            if (part != 0)
            {
                return Math.Sign(part);
            }
        }

        return mine.Length.CompareTo(theirs.Length);
    }

    public bool SameAs(ToolVersion other) => CompareTo(other) == 0;

    public override string ToString()
    {
        var text = Major.ToString(CultureInfo.InvariantCulture);
        if (MinorPart is { } minor)
        {
            text += "." + minor.ToString(CultureInfo.InvariantCulture);
            if (PatchPart is { } patch)
            {
                text += "." + patch.ToString(CultureInfo.InvariantCulture);
            }
        }

        return Prerelease is null ? text : $"{text}-{Prerelease}";
    }

    /// <summary>Parses, drops what does not parse, and sorts newest first without duplicates.</summary>
    public static List<ToolVersion> ParseAll(IEnumerable<string> versions)
    {
        var result = new List<ToolVersion>();
        foreach (var parsed in versions.Select(Parse))
        {
            if (parsed is not null && !result.Any(parsed.SameAs))
            {
                result.Add(parsed);
            }
        }

        result.Sort((a, b) => b.CompareTo(a));
        return result;
    }
}
