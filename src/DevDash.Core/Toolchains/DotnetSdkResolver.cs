namespace DevDash.Core.Toolchains;

/// <summary>The values of <c>sdk.rollForward</c> in <c>global.json</c>, in the order the .NET host defines them.</summary>
public enum RollForwardPolicy
{
    Disable,
    Patch,
    Feature,
    Minor,
    Major,
    LatestPatch,
    LatestFeature,
    LatestMinor,
    LatestMajor,
}

/// <summary>What <c>global.json</c> resolves to on a machine.</summary>
/// <param name="Sdk">The SDK the <c>dotnet</c> command would use. Null when none satisfies the file.</param>
/// <param name="Invalid">Set when the file itself cannot be honoured (an unknown policy, a version that is not one): the answer is unknown, not "missing".</param>
public sealed record SdkResolution(ToolVersion? Sdk, string? Invalid = null);

/// <summary>
/// Picks the .NET SDK a <c>global.json</c> selects from the SDKs installed, the way the .NET host does
/// (<c>hostfxr</c>'s SDK resolver). An SDK version is <c>major.minor.BPP</c>: the hundreds of the third
/// number are the feature band, the rest the patch.
/// <list type="bullet">
/// <item><c>disable</c>: exactly the version asked for.</item>
/// <item><c>patch</c>: the version asked for if installed, else the latest patch of the same feature band, not older than asked.</item>
/// <item><c>feature</c>, <c>minor</c>, <c>major</c>: the latest patch of the lowest feature band that is not older than asked,
/// staying inside the same major.minor, the same major, or going anywhere.</item>
/// <item><c>latestPatch</c>, <c>latestFeature</c>, <c>latestMinor</c>, <c>latestMajor</c>: the highest SDK that is not older than
/// asked, inside the same feature band, the same major.minor, the same major, or anywhere.</item>
/// </list>
/// With a version and no policy the host uses <c>latestPatch</c>; with neither, the highest SDK. A
/// pre-release SDK is only considered when <c>allowPrerelease</c> is true (the default on the command
/// line) or the version asked for is itself a pre-release.
/// </summary>
public static class DotnetSdkResolver
{
    public static bool TryParsePolicy(string? text, out RollForwardPolicy policy)
    {
        policy = RollForwardPolicy.LatestPatch;
        return text is not null && Enum.TryParse(text.Trim(), ignoreCase: true, out policy) && Enum.IsDefined(policy) && !text.Trim().All(char.IsAsciiDigit);
    }

    public static SdkResolution Resolve(string? requestedVersion, string? rollForward, bool? allowPrerelease, IEnumerable<string> installed)
    {
        ToolVersion? requested = null;
        if (!string.IsNullOrWhiteSpace(requestedVersion))
        {
            requested = ToolVersion.Parse(requestedVersion);
            if (requested is null || !requested.IsFull)
            {
                return new SdkResolution(null, $"'{requestedVersion}' is not an SDK version");
            }
        }

        RollForwardPolicy policy;
        if (string.IsNullOrWhiteSpace(rollForward))
        {
            policy = requested is null ? RollForwardPolicy.LatestMajor : RollForwardPolicy.LatestPatch;
        }
        else if (!TryParsePolicy(rollForward, out policy))
        {
            return new SdkResolution(null, $"'{rollForward}' is not a rollForward policy");
        }
        else if (requested is null && policy != RollForwardPolicy.LatestMajor)
        {
            return new SdkResolution(null, $"rollForward '{rollForward}' needs a version");
        }

        // Asking for a pre-release by name allows pre-releases, whatever the file says.
        var prerelease = (allowPrerelease ?? true) || requested is { IsPrerelease: true };
        var sdks = ToolVersion.ParseAll(installed).Where(v => v.IsFull).ToList();

        if (requested is not null && policy is RollForwardPolicy.Disable or RollForwardPolicy.Patch)
        {
            // An exact match wins outright, pre-release or not: it was named.
            if (sdks.FirstOrDefault(requested!.SameAs) is { } exact)
            {
                return new SdkResolution(exact);
            }

            if (policy == RollForwardPolicy.Disable)
            {
                return new SdkResolution(null);
            }
        }

        ToolVersion? best = null;
        foreach (var sdk in sdks)
        {
            if (Matches(sdk, requested, policy, prerelease) && (best is null || IsBetter(sdk, best, requested, policy)))
            {
                best = sdk;
            }
        }

        return new SdkResolution(best);
    }

    private static int Band(ToolVersion version) => version.Patch / 100;

    private static bool Matches(ToolVersion sdk, ToolVersion? requested, RollForwardPolicy policy, bool allowPrerelease)
    {
        if (sdk.IsPrerelease && !allowPrerelease)
        {
            return false;
        }

        if (requested is null)
        {
            return true;
        }

        var sameMajor = sdk.Major == requested.Major;
        var sameMinor = sameMajor && sdk.Minor == requested.Minor;
        var sameBand = sameMinor && Band(sdk) == Band(requested);
        var inScope = policy switch
        {
            RollForwardPolicy.Patch or RollForwardPolicy.LatestPatch => sameBand,
            RollForwardPolicy.Feature or RollForwardPolicy.LatestFeature => sameMinor,
            RollForwardPolicy.Minor or RollForwardPolicy.LatestMinor => sameMajor,
            RollForwardPolicy.Major or RollForwardPolicy.LatestMajor => true,
            _ => false,
        };

        return inScope && sdk.CompareTo(requested) >= 0;
    }

    private static bool IsBetter(ToolVersion current, ToolVersion previous, ToolVersion? requested, RollForwardPolicy policy)
    {
        // The later one when the policy is a "latest" one, or when the two share a feature band (the latest patch always wins).
        if (requested is null
            || policy > RollForwardPolicy.Major
            || (current.Major == previous.Major && current.Minor == previous.Minor && Band(current) == Band(previous)))
        {
            return current.CompareTo(previous) > 0;
        }

        // Otherwise roll forward as little as possible: the lower band.
        return current.CompareTo(previous) < 0;
    }

    /// <summary>The requirement in words: "10.0.100 or a later 10.0.1xx patch".</summary>
    public static string Describe(string version, string? rollForward)
    {
        if (ToolVersion.Parse(version) is not { IsFull: true } v)
        {
            return version;
        }

        if (!TryParsePolicy(string.IsNullOrWhiteSpace(rollForward) ? "latestPatch" : rollForward, out var policy))
        {
            return version;
        }

        return policy switch
        {
            RollForwardPolicy.Disable => $"exactly {version}",
            RollForwardPolicy.Patch or RollForwardPolicy.LatestPatch => $"{version} or a later {v.Major}.{v.Minor}.{Band(v)}xx patch",
            RollForwardPolicy.Feature or RollForwardPolicy.LatestFeature => $"{version} or a later {v.Major}.{v.Minor} SDK",
            RollForwardPolicy.Minor or RollForwardPolicy.LatestMinor => $"{version} or a later {v.Major}.x SDK",
            _ => $"{version} or any later SDK",
        };
    }
}
