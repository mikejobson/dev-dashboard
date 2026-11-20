using DevDash.Contracts;
using DevDash.Core.Cleanup;
using DevDash.Core.Cleanup.Rules;
using static DevDash.Core.Tests.Cleanup.Build;

namespace DevDash.Core.Tests.Cleanup;

public class ToolchainMismatchRuleTests
{
    private readonly ToolchainMismatchRule _rule = new();

    private static MachineSnapshot MachineWith(ToolchainInventory? inventory, params DevEntry[] entries)
    {
        var snapshot = Machine("mac", entries);
        return snapshot with { Machine = snapshot.Machine with { Toolchains = inventory } };
    }

    private static readonly ToolchainInventory Has = new()
    {
        Tools =
        [
            new InstalledToolchain { Tool = ToolNames.Dotnet, Versions = ["9.0.305"] },
            new InstalledToolchain { Tool = ToolNames.Node, Versions = ["20.11.0"], Managers = ["nvm"] },
            new InstalledToolchain { Tool = ToolNames.Python, Versions = ["3.12.4"], Managers = ["pyenv"] },
        ],
    };

    [Fact]
    public void Flags_a_repo_whose_global_json_no_installed_sdk_satisfies()
    {
        var repo = Repo("widget") with { Pins = [new ToolchainPin { Tool = ToolNames.Dotnet, Version = "10.0.100", Source = "global.json", RollForward = "latestFeature" }] };

        var finding = Assert.Single(_rule.Evaluate(Context(MachineWith(Has, repo))));

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Equal("widget: .NET SDK 10.0.100 is not on mac", finding.Title);
        Assert.Equal("global.json needs 10.0.100 or a later 10.0 SDK; this machine has 9.0.305.", finding.Explanation);
        Assert.Equal(["cat '/dev/widget/global.json'", "dotnet --list-sdks"], Inspects(finding));
        Assert.Empty(Fixes(finding));
        Assert.Equal("https://dotnet.microsoft.com/download/dotnet/10.0", finding.Url);
        Assert.Equal("github.com/acme/widget", finding.RepoKey);
    }

    [Fact]
    public void Several_pins_are_one_finding_with_an_install_command_where_a_version_manager_is_there()
    {
        var repo = Repo("widget") with
        {
            Pins =
            [
                new ToolchainPin { Tool = ToolNames.Node, Version = "22", Source = ".nvmrc" },
                new ToolchainPin { Tool = ToolNames.Node, Version = ">=18", Source = "package.json engines.node" }, // satisfied
                new ToolchainPin { Tool = ToolNames.Python, Version = "3.13", Source = ".python-version" },
                new ToolchainPin { Tool = ToolNames.Python, Version = "some-env", Source = ".tool-versions" }, // unknown: not reported
            ],
        };

        var finding = Assert.Single(_rule.Evaluate(Context(MachineWith(Has, repo))));

        Assert.Equal(FindingSeverity.Low, finding.Severity);
        Assert.Equal("widget: 2 pinned tool versions not on mac", finding.Title);
        Assert.Equal(["nvm install '22'", "pyenv install --skip-existing '3.13'"], Fixes(finding));
        Assert.DoesNotContain("engines", finding.Explanation);
        Assert.Null(finding.Url);
    }

    [Fact]
    public void Says_nothing_when_every_pin_is_met_or_the_machine_sent_no_inventory()
    {
        var met = Repo("widget") with { Pins = [new ToolchainPin { Tool = ToolNames.Dotnet, Version = "9.0.100", Source = "global.json", RollForward = "latestFeature" }] };
        var unmet = Repo("gadget") with { Pins = [new ToolchainPin { Tool = ToolNames.Dotnet, Version = "10.0.100", Source = "global.json" }] };

        Assert.Empty(_rule.Evaluate(Context(MachineWith(Has, met, Repo("plain")))));
        Assert.Empty(_rule.Evaluate(Context(MachineWith(null, unmet))));
    }

    [Fact]
    public void The_fingerprint_follows_the_pin_not_what_happens_to_be_installed()
    {
        var repo = Repo("widget") with { Pins = [new ToolchainPin { Tool = ToolNames.Dotnet, Version = "10.0.100", Source = "global.json" }] };
        var more = Has with { Tools = [new InstalledToolchain { Tool = ToolNames.Dotnet, Versions = ["9.0.305", "8.0.404"] }] };

        var first = Assert.Single(_rule.Evaluate(Context(MachineWith(Has, repo))));
        var second = Assert.Single(_rule.Evaluate(Context(MachineWith(more, repo))));
        var other = Assert.Single(_rule.Evaluate(Context(MachineWith(Has, repo with { Pins = [new ToolchainPin { Tool = ToolNames.Dotnet, Version = "11.0.100", Source = "global.json" }] }))));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual(first.Fingerprint, other.Fingerprint);
    }
}

