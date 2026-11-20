using DevDash.Agent;
using DevDash.Agent.Dependencies;
using DevDash.Agent.Scanning;
using DevDash.Agent.Toolchains;
using DevDash.Contracts;
using DevDash.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDash.Core.Tests.Agent;

/// <summary>
/// Stands in for dotnet, node, npm and the rest: no test here needs any of them installed, and none
/// ever starts a real one. It records every command it is asked to run.
/// </summary>
internal sealed class FakeTools : IToolRunner
{
    private readonly List<(Func<ToolCommand, bool> When, Func<ToolCommand, ToolResult> Then)> _rules = [];

    public List<ToolCommand> Calls { get; } = [];

    /// <summary>The commands as text: the program's file name and its arguments.</summary>
    public IEnumerable<string> Lines => Calls.Select(c => $"{Path.GetFileNameWithoutExtension(c.FileName)} {string.Join(' ', c.Arguments)}");

    public FakeTools On(string tool, string args, string stdout, int exit = 0, string stderr = "") =>
        On(c => Path.GetFileNameWithoutExtension(c.FileName) == tool && string.Join(' ', c.Arguments).StartsWith(args, StringComparison.Ordinal), _ => new ToolResult(exit, stdout, stderr, false));

    public FakeTools On(Func<ToolCommand, bool> when, Func<ToolCommand, ToolResult> then)
    {
        _rules.Add((when, then));
        return this;
    }

    public Task<ToolResult> RunAsync(ToolCommand command, TimeSpan timeout, CancellationToken ct)
    {
        lock (Calls)
        {
            Calls.Add(command);
        }

        foreach (var (when, then) in _rules)
        {
            if (when(command))
            {
                return Task.FromResult(then(command));
            }
        }

        return Task.FromResult(new ToolResult(127, "", "unexpected command", false));
    }
}

/// <summary>A machine made of temporary folders: a home, a PATH, and "system" folders under a root.</summary>
internal sealed class FakeMachine : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("devdash-tools-").FullName;

    public string Home => Path.Combine(Root, "home");

    public FakeMachine() => Directory.CreateDirectory(Home);

    /// <summary>Creates an (empty) executable file and returns its path.</summary>
    public string Tool(params string[] parts)
    {
        var file = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
        return file;
    }

    public string Dir(params string[] parts) => Directory.CreateDirectory(Path.Combine([Root, .. parts])).FullName;

    public ToolEnvironment Environment(bool windows = false, bool mac = false, string[]? path = null, Dictionary<string, string>? variables = null) => new()
    {
        Home = Home,
        IsWindows = windows,
        IsMacOS = mac,
        SystemRoot = Root,
        PathEntries = path ?? [],
        Variables = variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}

public sealed class ToolchainCollectorTests : IDisposable
{
    private readonly FakeMachine _machine = new();

    public void Dispose() => _machine.Dispose();

    [Fact]
    public async Task Finds_tools_off_the_path_and_reads_what_each_reports()
    {
        // A service's PATH: nothing useful on it. Everything is where installers put it.
        var dotnet = _machine.Tool("usr", "local", "share", "dotnet", "dotnet");
        var node = _machine.Tool("opt", "homebrew", "bin", "node");
        var python = _machine.Tool("opt", "homebrew", "bin", "python3");
        var go = _machine.Tool("usr", "local", "go", "bin", "go");
        var rustc = _machine.Tool("home", ".cargo", "bin", "rustc");
        var java = _machine.Tool("opt", "homebrew", "opt", "openjdk", "bin", "java");
        var tools = new FakeTools()
            .On("dotnet", "--list-sdks", "8.0.404 [/usr/local/share/dotnet/sdk]\n10.0.100 [/usr/local/share/dotnet/sdk]\n10.0.200-preview.1.25101.5 [/usr/local/share/dotnet/sdk]\n")
            .On("node", "--version", "v20.11.0\n")
            .On("python3", "--version", "Python 3.12.4\n")
            .On("go", "version", "go version go1.22.3 darwin/arm64\n")
            .On("rustc", "--version", "rustc 1.79.0 (129f3b996 2024-06-10)\n")
            .On("java", "-version", "", stderr: "openjdk version \"21.0.3\" 2024-04-16\nOpenJDK Runtime Environment\n");
        var now = new TestClock(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));

        var inventory = await new ToolchainCollector(tools, new ToolLocator(_machine.Environment(mac: true, path: ["/usr/bin-that-is-not-there", "relative/bin", ""])), now)
            .CollectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(now.GetUtcNow(), inventory.CollectedAt);
        Assert.Equal([ToolNames.Dotnet, ToolNames.Node, ToolNames.Python, ToolNames.Go, ToolNames.Rust, ToolNames.Java], inventory.Tools.Select(t => t.Tool));
        Assert.Equal(["10.0.200-preview.1.25101.5", "10.0.100", "8.0.404"], inventory.Find(ToolNames.Dotnet)!.Versions);
        Assert.Null(inventory.Find(ToolNames.Dotnet)!.Active);
        Assert.Equal(("20.11.0", "20.11.0"), (inventory.Find(ToolNames.Node)!.Active, inventory.Find(ToolNames.Node)!.Versions.Single()));
        Assert.Equal("3.12.4", inventory.Find(ToolNames.Python)!.Active);
        Assert.Equal("1.22.3", inventory.Find(ToolNames.Go)!.Active);
        Assert.Equal("1.79.0", inventory.Find(ToolNames.Rust)!.Active);
        Assert.Equal("21.0.3", inventory.Find(ToolNames.Java)!.Active);

