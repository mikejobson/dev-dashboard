using DevDash.Core.Toolchains;

namespace DevDash.Core.Tests.Toolchains;

/// <summary>
/// global.json's rollForward, case by case, against the rules the .NET host follows. An SDK version is
/// major.minor.BPP: B is the feature band, PP the patch.
/// </summary>
public class DotnetSdkResolverTests
{
    private static readonly string[] Installed =
    [
        "8.0.100", "8.0.110", "8.0.204", "8.0.303", "8.0.404",
        "9.0.100", "9.0.203", "9.0.305",
        "10.0.100", "10.0.102",
    ];

    private static string? Resolve(string? version, string? rollForward, bool? allowPrerelease = null, IEnumerable<string>? installed = null) =>
        DotnetSdkResolver.Resolve(version, rollForward, allowPrerelease, installed ?? Installed).Sdk?.ToString();

    [Theory]
    // disable: the exact version or nothing.
    [InlineData("8.0.204", "disable", "8.0.204")]
    [InlineData("8.0.205", "disable", null)]
    [InlineData("8.0.200", "disable", null)]
    // patch: the exact version when it is installed, even though a later patch is too…
    [InlineData("8.0.100", "patch", "8.0.100")]
    // …else the latest patch of the same band, never an older one, never another band.
    [InlineData("8.0.105", "patch", "8.0.110")]
    [InlineData("8.0.111", "patch", null)]
    [InlineData("8.0.200", "patch", "8.0.204")]
    [InlineData("8.0.205", "patch", null)]
    [InlineData("8.0.500", "patch", null)]
    // latestPatch: the latest patch of the band, even when the exact version is installed.
    [InlineData("8.0.100", "latestPatch", "8.0.110")]
    [InlineData("8.0.110", "latestPatch", "8.0.110")]
    [InlineData("8.0.111", "latestPatch", null)]
    [InlineData("9.0.200", "latestPatch", "9.0.203")]
    // feature: the latest patch of the asked-for band; if that band has nothing new enough, the next band up, same major.minor.
    [InlineData("8.0.100", "feature", "8.0.110")]
    [InlineData("8.0.111", "feature", "8.0.204")]
    [InlineData("8.0.250", "feature", "8.0.303")]
    [InlineData("8.0.405", "feature", null)]
    [InlineData("9.0.306", "feature", null)]
    // minor: as feature, then the next minor of the same major. No 8.1 here, so a too-new 8.0 has nowhere to go.
    [InlineData("8.0.405", "minor", null)]
    [InlineData("8.0.300", "minor", "8.0.303")]
    // major: as minor, then the next major: the lowest band that will do, and its latest patch.
    [InlineData("8.0.405", "major", "9.0.100")]
    [InlineData("9.0.101", "major", "9.0.203")]
    [InlineData("9.0.400", "major", "10.0.102")]
    [InlineData("10.0.103", "major", null)]
    [InlineData("7.0.100", "major", "8.0.110")]
    [InlineData("7.0.100", "minor", null)]
    // latestFeature: the highest band and patch of the same major.minor.
    [InlineData("8.0.100", "latestFeature", "8.0.404")]
    [InlineData("8.0.405", "latestFeature", null)]
    [InlineData("9.0.100", "latestFeature", "9.0.305")]
    // latestMinor: the highest SDK of the same major.
    [InlineData("8.0.100", "latestMinor", "8.0.404")]
    [InlineData("9.0.306", "latestMinor", null)]
    // latestMajor: the highest SDK there is, as long as it is not older than asked.
    [InlineData("8.0.100", "latestMajor", "10.0.102")]
    [InlineData("10.0.102", "latestMajor", "10.0.102")]
    [InlineData("10.0.103", "latestMajor", null)]
    [InlineData("11.0.100", "latestMajor", null)]
    public void Each_policy_picks_what_the_dotnet_host_would(string version, string policy, string? expected) =>
        Assert.Equal(expected, Resolve(version, policy));

    [Fact]
    public void Without_a_policy_a_version_means_latestPatch()
    {
        Assert.Equal("8.0.110", Resolve("8.0.100", null));
        Assert.Equal("8.0.204", Resolve("8.0.201", null));
        Assert.Null(Resolve("8.0.205", null));
        Assert.Null(Resolve("8.0.500", ""));
    }

