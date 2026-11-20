using DevDash.Contracts;
using DevDash.Core.Toolchains;

namespace DevDash.Core.Tests.Toolchains;

public class ToolVersionTests
{
    [Theory]
    [InlineData("v20.11.0", "20.11.0")]
    [InlineData("3.12", "3.12")]
    [InlineData("21", "21")]
    [InlineData("17.0.11+9", "17.0.11")]
    [InlineData("10.0.100-rc.2.25502.107", "10.0.100-rc.2.25502.107")]
    [InlineData(" 1.22.3 ", "1.22.3")]
    public void Reads_versions_leniently(string text, string expected) => Assert.Equal(expected, ToolVersion.Parse(text)!.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("lts/iron")]
    [InlineData("1..2")]
    [InlineData("1.2.x")]
    [InlineData("pypy3.9-7.3.9")]
    [InlineData("1.2.3-")]
    public void Refuses_what_is_not_a_version(string text) => Assert.Null(ToolVersion.Parse(text));

    [Fact]
    public void Orders_like_semantic_versioning()
    {
        string[] sorted = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.2", "2"];

        Assert.Equal(sorted.Reverse(), ToolVersion.ParseAll(sorted.Reverse().Concat(sorted)).Select(v => v.ToString()));
    }

    [Theory]
    [InlineData("3.12", "3.12.4", true)]
    [InlineData("3.12", "3.13.0", false)]
    [InlineData("20", "20.11.0", true)]
    [InlineData("3.12.4", "3.12.4", true)]
    [InlineData("3.12.4", "3.12.7", false)]
    [InlineData("3", "3.12.4", true)]
    public void A_partial_version_is_a_prefix(string prefix, string version, bool expected) =>
        Assert.Equal(expected, ToolVersion.Parse(prefix)!.IsPrefixOf(ToolVersion.Parse(version)!));
}

public class NodeRangeTests
{
    [Theory]
    [InlineData(">=18", "18.0.0", true)]
    [InlineData(">=18", "17.9.1", false)]
    [InlineData(">= 18.12", "18.11.0", false)]
    [InlineData(">= 18.12", "20.1.0", true)]
    [InlineData(">18", "18.20.0", false)]
    [InlineData(">18", "19.0.0", true)]
    [InlineData(">18.2.0", "18.2.1", true)]
    [InlineData("<21", "20.99.0", true)]
    [InlineData("<21", "21.0.0", false)]
    [InlineData("<=20", "20.5.0", true)]
    [InlineData("<=20", "21.0.0", false)]
    [InlineData("<=20.5.0", "20.5.1", false)]
    [InlineData(">=18 <21", "20.11.0", true)]
    [InlineData(">=18 <21", "22.0.0", false)]
    [InlineData("^20.1.0", "20.11.0", true)]
    [InlineData("^20.1.0", "20.0.9", false)]
    [InlineData("^20.1.0", "21.0.0", false)]
    [InlineData("^20", "20.0.0", true)]
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("~20.11", "20.11.9", true)]
    [InlineData("~20.11", "20.12.0", false)]
    [InlineData("~20.11.1", "20.11.0", false)]
    [InlineData("~20", "20.99.0", true)]
    [InlineData("18.x || 20.x", "20.3.0", true)]
    [InlineData("18.x || 20.x", "19.3.0", false)]
    [InlineData("^18.17.0 || >=20.5.0", "18.20.0", true)]
    [InlineData("^18.17.0 || >=20.5.0", "20.4.0", false)]
    [InlineData("18 - 20", "20.9.0", true)]
    [InlineData("18 - 20", "21.0.0", false)]
    [InlineData("18.0.0 - 20.0.0", "20.0.1", false)]
    [InlineData("20", "20.11.0", true)]
    [InlineData("20.11", "20.12.0", false)]
    [InlineData("=20.11.0", "20.11.0", true)]
    [InlineData("v20.11.0", "20.11.0", true)]
    [InlineData("*", "4.0.0", true)]
    [InlineData("", "4.0.0", true)]
    [InlineData("x", "4.0.0", true)]
    public void Reads_the_npm_range_syntax(string range, string version, bool expected) =>
        Assert.Equal(expected, NodeRange.Satisfies(range, ToolVersion.Parse(version)!));

    [Theory]
    [InlineData("latest")]
    [InlineData(">=18.0.0-rc.1")]
    [InlineData(">=")]
    [InlineData("18.x.1")]
    [InlineData("1.2.3.4")]
    [InlineData("=> 18")]
    public void A_range_it_cannot_read_is_unknown(string range) => Assert.Null(NodeRange.Satisfies(range, ToolVersion.Parse("20.0.0")!));
}

public class PinFilesTests
{
    [Fact]
    public void Reads_global_json_with_comments_and_every_sdk_field()
    {
        var pin = Assert.Single(PinFiles.Parse("global.json", """
            {
              // the SDK this repo builds with
              "sdk": { "version": "10.0.100", "rollForward": "latestFeature", "allowPrerelease": false, },
              "msbuild-sdks": { "Some.Sdk": "1.0.0" }
            }
            """));

        Assert.Equal((ToolNames.Dotnet, "10.0.100", "global.json", "latestFeature", (bool?)false), (pin.Tool, pin.Version, pin.Source, pin.RollForward, pin.AllowPrerelease));
    }