        // Started directly, by full path, with fixed arguments. No shell anywhere.
        Assert.Equal([dotnet, node, python, go, rustc, java], tools.Calls.Select(c => c.FileName));
        Assert.All(tools.Calls, c => Assert.True(Path.IsPathRooted(c.FileName)));
        Assert.DoesNotContain(tools.Calls, c => Path.GetFileName(c.FileName) is "sh" or "bash" or "zsh" or "cmd.exe" or "cmd" or "powershell.exe" or "pwsh");
    }

    [Fact]
    public async Task Lists_what_the_version_managers_hold_without_starting_anything()
    {
        _machine.Dir("home", ".nvm", "versions", "node", "v18.19.1");
        _machine.Dir("home", ".nvm", "versions", "node", "v20.11.0");
        _machine.Dir("home", ".local", "share", "fnm", "node-versions", "v22.3.0", "installation", "bin");
        _machine.Dir("home", ".volta", "tools", "image", "node", "16.20.2");
        _machine.Dir("home", ".pyenv", "versions", "3.11.9");
        _machine.Dir("home", ".pyenv", "versions", "3.12.4");
        _machine.Dir("home", ".pyenv", "versions", "my-virtualenv");
        _machine.Dir("home", ".asdf", "installs", "golang", "1.21.0");
        _machine.Dir("home", ".local", "share", "mise", "installs", "python", "3.13.0");
        var tools = new FakeTools();

        var inventory = await new ToolchainCollector(tools, new ToolLocator(_machine.Environment())).CollectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        var node = inventory.Find(ToolNames.Node)!;
        Assert.Equal(["22.3.0", "20.11.0", "18.19.1", "16.20.2"], node.Versions);
        Assert.Equal(["nvm", "fnm", "volta"], node.Managers);
        Assert.Null(node.Active);
        Assert.Equal(["3.13.0", "3.12.4", "3.11.9"], inventory.Find(ToolNames.Python)!.Versions);
        Assert.Equal(["pyenv", "mise"], inventory.Find(ToolNames.Python)!.Managers);
        Assert.Equal(["1.21.0"], inventory.Find(ToolNames.Go)!.Versions);
        Assert.Null(inventory.Find(ToolNames.Dotnet));

        // The newest managed node is found and asked its version (and here says nothing useful); that is the only thing started.
        Assert.All(tools.Calls, c => Assert.Equal("node", Path.GetFileName(c.FileName)));
    }

    [Fact]
    public async Task The_version_managers_can_be_moved_with_their_environment_variables()
    {
        _machine.Dir("elsewhere", "nvm", "versions", "node", "v21.1.0");
        _machine.Dir("elsewhere", "pyenv", "versions", "3.10.14");
        var env = _machine.Environment(variables: new(StringComparer.OrdinalIgnoreCase)
        {
            ["NVM_DIR"] = Path.Combine(_machine.Root, "elsewhere", "nvm"),
            ["PYENV_ROOT"] = Path.Combine(_machine.Root, "elsewhere", "pyenv"),
        });

        var inventory = await new ToolchainCollector(new FakeTools(), new ToolLocator(env)).CollectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(["21.1.0"], inventory.Find(ToolNames.Node)!.Versions);
        Assert.Equal(["3.10.14"], inventory.Find(ToolNames.Python)!.Versions);
    }

    [Fact]
    public async Task On_windows_it_looks_in_program_files_for_exe_files()
    {
        var programFiles = _machine.Dir("Program Files");
        var dotnet = _machine.Tool("Program Files", "dotnet", "dotnet.exe");
        var node = _machine.Tool("Program Files", "nodejs", "node.exe");
        _machine.Tool("Program Files", "nodejs", "node"); // not an executable on Windows
        _machine.Dir("home", "AppData", "Roaming", "nvm", "v18.20.0");
        var tools = new FakeTools().On("dotnet", "--list-sdks", "9.0.305 [C:\\Program Files\\dotnet\\sdk]\r\n").On("node", "--version", "v20.11.0\r\n");
        var env = _machine.Environment(windows: true, variables: new(StringComparer.OrdinalIgnoreCase) { ["ProgramFiles"] = programFiles });

        var inventory = await new ToolchainCollector(tools, new ToolLocator(env)).CollectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(["9.0.305"], inventory.Find(ToolNames.Dotnet)!.Versions);
        Assert.Equal(["20.11.0", "18.20.0"], inventory.Find(ToolNames.Node)!.Versions);
        Assert.Equal(["nvm"], inventory.Find(ToolNames.Node)!.Managers);
        Assert.Equal([dotnet, node], tools.Calls.Select(c => c.FileName));
    }

    [Fact]
    public async Task The_macos_stand_ins_that_would_open_an_install_dialog_are_never_run()
    {
        _machine.Tool("usr", "bin", "python3");
        _machine.Tool("usr", "bin", "java");
        var tools = new FakeTools().On("python3", "--version", "Python 3.9.6\n").On("java", "-version", "", stderr: "openjdk version \"17.0.11\"\n");
        var locator = new ToolLocator(_machine.Environment(mac: true, path: [Path.Combine(_machine.Root, "usr", "bin")]));

        var bare = await new ToolchainCollector(tools, locator).CollectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Empty(bare.Tools);
        Assert.Empty(tools.Calls);

        // With the command line tools and a JDK really there, the same files are the real thing.
        _machine.Dir("Library", "Developer", "CommandLineTools", "usr", "bin");
        _machine.Dir("Library", "Java", "JavaVirtualMachines", "temurin-17.jdk");
        var real = await new ToolchainCollector(tools, locator).CollectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(("3.9.6", "17.0.11"), (real.Find(ToolNames.Python)!.Active, real.Find(ToolNames.Java)!.Active));
    }

    [Fact]
    public async Task A_tool_that_fails_hangs_or_talks_nonsense_is_left_out()
    {
        _machine.Tool("opt", "homebrew", "bin", "node");
        _machine.Tool("opt", "homebrew", "bin", "python3");
        _machine.Tool("opt", "homebrew", "bin", "go");
        _machine.Tool("usr", "local", "share", "dotnet", "dotnet");
        var tools = new FakeTools()
            .On("node", "--version", "", exit: 1, stderr: "dyld: Library not loaded")
            .On(c => Path.GetFileName(c.FileName) == "python3", _ => new ToolResult(-1, "", "timed out after 5s", true))
            .On(c => Path.GetFileName(c.FileName) == "go", _ => ToolResult.NotStarted("Permission denied"))
            .On("dotnet", "--list-sdks", "Welcome to .NET!\n\nnot a version line\n");

        var inventory = await new ToolchainCollector(tools, new ToolLocator(_machine.Environment())).CollectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Empty(inventory.Tools);
    }

    [Fact]
    public async Task The_inventory_is_collected_once_and_reused_until_it_is_due()
    {
        _machine.Tool("opt", "homebrew", "bin", "node");
        var tools = new FakeTools().On("node", "--version", "v20.11.0\n");
        var options = new AgentOptions { Toolchains = new ToolchainOptions { IntervalHours = 6 } };
        var clock = new TestClock(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        var service = new ToolchainService(new ToolchainCollector(tools, new ToolLocator(_machine.Environment()), clock), new TestOptions(options), NullLogger<ToolchainService>.Instance, clock);

        var first = await service.GetAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromHours(5);
        var second = await service.GetAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Single(tools.Calls);

        clock.Now += TimeSpan.FromHours(2);
        var third = await service.GetAsync(CancellationToken.None);

        Assert.NotSame(first, third);
        Assert.Equal(2, tools.Calls.Count);

        options.Toolchains.Enabled = false;
        Assert.Null(await service.GetAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("v20.11.0", "20.11.0")]
    [InlineData("Python 3.12.4", "3.12.4")]
    [InlineData("go version go1.22.3 linux/amd64", "1.22.3")]
    [InlineData("rustc 1.81.0-nightly (abc 2024-07-01)", "1.81.0-nightly")]
    [InlineData("java version \"1.8.0_402\"", "1.8.0")]
    [InlineData("Python was not found; run without arguments to install from the Microsoft Store", null)]
    [InlineData("", null)]
    public void Reads_the_version_out_of_what_a_tool_prints(string output, string? expected) =>
        Assert.Equal(expected, ToolchainCollector.ParseVersion(output));

    [Fact]
    public void Reads_the_pins_at_the_top_of_a_working_tree_and_nowhere_else()
    {
        var repo = _machine.Dir("repo");
        File.WriteAllText(Path.Combine(repo, "global.json"), """{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }""");
        File.WriteAllText(Path.Combine(repo, ".nvmrc"), "20\n");
        File.WriteAllText(Path.Combine(repo, "package.json"), """{ "name": "x", "engines": { "node": ">=18" } }""");
        File.WriteAllText(Path.Combine(repo, ".python-version"), "3.12\n");
        File.WriteAllText(Path.Combine(repo, ".tool-versions"), "golang 1.22.3\n");
        Directory.CreateDirectory(Path.Combine(repo, "sub"));
        File.WriteAllText(Path.Combine(repo, "sub", ".node-version"), "8\n");

        var pins = PinReader.Read(repo)!;

        Assert.Equal(
            [("dotnet", "10.0.100", "global.json"), ("node", "20", ".nvmrc"), ("node", ">=18", "package.json engines.node"), ("python", "3.12", ".python-version"), ("go", "1.22.3", ".tool-versions")],
            pins.Select(p => (p.Tool, p.Version, p.Source)));
        Assert.Equal("latestFeature", pins[0].RollForward);
        Assert.Null(PinReader.Read(Path.Combine(repo, "sub", "nothing-here")));
        Assert.Null(PinReader.Read(_machine.Dir("empty")));
    }

    [Fact]
    public void An_oversized_pin_file_is_not_read()
    {
        var repo = _machine.Dir("big");
        File.WriteAllText(Path.Combine(repo, ".nvmrc"), "20\n" + new string('#', 300_000));

        Assert.Null(PinReader.Read(repo));
    }
}

public sealed class DependencyCheckTests : IDisposable
{
    private readonly FakeMachine _machine = new();
    private readonly string _repo;
    private readonly string _dotnet;
    private readonly string _npm;
    private readonly ToolLocator _locator;

    private const string HelpWithNoRestore = "Options:\n  --vulnerable\n  --outdated\n  --format <console|json>\n  --no-restore   Do not restore before running the command.\n";
    private const string HelpWithoutNoRestore = "Options:\n  --vulnerable\n  --outdated\n  --format <console|json>\n";

    private const string VulnerableJson = """
        {
          "version": 1,
          "parameters": "--vulnerable --include-transitive",
          "sources": [ "https://packages.example.test/v3/index.json" ],
          "projects": [
            { "path": "/repo/src/App/App.csproj",
              "frameworks": [
                { "framework": "net8.0",
                  "topLevelPackages": [
                    { "id": "Some.Json", "requestedVersion": "12.0.1", "resolvedVersion": "12.0.1",
                      "vulnerabilities": [ { "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr" } ] } ],
                  "transitivePackages": [
                    { "id": "Deep.Crypto", "resolvedVersion": "4.3.0",
                      "vulnerabilities": [ { "severity": "Moderate", "advisoryurl": "https://github.com/advisories/GHSA-aaaa-1111-bbbb" },
                                           { "severity": "Critical", "advisoryurl": "https://github.com/advisories/GHSA-cccc-2222-dddd" } ] } ] },
                { "framework": "net10.0",
                  "topLevelPackages": [
                    { "id": "Some.Json", "requestedVersion": "12.0.1", "resolvedVersion": "12.0.1",
                      "vulnerabilities": [ { "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr" } ] } ] } ] },
            { "path": "/repo/src/Lib/Lib.csproj" },
            { "path": "/repo/tests/App.Tests/App.Tests.csproj",
              "frameworks": [ { "framework": "net8.0", "transitivePackages": [
                    { "id": "Old.Text", "resolvedVersion": "1.0.0", "vulnerabilities": [ { "severity": "Low", "advisoryurl": "https://example.test/not-an-advisory" } ] } ] } ] }
          ]
        }
        """;

    private const string OutdatedJson = """
        { "version": 1, "parameters": "--outdated",
          "projects": [
            { "path": "/repo/src/App/App.csproj", "frameworks": [ { "framework": "net8.0", "topLevelPackages": [
                { "id": "Some.Json", "requestedVersion": "12.0.1", "resolvedVersion": "12.0.1", "latestVersion": "13.0.3" },
                { "id": "Fresh.Lib", "requestedVersion": "2.0.0", "resolvedVersion": "2.0.0", "latestVersion": "2.0.0" } ] } ] },
            { "path": "/repo/tests/App.Tests/App.Tests.csproj", "frameworks": [ { "framework": "net8.0", "topLevelPackages": [
                { "id": "Some.Json", "requestedVersion": "12.0.1", "resolvedVersion": "12.0.1", "latestVersion": "13.0.3" },
                { "id": "Test.Runner", "requestedVersion": "2.9.0", "resolvedVersion": "2.9.0", "latestVersion": "3.1.0" } ] } ] } ] }
        """;

    private const string NotRestoredJson = """
        { "version": 1, "parameters": "--vulnerable --include-transitive",
          "problems": [ { "project": "/repo/src/App/App.csproj", "level": "error", "text": "No assets file was found for `/repo/src/App/App.csproj`. Please run restore before running this command." } ],
          "projects": [ { "path": "/repo/src/App/App.csproj" } ] }
        """;

    private const string OfflineJson = """
        { "version": 1, "parameters": "--vulnerable",
          "problems": [ { "level": "error", "text": "Unable to load the service index for source https://packages.example.test/v3/index.json." } ],
          "projects": [ { "path": "/repo/src/App/App.csproj" } ] }
        """;

    private const string AuditJson = """
        {
          "auditReportVersion": 2,
          "vulnerabilities": {
            "left-pad": { "name": "left-pad", "severity": "critical", "isDirect": true,
              "via": [ { "source": 1, "name": "left-pad", "title": "Something bad", "url": "https://github.com/advisories/GHSA-pppp-qqqq-rrrr", "severity": "critical" } ] },
            "wrapper": { "name": "wrapper", "severity": "critical", "isDirect": false, "via": [ "left-pad" ] },
            "tiny-thing": { "name": "tiny-thing", "severity": "low", "via": [ { "url": "https://github.com/advisories/GHSA-ssss-tttt-uuuu", "severity": "low" } ] }
          },
          "metadata": { "vulnerabilities": { "info": 0, "low": 1, "moderate": 0, "high": 0, "critical": 2, "total": 3 } }
        }
        """;

    private const string NpmOutdatedJson = """
        { "left-pad": { "current": "1.0.0", "wanted": "1.0.2", "latest": "1.3.0", "dependent": "repo", "location": "node_modules/left-pad" },
          "same": { "current": "2.0.0", "wanted": "2.0.0", "latest": "2.0.0" },
          "shared": [ { "current": "1.0.0", "wanted": "1.0.0", "latest": "4.0.0" }, { "current": "2.0.0", "wanted": "2.0.0", "latest": "4.0.0" } ] }
        """;

    public DependencyCheckTests()
    {
        _repo = _machine.Dir("dev", "widget");
        _dotnet = _machine.Tool("usr", "local", "share", "dotnet", "dotnet");
        _npm = _machine.Tool("opt", "homebrew", "bin", "npm");
        _machine.Tool("opt", "homebrew", "bin", "node");
        _locator = new ToolLocator(_machine.Environment());
    }

    public void Dispose() => _machine.Dispose();

    private void Dotnet() => File.WriteAllText(Path.Combine(_repo, "Widget.sln"), "");

    private void Npm(bool lockfile = true, bool modules = true)
    {
        File.WriteAllText(Path.Combine(_repo, "package.json"), "{}");
        if (lockfile)
        {
            File.WriteAllText(Path.Combine(_repo, "package-lock.json"), "{}");
        }

        if (modules)
        {
            Directory.CreateDirectory(Path.Combine(_repo, "node_modules"));
        }
    }

    private Task<DependencyCacheEntry> CheckAsync(FakeTools tools) =>
        new DependencyChecker(tools, _locator, new TestClock(new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero))).CheckAsync(_repo, TimeSpan.FromSeconds(30), CancellationToken.None);

    /// <summary>The rule above all: nothing that restores, installs, fixes or goes through a shell is ever started.</summary>
    private static void AssertHarmless(FakeTools tools)
    {
        Assert.All(tools.Calls, call =>
        {
            Assert.True(Path.IsPathRooted(call.FileName));
            Assert.DoesNotContain(Path.GetFileNameWithoutExtension(call.FileName), (string[])["sh", "bash", "zsh", "cmd", "powershell", "pwsh", "env"]);
            Assert.DoesNotContain(call.Arguments, a => a is "restore" or "install" or "ci" or "i" or "fix" or "update" or "add" or "build" or "--force");
            Assert.Contains(call.Arguments[0], (string[])["list", "audit", "outdated"]);
        });
    }

    [Fact]
    public async Task Reduces_dotnet_list_package_to_counts_and_a_few_names()
    {
        Dotnet();
        var tools = new FakeTools()
            .On("dotnet", "list package --help", HelpWithNoRestore)
            .On("dotnet", "list Widget.sln package --vulnerable", VulnerableJson)
            .On("dotnet", "list Widget.sln package --outdated", OutdatedJson);

        var entry = await CheckAsync(tools);

        var report = Assert.Single(entry.Health.Reports);
        Assert.Equal((DependencyReport.NuGet, "Widget.sln", (string?)null), (report.Ecosystem, report.Target, report.Skipped));
        // One package in two frameworks counts once, at the worst of its severities.
        Assert.Equal((1, 1, 0, 1), (report.Vulnerable!.Critical, report.Vulnerable.High, report.Vulnerable.Moderate, report.Vulnerable.Low));
        Assert.Equal(
            [("Deep.Crypto", "4.3.0", "critical", "GHSA-aaaa-1111-bbbb,GHSA-cccc-2222-dddd"), ("Some.Json", "12.0.1", "high", "GHSA-5crp-9r3c-p9vr"), ("Old.Text", "1.0.0", "low", "")],
            report.TopVulnerable.Select(p => (p.Name, p.Version!, p.Severity, string.Join(',', p.Advisories))));
        Assert.Equal(2, report.Outdated);
        Assert.Equal([("Some.Json", "12.0.1", "13.0.3"), ("Test.Runner", "2.9.0", "3.1.0")], report.TopOutdated.Select(p => (p.Name, p.Current!, p.Latest!)));
        Assert.False(entry.Transient);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero), entry.Health.CheckedAt);

        Assert.Equal(
        [
            "dotnet list package --help",
            "dotnet list Widget.sln package --vulnerable --include-transitive --format json --no-restore",
            "dotnet list Widget.sln package --outdated --format json --no-restore",
        ], tools.Lines);
        Assert.All(tools.Calls, c => Assert.Equal((_dotnet, _repo, true), (c.FileName, c.WorkingDirectory, c.LowPriority)));
        AssertHarmless(tools);

        // What goes to the hub has no path, no package source and nothing of the tool's own text.
        var sent = System.Text.Json.JsonSerializer.Serialize(entry.Health, SnapshotSchema.JsonOptions);
        Assert.DoesNotContain("example.test", sent);
        Assert.DoesNotContain("/repo/", sent);
        Assert.DoesNotContain("frameworks", sent);
    }

    [Fact]
    public async Task An_sdk_that_never_restores_is_not_given_a_flag_it_does_not_know()
    {
        Dotnet();
        var tools = new FakeTools()
            .On("dotnet", "list package --help", HelpWithoutNoRestore)
            .On("dotnet", "list Widget.sln package --vulnerable", VulnerableJson)
            .On("dotnet", "list Widget.sln package --outdated", OutdatedJson);

        await CheckAsync(tools);

        Assert.DoesNotContain(tools.Calls, c => c.Arguments.Contains("--no-restore"));
        AssertHarmless(tools);
    }

    [Fact]
    public async Task A_repo_that_is_not_restored_is_skipped_and_never_restored()
    {
        Dotnet();
        var tools = new FakeTools()
            .On("dotnet", "list package --help", HelpWithNoRestore)
            .On("dotnet", "list Widget.sln package --vulnerable", NotRestoredJson, exit: 1);

        var entry = await CheckAsync(tools);

        var report = Assert.Single(entry.Health.Reports);
        Assert.Null(report.Vulnerable);
        Assert.Null(report.Outdated);
        Assert.Equal(DependencyParsers.NotRestored, report.Skipped);
        Assert.False(entry.Transient); // nothing will change by asking again in two hours
        Assert.Equal(2, tools.Calls.Count); // the second question is not asked
        AssertHarmless(tools);
    }

    [Fact]
    public async Task Offline_is_a_reason_to_ask_again_later_not_a_result()
    {
        Dotnet();
        Npm();
        var tools = new FakeTools()
            .On("dotnet", "list package --help", HelpWithNoRestore)
            .On("dotnet", "list Widget.sln package --vulnerable", OfflineJson)
            .On("npm", "audit --json", """{ "error": { "code": "ENOTFOUND", "summary": "request to https://registry.example.test failed", "detail": "" } }""", exit: 1);

        var entry = await CheckAsync(tools);

        Assert.True(entry.Transient);
        Assert.All(entry.Health.Reports, r => Assert.Equal((null, null, DependencyParsers.Unreachable), (r.Vulnerable, r.Outdated, r.Skipped)));
        Assert.DoesNotContain(tools.Lines, l => l.Contains("outdated"));
        AssertHarmless(tools);
    }

    [Fact]
    public async Task A_timeout_a_missing_tool_and_a_pinned_sdk_that_is_not_installed_are_each_said_plainly()
    {
        Dotnet();
        var hang = new FakeTools()
            .On("dotnet", "list package --help", HelpWithNoRestore)
            .On(c => c.Arguments.Contains("--vulnerable"), _ => new ToolResult(-1, "", "timed out after 30s", true));
        var timedOut = await CheckAsync(hang);
        Assert.Equal(("timed out", true), (timedOut.Health.Reports[0].Skipped, timedOut.Transient));

        var noSdk = await CheckAsync(new FakeTools().On("dotnet", "list package --help", "", exit: 145, stderr: "A compatible .NET SDK was not found."));
        Assert.Equal(("the .NET SDK this repo asks for is not installed", false), (noSdk.Health.Reports[0].Skipped, noSdk.Transient));

        var old = await CheckAsync(new FakeTools().On("dotnet", "list package --help", "Options:\n  --outdated\n  --include-transitive\n"));
        Assert.Equal("this .NET SDK is too old to report as JSON", old.Health.Reports[0].Skipped);

        var nowhere = await new DependencyChecker(new FakeTools(), new ToolLocator(_machine.Environment() with { SystemRoot = Path.Combine(_machine.Root, "nothing") }))
            .CheckAsync(_repo, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal("dotnet was not found on this machine", nowhere.Health.Reports[0].Skipped);

        var garbage = await CheckAsync(new FakeTools().On("dotnet", "list package --help", HelpWithNoRestore).On("dotnet", "list Widget.sln", "MSBUILD : error MSB1009: Project file does not exist.", exit: 1));
        Assert.Null(garbage.Health.Reports[0].Vulnerable);
        Assert.NotNull(garbage.Health.Reports[0].Skipped);
    }

    [Fact]
    public async Task Reduces_npm_audit_and_outdated_and_puts_node_on_the_path()
    {
        Npm();
        var tools = new FakeTools().On("npm", "audit --json", AuditJson, exit: 1).On("npm", "outdated --json", NpmOutdatedJson, exit: 1);

        var entry = await CheckAsync(tools);

        var report = Assert.Single(entry.Health.Reports);
        Assert.Equal((DependencyReport.Npm, "package-lock.json", (string?)null), (report.Ecosystem, report.Target, report.Skipped));
        Assert.Equal((2, 0, 0, 1), (report.Vulnerable!.Critical, report.Vulnerable.High, report.Vulnerable.Moderate, report.Vulnerable.Low));
        Assert.Equal([("left-pad", "critical", "GHSA-pppp-qqqq-rrrr"), ("wrapper", "critical", ""), ("tiny-thing", "low", "GHSA-ssss-tttt-uuuu")],
            report.TopVulnerable.Select(p => (p.Name, p.Severity, string.Join(',', p.Advisories))));
        Assert.Equal(2, report.Outdated);
        Assert.Equal([("left-pad", "1.3.0"), ("shared", "4.0.0")], report.TopOutdated.Select(p => (p.Name, p.Latest!)));

        Assert.Equal(["npm audit --json", "npm outdated --json"], tools.Lines);
        Assert.All(tools.Calls, c =>
        {
            Assert.Equal((_npm, _repo), (c.FileName, c.WorkingDirectory));
            Assert.Contains(Path.GetDirectoryName(_npm)!, c.PathPrefix); // npm is a script that asks env for node
        });
        AssertHarmless(tools);
    }

    [Theory]
    [InlineData(true, false)]  // a lockfile and no node_modules: npm would have to install
    [InlineData(false, true)]  // node_modules and no lockfile: yarn or pnpm, or nothing to audit against
    [InlineData(false, false)]
    public async Task Npm_is_only_asked_where_a_lockfile_and_node_modules_both_exist(bool lockfile, bool modules)
    {
        Npm(lockfile, modules);
        var tools = new FakeTools();

        var entry = await CheckAsync(tools);

        Assert.Empty(entry.Health.Reports);
        Assert.Empty(tools.Calls);
    }

    [Fact]
    public async Task On_windows_npm_is_run_as_node_with_its_script_not_through_cmd()
    {
        Npm();
        var programFiles = _machine.Dir("Program Files");
        var node = _machine.Tool("Program Files", "nodejs", "node.exe");
        _machine.Tool("Program Files", "nodejs", "npm.cmd");
        var cli = _machine.Tool("Program Files", "nodejs", "node_modules", "npm", "bin", "npm-cli.js");
        var tools = new FakeTools().On("node", cli + " audit --json", AuditJson, exit: 1).On("node", cli + " outdated --json", "{}");
        var windows = new ToolLocator(_machine.Environment(windows: true, variables: new(StringComparer.OrdinalIgnoreCase) { ["ProgramFiles"] = programFiles }));

        var entry = await new DependencyChecker(tools, windows).CheckAsync(_repo, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(3, Assert.Single(entry.Health.Reports).Vulnerable!.Total);
        Assert.All(tools.Calls, c => Assert.Equal((node, cli), (c.FileName, c.Arguments[0])));
        Assert.DoesNotContain(tools.Calls, c => c.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Finds_the_solution_or_project_to_ask_about()
    {
        Assert.Null(DependencyChecker.FindDotnetTarget(_repo));

        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "App.csproj"), "");
        Assert.Equal(Path.Combine("src", "App.csproj"), DependencyChecker.FindDotnetTarget(_repo));

        File.WriteAllText(Path.Combine(_repo, "Tool.fsproj"), "");
        Assert.Equal("Tool.fsproj", DependencyChecker.FindDotnetTarget(_repo));

        File.WriteAllText(Path.Combine(_repo, "Widget.slnx"), "");
        Assert.Equal("Widget.slnx", DependencyChecker.FindDotnetTarget(_repo));

        File.WriteAllText(Path.Combine(_repo, "B.sln"), "");
        File.WriteAllText(Path.Combine(_repo, "a.sln"), "");
        Assert.Equal("a.sln", DependencyChecker.FindDotnetTarget(_repo));
    }

    [Theory]
    [InlineData("")]
    [InlineData("npm ERR! something")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"unrelated\": true }")]
    public void Text_that_is_not_the_expected_json_gives_no_counts(string output)
    {
        Assert.Null(DependencyParsers.DotnetVulnerable(output).Counts);
        Assert.Null(DependencyParsers.DotnetOutdated(output).Count);
        Assert.Null(DependencyParsers.NpmAudit(output).Counts);
    }

    [Fact]
    public void Npm_edge_cases()
    {
        Assert.Equal(0, DependencyParsers.NpmOutdated("").Count);
        Assert.Equal(0, DependencyParsers.NpmOutdated("{}\n").Count);
        Assert.Equal(0, DependencyParsers.NpmAudit("""{ "auditReportVersion": 2, "vulnerabilities": {}, "metadata": { "vulnerabilities": { "total": 0 } } }""").Counts!.Total);
        Assert.Equal(("no lockfile", false), (DependencyParsers.NpmAudit("""{ "error": { "code": "ENOLOCK" } }""").Skipped, DependencyParsers.NpmAudit("""{ "error": { "code": "ENOLOCK" } }""").Transient));
        Assert.Equal("npm reported an error", DependencyParsers.NpmAudit("""{ "error": { "code": "weird code; rm -rf" } }""").Skipped);

        // npm 6 listed advisories, not packages.
        var old = DependencyParsers.NpmAudit("""
            { "advisories": { "1": { "module_name": "left-pad", "severity": "high", "url": "https://npmjs.com/advisories/1", "github_advisory_id": "GHSA-xxxx-yyyy-zzzz" } },
              "metadata": { "vulnerabilities": { "low": 0, "moderate": 0, "high": 1, "critical": 0 } } }
            """);
        Assert.Equal((1, "left-pad", "GHSA-xxxx-yyyy-zzzz"), (old.Counts!.High, old.Top[0].Name, old.Top[0].Advisories.Single()));
    }

    [Fact]
    public void Only_the_top_few_names_are_kept()
    {
        var packages = string.Join(",", Enumerable.Range(1, 40).Select(i => $$"""{ "id": "Pkg{{i:00}}", "resolvedVersion": "1.0.0", "latestVersion": "2.0.0", "vulnerabilities": [ { "severity": "{{(i == 40 ? "Critical" : "Low")}}", "advisoryurl": "https://github.com/advisories/GHSA-0000-0000-00{{i:00}}" } ] }"""));
        var json = $$"""{ "projects": [ { "path": "p", "frameworks": [ { "framework": "net8.0", "topLevelPackages": [ {{packages}} ] } ] } ] }""";

        var vulnerable = DependencyParsers.DotnetVulnerable(json);
        var outdated = DependencyParsers.DotnetOutdated(json);

        Assert.Equal((40, DependencyHealth.MaxNames, "Pkg40"), (vulnerable.Counts!.Total, vulnerable.Top.Count, vulnerable.Top[0].Name));
        Assert.Equal((40, DependencyHealth.MaxNames), (outdated.Count, outdated.Top.Count));
    }
}