    [Fact]
    public void Without_a_version_any_sdk_will_do_and_the_highest_is_used()
    {
        Assert.Equal("10.0.102", Resolve(null, null));
        Assert.Equal("10.0.102", Resolve(null, "latestMajor"));
        Assert.Null(Resolve(null, null, installed: []));
    }

    [Fact]
    public void The_policy_name_is_not_case_sensitive()
    {
        Assert.Equal("8.0.404", Resolve("8.0.100", "LATESTFEATURE"));
        Assert.Equal("8.0.404", Resolve("8.0.100", "latestfeature"));
    }

    [Fact]
    public void Minor_rolls_into_the_next_minor_of_the_same_major_and_no_further()
    {
        string[] installed = ["3.0.100", "3.1.201", "3.1.426", "5.0.408"];

        Assert.Equal("3.1.201", Resolve("3.0.101", "minor", installed: installed));
        Assert.Equal("3.1.426", Resolve("3.0.101", "latestMinor", installed: installed));
        Assert.Null(Resolve("3.1.500", "minor", installed: installed));
        Assert.Equal("5.0.408", Resolve("3.1.500", "major", installed: installed));
        Assert.Null(Resolve("3.0.101", "feature", installed: installed));
    }

    [Fact]
    public void A_preview_sdk_counts_unless_allowPrerelease_is_false()
    {
        string[] installed = ["9.0.305", "10.0.100-rc.2.25502.107"];

        // The command line's default is to allow previews.
        Assert.Equal("10.0.100-rc.2.25502.107", Resolve("9.0.100", "latestMajor", installed: installed));
        Assert.Equal("9.0.305", Resolve("9.0.100", "latestMajor", allowPrerelease: false, installed: installed));
        Assert.Null(Resolve("10.0.100-rc.1.25451.107", "latestPatch", allowPrerelease: null, installed: ["9.0.305"]));

        // A preview is older than its release: asking for the release is not met by the preview.
        Assert.Null(Resolve("10.0.100", "latestPatch", installed: installed));
        // A later preview of the same version satisfies an earlier one, and naming a preview allows previews whatever the file says.
        Assert.Equal("10.0.100-rc.2.25502.107", Resolve("10.0.100-rc.1.25451.107", "latestPatch", allowPrerelease: false, installed: installed));
        Assert.Null(Resolve("10.0.100-rc.2.25502.107", "disable", installed: ["10.0.100-rc.1.25451.107"]));
        Assert.Equal("10.0.100-rc.2.25502.107", Resolve("10.0.100-rc.2.25502.107", "disable", allowPrerelease: false, installed: installed));
    }

    [Fact]
    public void Within_a_band_the_release_beats_its_previews()
    {
        Assert.Equal("10.0.100", Resolve("10.0.100-preview.7.25380.108", "latestPatch", installed: ["10.0.100-rc.2.25502.107", "10.0.100", "10.0.100-preview.7.25380.108"]));
    }

    [Theory]
    [InlineData("8.0.100", "sideways")]
    [InlineData("8.0.100", "3")]
    [InlineData("8.0", "latestPatch")]
    [InlineData("eight", null)]
    [InlineData(null, "latestMinor")] // every policy but latestMajor needs a version
    [InlineData(null, "disable")]
    public void A_file_that_cannot_be_honoured_is_unknown_not_missing(string? version, string? policy)
    {
        var resolution = DotnetSdkResolver.Resolve(version, policy, null, Installed);

        Assert.Null(resolution.Sdk);
        Assert.NotNull(resolution.Invalid);
    }

    [Fact]
    public void Junk_in_the_installed_list_is_ignored()
    {
        Assert.Equal("8.0.110", Resolve("8.0.100", "latestPatch", installed: ["", "not-a-version", "8.0", "8.0.110", "8.0.110"]));
    }

    [Theory]
    [InlineData("10.0.100", "disable", "exactly 10.0.100")]
    [InlineData("10.0.100", null, "10.0.100 or a later 10.0.1xx patch")]
    [InlineData("10.0.203", "patch", "10.0.203 or a later 10.0.2xx patch")]
    [InlineData("10.0.100", "latestFeature", "10.0.100 or a later 10.0 SDK")]
    [InlineData("10.0.100", "minor", "10.0.100 or a later 10.x SDK")]
    [InlineData("10.0.100", "latestMajor", "10.0.100 or any later SDK")]
    public void The_requirement_is_described_in_words(string version, string? policy, string expected) =>
        Assert.Equal(expected, DotnetSdkResolver.Describe(version, policy));
}
