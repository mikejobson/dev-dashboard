using System.Net;
using System.Text;
using DevDash.Agent.Dependencies;
using DevDash.Agent.Toolchains;
using DevDash.Contracts;
using DevDash.Tests.Shared;

namespace DevDash.Core.Tests.Agent;

/// <summary>A pretend nuget.org, served from memory. Records every address asked for; can play "offline".</summary>
internal sealed class FakeNuGet : HttpMessageHandler
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    public bool Offline { get; set; }

    public FakeNuGet()
    {
        Files[NuGetOrg.ServiceIndex] = """
            { "version": "3.0.0", "resources": [
              { "@id": "https://api.nuget.org/v3-flatcontainer/", "@type": "PackageBaseAddress/3.0.0" },
              { "@id": "https://api.nuget.org/v3/vulnerabilities/index.json", "@type": "VulnerabilityInfo/6.7.0" } ] }
            """;
        Files["https://api.nuget.org/v3/vulnerabilities/index.json"] = """
            [ { "@name": "base", "@id": "https://api.nuget.org/v3/vulnerabilities/base.json", "@updated": "2026-11-01T00:00:00Z" },
              { "@name": "update", "@id": "https://api.nuget.org/v3/vulnerabilities/update.json", "@updated": "2026-11-19T00:00:00Z" },
              { "@name": "elsewhere", "@id": "https://feed.example.test/page.json" },
              { "@name": "plain", "@id": "http://api.nuget.org/v3/vulnerabilities/plain.json" } ]
            """;
        Files["https://api.nuget.org/v3/vulnerabilities/base.json"] = """
            { "some.json": [ { "severity": 2, "url": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr", "versions": "(, 13.0.1)" } ],
              "deep.crypto": [ { "severity": 1, "url": "https://github.com/advisories/GHSA-aaaa-1111-bbbb", "versions": "[4.0.0, 4.3.1)" } ],
              "old.text": [ { "severity": 0, "url": "https://example.test/not-an-advisory", "versions": "[1.0.0]" } ],
              "fresh.lib": [ { "severity": 3, "url": "https://github.com/advisories/GHSA-zzzz-9999-zzzz", "versions": "[1.0.0, 2.0.0)" } ] }
            """;
        Files["https://api.nuget.org/v3/vulnerabilities/update.json"] = """
            { "deep.crypto": [ { "severity": 3, "url": "https://github.com/advisories/GHSA-cccc-2222-dddd", "versions": "4.0.0" } ] }
            """;
        Files["https://api.nuget.org/v3-flatcontainer/some.json/index.json"] = """{ "versions": [ "12.0.1", "13.0.1", "13.0.3", "14.0.0-beta.1" ] }""";
        Files["https://api.nuget.org/v3-flatcontainer/test.runner/index.json"] = """{ "versions": [ "2.9.0", "3.1.0" ] }""";
        Files["https://api.nuget.org/v3-flatcontainer/fresh.lib/index.json"] = """{ "versions": [ "1.0.0", "2.0.0", "2.1.0-preview.1" ] }""";
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        lock (Requests)
        {
            Requests.Add(url);
        }

        if (Offline)
        {
            throw new HttpRequestException("No such host is known. (api.nuget.org:443)");
        }

        var response = Files.TryGetValue(url, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

/// <summary>
/// The NuGet check reads restore's <c>project.assets.json</c> and nuget.org's published data, and never
/// starts <c>dotnet</c> in the repository, whose <c>global.json</c>, MSBuild files and
/// <c>nuget.config</c> would then be in charge.
/// </summary>
public sealed class NuGetCheckTests : IDisposable
{
    private readonly FakeMachine _machine = new();
    private readonly FakeNuGet _feed = new();
    private readonly string _repo;
    private readonly ToolLocator _locator;

    private static string Assets(string libraries, string dependencies) => $$"""
        { "version": 3,
          "targets": { "net8.0": {} },
          "libraries": { {{libraries}} },
          "project": { "version": "1.0.0", "frameworks": { "net8.0": { "dependencies": { {{dependencies}} } } } } }
        """;

    private const string AppAssets = """
        "Some.Json/12.0.1": { "type": "package" },
        "Deep.Crypto/4.3.0": { "type": "package" },
        "Fresh.Lib/2.0.0": { "type": "package" },
        "Private.Pkg/1.0.0": { "type": "package" },
        "Microsoft.NET.ILLink.Tasks/8.0.0": { "type": "package" },
        "Lib/1.0.0": { "type": "project", "path": "../Lib/Lib.csproj" }
        """;

    private const string AppDependencies = """
        "Some.Json": { "target": "Package", "version": "[12.0.1, )" },
        "Fresh.Lib": { "target": "Package", "version": "[2.0.0, )" },
        "Private.Pkg": { "target": "Package", "version": "[1.0.0, )" },
        "Microsoft.NET.ILLink.Tasks": { "target": "Package", "version": "[8.0.0, )", "autoReferenced": true }
        """;

    public NuGetCheckTests()
    {
        _repo = _machine.Dir("dev", "widget");
        _machine.Tool("usr", "local", "share", "dotnet", "dotnet");
        _locator = new ToolLocator(_machine.Environment());
    }

    public void Dispose() => _machine.Dispose();

    private string Write(string relative, string text)
    {
        var file = Path.Combine([_repo, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    /// <summary>A solution of three projects, two of them restored, and the files a hostile repo would use to take over a dotnet run.</summary>
    private void Solution()
    {
        Write("Widget.sln", "");
        Write("src/App/App.csproj", "<Project />");
        Write("src/App/obj/project.assets.json", Assets(AppAssets, AppDependencies));
        Write("src/Lib/Lib.csproj", "<Project />"); // never restored
        Write("tests/App.Tests/App.Tests.csproj", "<Project />");
        Write("tests/App.Tests/obj/project.assets.json", Assets("""
            "Some.Json/12.0.1": { "type": "package" },
            "Test.Runner/2.9.0": { "type": "package" },
            "Old.Text/1.0.0": { "type": "package" }
            """, """
            "Some.Json": { "target": "Package", "version": "[12.0.1, )" },
            "Test.Runner": { "target": "Package", "version": "[2.9.0, )" }
            """));

        Write("global.json", """{ "sdk": { "paths": [ ".evil-sdk" ], "version": "8.0.100" } }""");
        Write("Directory.Build.props", """<Project><Target Name="Pwn" BeforeTargets="Restore"><Exec Command="touch pwned" /></Target></Project>""");
        Write("nuget.config", """<configuration><packageSources><clear /><add key="corp" value="https://feed.example.test/v3/index.json" /></packageSources></configuration>""");
    }

    private Task<DependencyCacheEntry> CheckAsync(FakeTools tools, NuGetOrg? nuget = null) =>
        new DependencyChecker(tools, _locator, new TestClock(new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero)), nuget ?? new NuGetOrg(_feed))
            .CheckAsync(_repo, TimeSpan.FromSeconds(30), CancellationToken.None);

    [Fact]
    public async Task Reads_restore_output_and_nuget_org_data_without_starting_dotnet()
    {
        Solution();
        var tools = new FakeTools();

        var entry = await CheckAsync(tools);

        var report = Assert.Single(entry.Health.Reports);
        Assert.Equal((DependencyReport.NuGet, "Widget.sln"), (report.Ecosystem, report.Target));
        Assert.Equal("1 of 3 projects not restored", report.Skipped);

        // Transitive packages count too; one package in two projects counts once, at its worst severity.
        Assert.Equal((1, 1, 0, 1), (report.Vulnerable!.Critical, report.Vulnerable.High, report.Vulnerable.Moderate, report.Vulnerable.Low));
        Assert.Equal(
            [("Deep.Crypto", "4.3.0", "critical", "GHSA-aaaa-1111-bbbb,GHSA-cccc-2222-dddd"), ("Some.Json", "12.0.1", "high", "GHSA-5crp-9r3c-p9vr"), ("Old.Text", "1.0.0", "low", "")],
            report.TopVulnerable.Select(p => (p.Name, p.Version!, p.Severity, string.Join(',', p.Advisories))));

        // Newest stable version; a private package nuget.org does not have, and the SDK's own, are left out.
        Assert.Equal(2, report.Outdated);
        Assert.Equal([("Some.Json", "12.0.1", "13.0.3"), ("Test.Runner", "2.9.0", "3.1.0")], report.TopOutdated.Select(p => (p.Name, p.Current!, p.Latest!)));
        Assert.False(entry.Transient);

        // Nothing was run, and only nuget.org was asked, over https: never the repo's own feed.
        Assert.Empty(tools.Calls);
        Assert.All(_feed.Requests, url => Assert.StartsWith("https://api.nuget.org/", url));
        Assert.DoesNotContain(_feed.Requests, url => url.Contains("ill", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(_repo, "pwned")));

        var sent = System.Text.Json.JsonSerializer.Serialize(entry.Health, SnapshotSchema.JsonOptions);
        Assert.DoesNotContain("example.test", sent);
        Assert.DoesNotContain(_repo, sent);
    }

    [Fact]
    public async Task The_vulnerability_data_is_downloaded_once_for_many_repositories()
    {
        Solution();
        var nuget = new NuGetOrg(_feed);

        await CheckAsync(new FakeTools(), nuget);
        var first = _feed.Requests.Count;
        await CheckAsync(new FakeTools(), nuget);

        Assert.Equal(1, _feed.Requests.Count(r => r == NuGetOrg.ServiceIndex));
        Assert.Equal(first, _feed.Requests.Count);
    }

    [Fact]
    public async Task A_repo_that_is_not_restored_is_skipped_and_never_restored()
    {
        Write("Widget.sln", "");
        Write("src/App/App.csproj", "<Project />");
        var tools = new FakeTools();

        var entry = await CheckAsync(tools);

        var report = Assert.Single(entry.Health.Reports);
        Assert.Equal((null, null, DependencyParsers.NotRestored), (report.Vulnerable, report.Outdated, report.Skipped));
        Assert.False(entry.Transient); // nothing will change by asking again in two hours
        Assert.Empty(tools.Calls);
        Assert.Empty(_feed.Requests);
    }

    [Fact]
    public async Task An_assets_file_that_is_a_link_out_of_the_repo_is_not_read()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // symbolic links need developer mode there
        }

        Write("Widget.sln", "");
        Write("src/App/App.csproj", "<Project />");
        var elsewhere = Path.Combine(_machine.Dir("elsewhere"), "project.assets.json");
        File.WriteAllText(elsewhere, Assets(AppAssets, AppDependencies));
        Directory.CreateDirectory(Path.Combine(_repo, "src", "App", "obj"));
        File.CreateSymbolicLink(Path.Combine(_repo, "src", "App", "obj", "project.assets.json"), elsewhere);

        var entry = await CheckAsync(new FakeTools());

        Assert.Equal(DependencyParsers.NotRestored, Assert.Single(entry.Health.Reports).Skipped);
    }

    [Fact]
    public async Task Offline_is_a_reason_to_ask_again_later_not_a_result()
    {
        Solution();
        File.WriteAllText(Path.Combine(_repo, "package-lock.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_repo, "node_modules"));
        _machine.Tool("opt", "homebrew", "bin", "npm");
        _feed.Offline = true;
        var tools = new FakeTools().On("npm", "audit --json", """{ "error": { "code": "ENOTFOUND", "summary": "request to https://registry.example.test failed", "detail": "" } }""", exit: 1);

        var entry = await new DependencyChecker(tools, new ToolLocator(_machine.Environment()), null, new NuGetOrg(_feed)).CheckAsync(_repo, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(entry.Transient);
        Assert.Equal(2, entry.Health.Reports.Count);
        Assert.All(entry.Health.Reports, r => Assert.Equal((null, null, DependencyParsers.Unreachable), (r.Vulnerable, r.Outdated, r.Skipped)));
        Assert.DoesNotContain(tools.Lines, l => l.Contains("outdated"));
        Assert.Equal([NuGetOrg.ServiceIndex], _feed.Requests); // the second question is not asked
    }

    [Fact]
    public async Task Npm_and_node_inside_the_repository_are_never_run()
    {
        File.WriteAllText(Path.Combine(_repo, "package-lock.json"), "{}");
        var bin = Path.Combine(_repo, "node_modules", ".bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "npm"), "#!/bin/sh\ntouch pwned\n");
        File.WriteAllText(Path.Combine(bin, "node"), "");
        var system = _machine.Tool("opt", "homebrew", "bin", "npm");
        var tools = new FakeTools().On("npm", "audit --json", "{ \"vulnerabilities\": {}, \"metadata\": {} }").On("npm", "outdated --json", "{}");

        // The repo's own .bin comes first on the PATH, as it would after `export PATH=node_modules/.bin:$PATH`, absolute or not.
        var locator = new ToolLocator(_machine.Environment(path: [bin, "node_modules/.bin"]));
        await new DependencyChecker(tools, locator, null, new NuGetOrg(_feed)).CheckAsync(_repo, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotEmpty(tools.Calls);
        Assert.All(tools.Calls, c =>
        {
            Assert.Equal(system, c.FileName);
            Assert.DoesNotContain(c.PathPrefix, p => p.StartsWith(_repo, StringComparison.Ordinal));
            Assert.DoesNotContain(_repo, c.Environment["PATH"]);
            Assert.DoesNotContain("node_modules/.bin", c.Environment["PATH"]);
        });

        // With only the repo's npm about, npm is simply not there.
        var none = await new DependencyChecker(new FakeTools(), new ToolLocator(_machine.Environment(path: [bin]) with { SystemRoot = Path.Combine(_machine.Root, "nothing") }), null, new NuGetOrg(_feed))
            .CheckAsync(_repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal("npm was not found on this machine", Assert.Single(none.Health.Reports).Skipped);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0.0", 0)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.0.0-beta", "1.0.0", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("1.0.0-beta", "1.0.0-beta.1", -1)]
    [InlineData("1.0.0+build", "1.0.0", 0)]
    [InlineData("10.0.0", "9.9.9", 1)]
    public void Orders_versions_as_nuget_does(string a, string b, int expected)
    {
        Assert.True(NuGetVersion.TryParse(a, out var left));
        Assert.True(NuGetVersion.TryParse(b, out var right));
        Assert.Equal(expected, Math.Sign(left.CompareTo(right)));
    }

    [Theory]
    [InlineData("[1.0.0, 2.0.0)", "1.0.0", true)]
    [InlineData("[1.0.0, 2.0.0)", "2.0.0", false)]
    [InlineData("(1.0.0, 2.0.0]", "1.0.0", false)]
    [InlineData("(1.0.0, 2.0.0]", "2.0.0", true)]
    [InlineData("(, 1.2.3)", "0.0.1", true)]
    [InlineData("(, 1.2.3)", "1.2.3", false)]
    [InlineData("[1.2.3]", "1.2.3", true)]
    [InlineData("[1.2.3]", "1.2.4", false)]
    [InlineData("1.2.3", "9.0.0", true)]
    [InlineData("1.2.3", "1.2.2", false)]
    [InlineData("[1.0.0, )", "1.0.0-beta", false)]
    public void Reads_version_ranges(string range, string version, bool expected)
    {
        Assert.True(NuGetVersionRange.TryParse(range, out var parsed));
        Assert.Equal(expected, parsed.Contains(NuGetVersion.Parse(version)));
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json", true)]
    [InlineData("http://api.nuget.org/v3/index.json", false)]
    [InlineData("https://feed.example.test/v3/index.json", false)]
    [InlineData("https://nuget.org.example.test/v3/index.json", false)]
    [InlineData("https://evilnuget.org/v3/index.json", false)]
    public void Only_nuget_org_over_https_is_asked(string url, bool allowed) =>
        Assert.Equal(allowed, NuGetOrg.IsNuGetOrg(new Uri(url)));
}