public class VulnerableDependenciesRuleTests
{
    private readonly VulnerableDependenciesRule _rule = new();

    private static DevEntry With(params DependencyReport[] reports) =>
        Repo("widget") with { Dependencies = new DependencyHealth { CheckedAt = DaysAgo(2), Reports = reports } };

    [Fact]
    public void A_critical_vulnerability_is_a_high_finding_with_names_and_commands()
    {
        var entry = With(
            new DependencyReport
            {
                Ecosystem = DependencyReport.NuGet,
                Target = "Widget.sln",
                Vulnerable = new SeverityCounts { Critical = 1, High = 1, Moderate = 2 },
                TopVulnerable =
                [
                    new VulnerablePackage { Name = "Some.Json", Version = "12.0.1", Severity = "critical", Advisories = ["GHSA-aaaa-bbbb-cccc"] },
                    new VulnerablePackage { Name = "Other.Lib", Version = "1.0.0", Severity = "high", Advisories = ["GHSA-dddd-eeee-ffff"] },
                    new VulnerablePackage { Name = "Mild.Lib", Version = "2.0.0", Severity = "moderate" },
                ],
            },
            new DependencyReport { Ecosystem = DependencyReport.Npm, Target = "package-lock.json", Vulnerable = new SeverityCounts { Low = 3 } });

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", entry))));

        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Equal("widget: 2 serious vulnerabilities in its dependencies", finding.Title);
        Assert.Equal("1 critical, 1 high: Some.Json 12.0.1, Other.Lib 1.0.0. Checked 2 days ago on mac. 5 more of lower severity.", finding.Explanation);
        Assert.Equal(["cd '/dev/widget' && dotnet list 'Widget.sln' package --vulnerable --include-transitive"], Inspects(finding));
        Assert.Empty(Fixes(finding)); // npm has nothing serious here, so no npm commands
        Assert.Equal("https://github.com/advisories/GHSA-aaaa-bbbb-cccc", finding.Url);
    }

    [Fact]
    public void High_only_is_medium_and_npm_gets_its_own_commands()
    {
        var entry = With(new DependencyReport
        {
            Ecosystem = DependencyReport.Npm,
            Target = "package-lock.json",
            Vulnerable = new SeverityCounts { High = 1 },
            TopVulnerable = [new VulnerablePackage { Name = "left-pad", Severity = "high", Advisories = ["GHSA-1111-2222-3333"] }],
        });

        var finding = Assert.Single(_rule.Evaluate(Context(Machine("mac", entry))));

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Equal("widget: 1 high-severity vulnerability in its dependencies", finding.Title);
        Assert.Equal(["cd '/dev/widget' && npm audit"], Inspects(finding));
        Assert.Equal(["cd '/dev/widget' && npm audit fix"], Fixes(finding));
        Assert.Null(Fix(finding).Action); // nothing an agent runs
    }

    [Fact]
    public void Moderate_and_low_alone_skipped_checks_and_unchecked_repos_are_not_findings()
    {
        var mild = With(new DependencyReport { Ecosystem = DependencyReport.Npm, Vulnerable = new SeverityCounts { Moderate = 4, Low = 1 } });
        var skipped = With(new DependencyReport { Ecosystem = DependencyReport.NuGet, Skipped = "not restored" });

        Assert.Empty(_rule.Evaluate(Context(Machine("mac", mild))));
        Assert.Empty(_rule.Evaluate(Context(Machine("mac", skipped))));
        Assert.Empty(_rule.Evaluate(Context(Machine("mac", Repo("widget")))));
    }

    [Fact]
    public void A_new_advisory_is_a_new_finding_but_a_recheck_is_not()
    {
        DevEntry Entry(DateTimeOffset at, params string[] advisories) => Repo("widget") with
        {
            Dependencies = new DependencyHealth
            {
                CheckedAt = at,
                Reports =
                [
                    new DependencyReport
                    {
                        Ecosystem = DependencyReport.Npm,
                        Vulnerable = new SeverityCounts { High = advisories.Length },
                        TopVulnerable = advisories.Select(a => new VulnerablePackage { Name = "pkg-" + a, Severity = "high", Advisories = [a] }).ToList(),
                    },
                ],
            },
        };

        string Print(DevEntry entry) => Assert.Single(_rule.Evaluate(Context(Machine("mac", entry)))).Fingerprint;

        Assert.Equal(Print(Entry(DaysAgo(3), "GHSA-a")), Print(Entry(DaysAgo(0), "GHSA-a")));
        Assert.NotEqual(Print(Entry(DaysAgo(3), "GHSA-a")), Print(Entry(DaysAgo(3), "GHSA-a", "GHSA-b")));
    }
}
