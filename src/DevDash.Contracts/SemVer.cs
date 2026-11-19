using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace DevDash.Contracts;

/// <summary>
/// A strict semantic version: <c>MAJOR.MINOR.PATCH</c> with an optional <c>-prerelease</c>. No leading
/// <c>v</c>, no build metadata, no leading zeros. This is the only shape a release tag, an update
/// action or a downloaded <c>version.txt</c> may have, so nothing else ever reaches a URL or a path.
/// </summary>
public sealed partial record SemVer : IComparable<SemVer>
{
    public const int MaxLength = 64;

    [GeneratedRegex(@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    private SemVer(int major, int minor, int patch, string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>Null for a normal release.</summary>
    public string? Prerelease { get; }

    public bool IsPrerelease => Prerelease is not null;

    public static bool TryParse(string? text, out SemVer version)
    {
        version = null!;
        if (text is null || text.Length > MaxLength || Pattern().Match(text) is not { Success: true } match)
        {
            return false;
        }

        version = new SemVer(
            int.Parse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture),
            match.Groups[4].Success ? match.Groups[4].Value : null);
        return true;
    }

    public static SemVer? ParseOrNull(string? text) => TryParse(text, out var version) ? version : null;

    /// <summary>True when <paramref name="text"/> is a strict version, exactly as <see cref="ToString"/> would print it.</summary>
    public static bool IsValid(string? text) => TryParse(text, out _);

    /// <summary>Semantic Versioning 2.0 precedence: a pre-release sorts before its release.</summary>
    public int CompareTo(SemVer? other)
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
            var aNumeric = a.All(char.IsAsciiDigit);
            var bNumeric = b.All(char.IsAsciiDigit);
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

    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;
    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;
    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;
    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;

    public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString() => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
