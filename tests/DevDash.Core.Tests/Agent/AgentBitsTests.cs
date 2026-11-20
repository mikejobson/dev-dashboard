using DevDash.Agent;
using DevDash.Agent.Scanning;

namespace DevDash.Core.Tests.Agent;

public class AgentBitsTests
{
    [Fact]
    public void Auto_fetch_is_on_every_four_hours_by_default()
    {
        var options = new AgentOptions();

        Assert.True(options.AutoFetch.Enabled);
        Assert.Equal(4, options.AutoFetch.IntervalHours);
        Assert.Equal(15, options.ScanIntervalMinutes);
        // On a machine without T3 Code (a home with no ~/.t3/projects) the only default root is ~/Developer.
        var home = Directory.CreateTempSubdirectory("devdash-home-").FullName;
        try
        {
            Assert.EndsWith("Developer", Assert.Single(options.ResolveRoots(home)));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Cli_flags_become_configuration_overrides()
    {
        var cli = AgentCli.Parse(["scan", "--dry-run", "--no-fetch", "--root", "/a", "--root", "/b", "--hub-url", "http://hub:1", "--sizes"]);

        Assert.Null(cli.Error);
        Assert.Equal("scan", cli.Command);
        Assert.True(cli.DryRun);
        Assert.True(cli.Sizes);
        Assert.Equal("false", cli.Overrides["Agent:AutoFetch:Enabled"]);
        Assert.Equal("/a", cli.Overrides["Agent:Roots:0"]);
        Assert.Equal("/b", cli.Overrides["Agent:Roots:1"]);
        Assert.Equal("http://hub:1", cli.Overrides["Agent:HubUrl"]);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("scan", "--what")]
    [InlineData("scan", "--root")]
    public void Cli_rejects_unknown_input(params string[] args) => Assert.NotNull(AgentCli.Parse(args).Error);

    [Fact]
    public void Cli_defaults_to_run() => Assert.Equal("run", AgentCli.Parse([]).Command);

    [Theory]
    [InlineData("# Title\n\nFirst paragraph\ncontinues here.\n\nSecond.", "First paragraph continues here.")]
    [InlineData("<img src=\"a.svg\">\n\n# Dev Dashboard\n\nCreated in [T3 Code](https://t3.codes).", "Created in T3 Code.")]
    [InlineData("[![build](a)](b)\n\n```\ncode\n```\n\nReal **text** with `code`.", "Real text with code.")]
    [InlineData("# Only a heading\n", null)]
    [InlineData("# App\n\n- App.sln — solution\n- src/ — code\n\nA tool for rooms.\n", "A tool for rooms.")]
    [InlineData("Key folders:\n- src/\n- docs/\n\nThe real summary.", "The real summary.")]
    [InlineData("Does three things.\n\n1. One\n2. Two", "Does three things.")]
    [InlineData("- only\n- bullets\n", null)]
    [InlineData("", null)]
    public void First_paragraph_skips_headings_badges_and_code(string readme, string? expected) =>
        Assert.Equal(expected, DescriptionReader.FirstParagraph(readme));

    [Theory]
    [InlineData("node_modules", "", true)]
    [InlineData(".venv", "", true)]
    [InlineData(".next", "", true)]
    [InlineData("bin", "App.csproj", true)]
    [InlineData("obj", "App.csproj", true)]
    [InlineData("bin", "run.sh", false)]
    [InlineData("target", "Cargo.toml", true)]
    [InlineData("target", "notes.md", false)]
    [InlineData("dist", "package.json", true)]
    [InlineData("dist", "", false)]
    [InlineData("src", "package.json", false)]
    [InlineData(".angular", "", true)]
    [InlineData(".dart_tool", "", true)]
    [InlineData("packages", "App.sln", false)] // NuGet's folder is not counted: the name is too ordinary to delete
    [InlineData("coverage", "package.json", false)]
    public void Build_folders_only_count_next_to_the_manifest_that_makes_them(string name, string sibling, bool expected) =>
        Assert.Equal(expected, TreeWalker.IsReclaimable(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sibling }));
}
