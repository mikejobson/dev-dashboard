using DevDash.Core.Toolchains;

namespace DevDash.Core.Tests.Toolchains;

/// <summary>
/// The resolver against the real thing. This table was recorded from the .NET host itself: on a machine
/// with SDKs 8.0.415 and 11.0.100, a global.json with each version and policy was written to an empty
/// folder and <c>dotnet --version</c> run there (<c>null</c>: it refused to pick one; <c>NONE</c>: no
/// rollForward in the file).
/// </summary>
public class DotnetHostRecordedTests
{
    private static readonly string[] Installed = ["8.0.415", "11.0.100"];

    private const string Recorded = """
        8.0.100 disable null
        8.0.100 patch null
        8.0.100 feature 8.0.415
        8.0.100 minor 8.0.415
        8.0.100 major 8.0.415
        8.0.100 latestPatch null
        8.0.100 latestFeature 8.0.415
        8.0.100 latestMinor 8.0.415
        8.0.100 latestMajor 11.0.100
        8.0.100 NONE null
        8.0.400 disable null
        8.0.400 patch 8.0.415
        8.0.400 feature 8.0.415
        8.0.400 minor 8.0.415
        8.0.400 major 8.0.415
        8.0.400 latestPatch 8.0.415
        8.0.400 latestFeature 8.0.415
        8.0.400 latestMinor 8.0.415
        8.0.400 latestMajor 11.0.100
        8.0.400 NONE 8.0.415
        8.0.415 disable 8.0.415
        8.0.415 patch 8.0.415
        8.0.415 feature 8.0.415
        8.0.415 minor 8.0.415
        8.0.415 major 8.0.415
        8.0.415 latestPatch 8.0.415
        8.0.415 latestFeature 8.0.415
        8.0.415 latestMinor 8.0.415
        8.0.415 latestMajor 11.0.100
        8.0.415 NONE 8.0.415
        8.0.416 disable null
        8.0.416 patch null
        8.0.416 feature null
        8.0.416 minor null
        8.0.416 major 11.0.100
        8.0.416 latestPatch null
        8.0.416 latestFeature null
        8.0.416 latestMinor null
        8.0.416 latestMajor 11.0.100
        8.0.416 NONE null
        8.0.500 disable null
        8.0.500 patch null
        8.0.500 feature null
        8.0.500 minor null
        8.0.500 major 11.0.100
        8.0.500 latestPatch null
        8.0.500 latestFeature null
        8.0.500 latestMinor null
        8.0.500 latestMajor 11.0.100
        8.0.500 NONE null
        7.0.100 disable null
        7.0.100 patch null
        7.0.100 feature null
        7.0.100 minor null
        7.0.100 major 8.0.415
        7.0.100 latestPatch null
        7.0.100 latestFeature null
        7.0.100 latestMinor null
        7.0.100 latestMajor 11.0.100
        7.0.100 NONE null
        9.0.100 disable null
        9.0.100 patch null
        9.0.100 feature null
        9.0.100 minor null
        9.0.100 major 11.0.100
        9.0.100 latestPatch null
        9.0.100 latestFeature null
        9.0.100 latestMinor null
        9.0.100 latestMajor 11.0.100
        9.0.100 NONE null
        11.0.100 disable 11.0.100
        11.0.100 patch 11.0.100
        11.0.100 feature 11.0.100
        11.0.100 minor 11.0.100
        11.0.100 major 11.0.100
        11.0.100 latestPatch 11.0.100
        11.0.100 latestFeature 11.0.100
        11.0.100 latestMinor 11.0.100
        11.0.100 latestMajor 11.0.100
        11.0.100 NONE 11.0.100
        11.0.101 disable null
        11.0.101 patch null
        11.0.101 feature null
        11.0.101 minor null
        11.0.101 major null
        11.0.101 latestPatch null
        11.0.101 latestFeature null
        11.0.101 latestMinor null
        11.0.101 latestMajor null
        11.0.101 NONE null
        12.0.100 disable null
        12.0.100 patch null
        12.0.100 feature null
        12.0.100 minor null
        12.0.100 major null
        12.0.100 latestPatch null
        12.0.100 latestFeature null
        12.0.100 latestMinor null
        12.0.100 latestMajor null
        12.0.100 NONE null
        8.0.300 disable null
        8.0.300 patch null
        8.0.300 feature 8.0.415
        8.0.300 minor 8.0.415
        8.0.300 major 8.0.415
        8.0.300 latestPatch null
        8.0.300 latestFeature 8.0.415
        8.0.300 latestMinor 8.0.415
        8.0.300 latestMajor 11.0.100
        8.0.300 NONE null
        11.0.100-preview.1.2 disable null
        11.0.100-preview.1.2 patch 11.0.100
        11.0.100-preview.1.2 feature 11.0.100
        11.0.100-preview.1.2 minor 11.0.100
        11.0.100-preview.1.2 major 11.0.100
        11.0.100-preview.1.2 latestPatch 11.0.100
        11.0.100-preview.1.2 latestFeature 11.0.100
        11.0.100-preview.1.2 latestMinor 11.0.100
        11.0.100-preview.1.2 latestMajor 11.0.100
        11.0.100-preview.1.2 NONE 11.0.100
        """;

    public static TheoryData<string, string?, string?> Cases()
    {
        var data = new TheoryData<string, string?, string?>();
        foreach (var line in Recorded.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ');
            data.Add(parts[0], parts[1] == "NONE" ? null : parts[1], parts[2] == "null" ? null : parts[2]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Agrees_with_what_dotnet_itself_chose(string version, string? rollForward, string? expected) =>
        Assert.Equal(expected, DotnetSdkResolver.Resolve(version, rollForward, null, Installed).Sdk?.ToString());

    [Fact]
    public void The_recording_is_all_there() => Assert.Equal(120, Cases().Count);
}