    [Theory]
    [InlineData("""{ "sdk": { "version": "8.0.100" } }""", "8.0.100", null)]
    [InlineData("""{ "sdk": { "version": " 8.0.100 ", "rollForward": "disable" } }""", "8.0.100", "disable")]
    public void A_missing_policy_stays_missing(string json, string version, string? policy)
    {
        var pin = Assert.Single(PinFiles.Parse("global.json", json));

        Assert.Equal((version, policy, (bool?)null), (pin.Version, pin.RollForward, pin.AllowPrerelease));
    }

    [Theory]
    [InlineData("""{ "msbuild-sdks": { "X": "1.0.0" } }""")]
    [InlineData("""{ "sdk": { "rollForward": "latestMajor" } }""")] // no version: any SDK will do
    [InlineData("""{ "sdk": "8.0.100" }""")]
    [InlineData("""{ "sdk": { "version": 8 } }""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void A_global_json_that_pins_nothing_gives_no_pin(string json) => Assert.Empty(PinFiles.Parse("global.json", json));

    [Theory]
    [InlineData(".nvmrc", "v20.11.0\n", "20.11.0", false)]
    [InlineData(".nvmrc", "# what CI uses\n\nlts/iron  # yes\n", "lts/iron", true)]
    [InlineData(".node-version", "﻿22\r\n", "22", true)]
    public void Reads_the_first_line_of_a_node_version_file(string file, string content, string expected, bool exact)
    {
        var pin = Assert.Single(PinFiles.Parse(file, content));

        Assert.Equal(ToolNames.Node, pin.Tool);
        Assert.Equal(file, pin.Source);
        Assert.Equal(exact ? expected : "v" + expected, pin.Version);
    }

    [Fact]
    public void Reads_engines_and_volta_from_package_json()
    {
        var pins = PinFiles.Parse("package.json", """{ "name": "x", "engines": { "node": ">=18 <21", "npm": ">=9" }, "volta": { "node": "20.11.0", "yarn": "1.22.19" } }""");

        Assert.Equal([("package.json engines.node", ">=18 <21"), ("package.json volta.node", "20.11.0")], pins.Select(p => (p.Source, p.Version)));
        Assert.All(pins, p => Assert.Equal(ToolNames.Node, p.Tool));
        Assert.Empty(PinFiles.Parse("package.json", """{ "name": "x", "engines": "node >= 4" }"""));
        Assert.Empty(PinFiles.Parse("package.json", "{ broken"));
    }

    [Fact]
    public void Reads_python_version_and_tool_versions()
    {
        Assert.Equal("3.12.4", Assert.Single(PinFiles.Parse(".python-version", "3.12.4\n3.11.9\n")).Version);

        var pins = PinFiles.Parse(".tool-versions", "nodejs 20.11.0\npython 3.12.1 3.11.8 # fallbacks\nruby 3.3.0\ndotnet-core 8.0.100\n# comment\ngolang\nnodejs 18.0.0\n");

        Assert.Equal([(ToolNames.Node, "20.11.0"), (ToolNames.Python, "3.12.1 3.11.8"), (ToolNames.Dotnet, "8.0.100")], pins.Select(p => (p.Tool, p.Version)));
        Assert.All(pins, p => Assert.Equal(".tool-versions", p.Source));
    }

    [Fact]
    public void An_empty_or_absurd_file_pins_nothing()
    {
        Assert.Empty(PinFiles.Parse(".nvmrc", "\n\n# nothing\n"));
        Assert.Empty(PinFiles.Parse(".nvmrc", new string('9', 500)));
        Assert.Empty(PinFiles.Parse("README.md", "20"));
    }
}

public class ToolchainCheckTests
{
    private static ToolchainInventory Inventory(params (string Tool, string[] Versions)[] tools) => new()
    {
        Tools = tools.Select(t => new InstalledToolchain { Tool = t.Tool, Versions = t.Versions, Active = t.Versions.FirstOrDefault() }).ToList(),
    };

    private static readonly ToolchainInventory Machine = Inventory(
        (ToolNames.Dotnet, ["10.0.102", "8.0.404"]),
        (ToolNames.Node, ["22.3.0", "20.11.0", "18.19.1"]),
        (ToolNames.Python, ["3.12.4", "3.11.9"]));

    private static PinCheck Check(string tool, string version, string source, string? rollForward = null, ToolchainInventory? on = null) =>
        ToolchainCheck.Check(new ToolchainPin { Tool = tool, Version = version, Source = source, RollForward = rollForward }, on ?? Machine);

    [Fact]
    public void A_global_json_is_judged_with_its_roll_forward()
    {
        var met = Check(ToolNames.Dotnet, "10.0.100", "global.json");
        Assert.Equal((PinStatus.Satisfied, "10.0.102", "uses 10.0.102"), (met.Status, met.Resolved, met.Detail));

        var missing = Check(ToolNames.Dotnet, "9.0.100", "global.json", "latestFeature");
        Assert.Equal(PinStatus.Missing, missing.Status);
        Assert.Equal("needs 9.0.100 or a later 9.0 SDK; this machine has 10.0.102, 8.0.404", missing.Detail);

        Assert.Equal(PinStatus.Satisfied, Check(ToolNames.Dotnet, "9.0.100", "global.json", "latestMajor").Status);
        Assert.Equal(PinStatus.Unknown, Check(ToolNames.Dotnet, "9.0.100", "global.json", "whenever").Status);

        var none = Check(ToolNames.Dotnet, "8.0.100", "global.json", on: Inventory((ToolNames.Node, ["20.0.0"])));
        Assert.Equal((PinStatus.Missing, "needs 8.0.100 or a later 8.0.1xx patch; no .NET SDK was found on this machine"), (none.Status, none.Detail));
    }

    [Theory]
    [InlineData(".nvmrc", "20", PinStatus.Satisfied, "20.11.0")]
    [InlineData(".nvmrc", "v20.11.0", PinStatus.Satisfied, "20.11.0")]
    [InlineData(".nvmrc", "20.12", PinStatus.Missing, null)]
    [InlineData(".nvmrc", "16", PinStatus.Missing, null)]
    [InlineData(".nvmrc", "lts/iron", PinStatus.Satisfied, "20.11.0")]
    [InlineData(".nvmrc", "lts/gallium", PinStatus.Missing, null)]
    [InlineData(".nvmrc", "lts/*", PinStatus.Satisfied, "22.3.0")]
    [InlineData(".nvmrc", "lts/unheard-of", PinStatus.Unknown, null)]
    [InlineData(".nvmrc", "node", PinStatus.Satisfied, "22.3.0")]
    [InlineData(".nvmrc", "system", PinStatus.Satisfied, "22.3.0")]
    [InlineData(".node-version", "22.3.0", PinStatus.Satisfied, "22.3.0")]
    [InlineData("package.json engines.node", ">=18 <21", PinStatus.Satisfied, "20.11.0")]
    [InlineData("package.json engines.node", "^16 || ^14", PinStatus.Missing, null)]
    [InlineData("package.json engines.node", ">=24", PinStatus.Missing, null)]
    [InlineData("package.json engines.node", "whatever works", PinStatus.Unknown, null)]
    [InlineData("package.json volta.node", "18.19.1", PinStatus.Satisfied, "18.19.1")]
    [InlineData(".tool-versions", "16.0.0 20.11.0", PinStatus.Satisfied, "20.11.0")]
    [InlineData(".tool-versions", "16.0.0 14.0.0", PinStatus.Missing, null)]
    public void Node_pins(string source, string version, PinStatus status, string? resolved)
    {
        var check = Check(ToolNames.Node, version, source);

        Assert.Equal((status, resolved), (check.Status, check.Resolved));
    }

    [Theory]
    [InlineData("3.12", PinStatus.Satisfied, "3.12.4")]
    [InlineData("3.12.4", PinStatus.Satisfied, "3.12.4")]
    [InlineData("3.12.7", PinStatus.Missing, null)] // pyenv would say "not installed"
    [InlineData("3.13", PinStatus.Missing, null)]
    [InlineData("3.11.9/envs/api", PinStatus.Satisfied, "3.11.9")]
    [InlineData("my-virtualenv", PinStatus.Unknown, null)]
    [InlineData("pypy3.9-7.3.9", PinStatus.Unknown, null)]
    public void Python_pins(string version, PinStatus status, string? resolved)
    {
        var check = Check(ToolNames.Python, version, ".python-version");

        Assert.Equal((status, resolved), (check.Status, check.Resolved));
    }

    [Fact]
    public void A_tool_the_machine_does_not_have_is_missing_and_says_so()
    {
        var check = Check(ToolNames.Go, "1.22.3", ".tool-versions");

        Assert.Equal((PinStatus.Missing, "needs 1.22.3; no Go was found on this machine"), (check.Status, check.Detail));
    }

    [Fact]
    public void Nothing_is_said_about_a_machine_that_sent_no_inventory()
    {
        var entry = new DevEntry { Root = "/dev", Name = "x", Path = "/dev/x", Pins = [new ToolchainPin { Tool = ToolNames.Node, Version = "99", Source = ".nvmrc" }] };

        Assert.Empty(ToolchainCheck.For(entry, new MachineInfo { MachineId = "old", Hostname = "old" }));
        Assert.Equal(PinStatus.Missing, Assert.Single(ToolchainCheck.For(entry, new MachineInfo { MachineId = "new", Hostname = "new", Toolchains = Machine })).Status);
        Assert.Empty(ToolchainCheck.For(entry with { Pins = null }, new MachineInfo { MachineId = "new", Hostname = "new", Toolchains = Machine }));
    }
}