public sealed class DependencyWorkerTests : IDisposable
{
    private readonly FakeMachine _machine = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero));
    private readonly AgentOptions _options = new() { Dependencies = new DependencyOptions { IntervalHours = 24, RetryHours = 2 } };
    private readonly DependencyTargets _targets = new();
    private readonly string _cacheFile;
    private readonly string _a, _b;

    public DependencyWorkerTests()
    {
        _cacheFile = Path.Combine(_machine.Root, "state", "agent-deps.json");
        _a = Repo("a");
        _b = Repo("b");
        _machine.Tool("opt", "homebrew", "bin", "npm");
        _targets.Offer([_a, _b, _a]);
    }

    public void Dispose() => _machine.Dispose();

    private string Repo(string name)
    {
        var path = _machine.Dir("dev", name);
        File.WriteAllText(Path.Combine(path, "package-lock.json"), "{}");
        Directory.CreateDirectory(Path.Combine(path, "node_modules"));
        return path;
    }

    private DependencyWorker Worker(FakeTools tools, DependencyCache cache) =>
        new(new DependencyChecker(tools, new ToolLocator(_machine.Environment()), _clock), cache, _targets, new TestOptions(_options), NullLogger<DependencyWorker>.Instance, _clock);

    private static FakeTools Healthy() => new FakeTools()
        .On("npm", "audit --json", """{ "auditReportVersion": 2, "vulnerabilities": { "left-pad": { "severity": "high", "via": [] } }, "metadata": { "vulnerabilities": { "high": 1 } } }""", exit: 1)
        .On("npm", "outdated --json", "{}");

    [Fact]
    public async Task Checks_one_repo_at_a_time_then_leaves_each_alone_for_a_day_and_survives_a_restart()
    {
        var tools = Healthy();
        var cache = new DependencyCache(_cacheFile);
        var worker = Worker(tools, cache);

        Assert.True(await worker.CheckNextAsync(CancellationToken.None));
        Assert.Equal(2, tools.Calls.Count); // one repo's two questions, and no more
        Assert.All(tools.Calls, c => Assert.Equal(_a, c.WorkingDirectory));

        Assert.True(await worker.CheckNextAsync(CancellationToken.None));
        Assert.Equal(_b, tools.Calls[^1].WorkingDirectory);
        Assert.False(await worker.CheckNextAsync(CancellationToken.None));
        Assert.Equal(4, tools.Calls.Count);

        // The state folder has it, so a restarted agent does not start the day's checks over.
        var restarted = new DependencyCache(_cacheFile);
        Assert.Equal(1, restarted.HealthOf(_a)!.High);
        var again = Worker(tools, restarted);
        _clock.Now += TimeSpan.FromHours(23);
        Assert.Null(again.NextDue());
        _clock.Now += TimeSpan.FromHours(2);
        Assert.Equal(_a, again.NextDue());
    }

    [Fact]
    public async Task Off_means_nothing_is_run()
    {
        _options.Dependencies.Enabled = false;
        var tools = Healthy();

        Assert.False(await Worker(tools, new DependencyCache(_cacheFile)).CheckNextAsync(CancellationToken.None));
        Assert.Empty(tools.Calls);
    }

    [Fact]
    public async Task Going_offline_keeps_the_last_answer_and_asks_again_sooner()
    {
        var cache = new DependencyCache(_cacheFile);
        _targets.Offer([_a]);
        await Worker(Healthy(), cache).CheckNextAsync(CancellationToken.None);
        var checkedAt = cache.HealthOf(_a)!.CheckedAt;

        _clock.Now += TimeSpan.FromHours(25);
        var offline = new FakeTools().On("npm", "audit --json", """{ "error": { "code": "EAI_AGAIN" } }""", exit: 1);
        var worker = Worker(offline, cache);
        Assert.True(await worker.CheckNextAsync(CancellationToken.None));

        Assert.Equal((1, checkedAt), (cache.HealthOf(_a)!.High, cache.HealthOf(_a)!.CheckedAt));
        Assert.True(cache.Get(_a)!.Transient);
        Assert.Null(worker.NextDue());
        _clock.Now += TimeSpan.FromHours(2);
        Assert.Equal(_a, worker.NextDue());
    }

    [Fact]
    public async Task A_repo_with_nothing_to_check_is_remembered_but_puts_nothing_in_the_snapshot()
    {
        var plain = _machine.Dir("dev", "plain");
        _targets.Offer([plain]);
        var cache = new DependencyCache(_cacheFile);
        var worker = Worker(new FakeTools(), cache);

        Assert.True(await worker.CheckNextAsync(CancellationToken.None));

        Assert.Null(cache.HealthOf(plain));
        Assert.NotNull(cache.Get(plain));
        Assert.Null(worker.NextDue());
    }

    [Fact]
    public void A_cache_file_that_is_damaged_or_about_folders_that_are_gone_is_survivable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
        File.WriteAllText(_cacheFile, "{ not json");
        var cache = new DependencyCache(_cacheFile);
        Assert.Null(cache.Get(_a));

        var health = new DependencyHealth { CheckedAt = _clock.Now, Reports = [new DependencyReport { Ecosystem = DependencyReport.Npm, Outdated = 1 }] };
        cache.Set(_a, new DependencyCacheEntry(health, _clock.Now));
        cache.Set(Path.Combine(_machine.Root, "dev", "deleted"), new DependencyCacheEntry(health, _clock.Now));
        cache.Save();

        var reloaded = new DependencyCache(_cacheFile);
        Assert.Equal(1, reloaded.HealthOf(_a)!.OutdatedTotal);
        Assert.Null(reloaded.Get(Path.Combine(_machine.Root, "dev", "deleted")));
    }
}
