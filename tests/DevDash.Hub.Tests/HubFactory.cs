using DevDash.Contracts;
using DevDash.Hub.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DevDash.Hub.Tests;

/// <summary>Boots the real hub against an in-memory SQLite database, with GitHub polling switched off.</summary>
public sealed class HubFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString = $"Data Source=file:devdash-test-{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keepAlive;

    public HubFactory()
    {
        // A shared in-memory database lives as long as one connection stays open.
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public bool RequireLoopback { get; init; }

    /// <summary>Replaces the hub's clock, for tests about expiry.</summary>
    public TimeProvider? Time { get; init; }

    /// <summary>
    /// What "GitHub" answers when the hub asks for the agent's releases. The hub never reaches the real
    /// GitHub in tests. Set before the first request to the hub; the default is no releases at all.
    /// </summary>
    public string ReleasesJson { get; set; } = "[]";

    /// <summary>The release lookups the hub made: URL and Authorization header.</summary>
    public List<(string Url, string? Auth)> ReleaseRequests { get; } = [];

    public static string ReleaseJson(string tag, bool signed = true, bool draft = false, bool prerelease = false) => $$"""
        { "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/mikejobson/dev-dashboard/releases/tag/{{tag}}", "published_at": "2026-11-18T10:00:00Z",
          "assets": [ { "name": "checksums.txt" }, { "name": "devdash-agent-osx-arm64.tar.gz" }{{(signed ? """, { "name": "checksums.txt.sig" }""" : "")}} ] }
        """;

    /// <summary>Makes the hub look for the latest release now, as its hourly check would.</summary>
    public async Task<DevDash.Hub.Updates.LatestRelease?> CheckReleasesAsync(params string[] releases)
    {
        if (releases.Length > 0)
        {
            ReleasesJson = $"[{string.Join(",", releases)}]";
        }

        await Services.GetRequiredService<DevDash.Hub.Updates.ReleasePoller>().PollOnceAsync(CancellationToken.None);
        return Services.GetRequiredService<DevDash.Hub.Updates.ReleaseStore>().Current;
    }

    private sealed class ReleaseStub(HubFactory factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (factory.ReleaseRequests)
            {
                factory.ReleaseRequests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(factory.ReleasesJson, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DevDash", _connectionString);
        builder.UseSetting("GitHub:Enabled", "false");
        builder.UseSetting("Auth:RequireLoopback", RequireLoopback ? "true" : "false");
        builder.ConfigureServices(services =>
        {
            if (Time is not null)
            {
                services.AddSingleton(Time);
            }

            services.AddHttpClient<DevDash.Hub.Updates.ReleaseClient>().ConfigurePrimaryHttpMessageHandler(() => new ReleaseStub(this));
            services.AddSingleton<DevDash.Hub.GitHub.IGitHubTokenProvider>(new NoToken());
        });
    }

    public async Task<string> IssueKeyAsync(string label = "test", string? machineId = null)
    {
        var keys = Services.GetRequiredService<ApiKeyService>();
        return (await keys.IssueAsync(label, machineId)).Plaintext;
    }

    public HttpClient CreateAgentClient(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAlive.Dispose();
        }
    }

    /// <summary>As in production with no token configured and no <c>gh</c>: GitHub is asked anonymously.</summary>
    private sealed class NoToken : DevDash.Hub.GitHub.IGitHubTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public void Invalidate()
        {
        }
    }

    /// <param name="updateMode">What the agent reports as its update mode. Null is an agent from before self-update (0.1.x).</param>
    public static MachineSnapshot Snapshot(string machineId = "mac", DateTimeOffset? now = null, bool actions = false, string? updateMode = null, string version = "0.1.0")
    {
        var at = now ?? DateTimeOffset.UtcNow;
        return new MachineSnapshot
        {
            Machine = new MachineInfo { MachineId = machineId, Hostname = machineId + "-host", Os = "macOS", AgentVersion = version, ActionsEnabled = actions, UpdateMode = updateMode },
            ScannedAt = at,
            ScanDurationMs = 123,
            Roots = ["/dev"],
            Entries =
            [
                new DevEntry
                {
                    Root = "/dev",
                    Name = "widget",
                    Path = "/dev/widget",
                    Kind = EntryKind.GitRepo,
                    LastModified = at.AddDays(-40),
                    Description = new DescriptionInputs { ReadmeExcerpt = "A widget from the README." },
                    Git = new GitState
                    {
                        Branch = "feature/login",
                        HeadSha = "1111111111111111111111111111111111111111",
                        Upstream = "origin/feature/login",
                        Ahead = 2,
                        Unstaged = 3,
                        NewestTrackedChangeAt = at.AddDays(-40),
                        DefaultBranch = "main",
                        CommonDir = "/dev/widget/.git",
                        LastCommit = new CommitInfo { Sha = "1111111111111111111111111111111111111111", Date = at.AddDays(-41), Subject = "Add login form", Author = "me" },
                        Remotes = [new RemoteInfo { Name = "origin", Url = "https://github.com/acme/widget.git", Normalized = "github.com/acme/widget" }],
                        Branches =
                        [
                            new BranchInfo { Name = "feature/login", Upstream = "origin/feature/login", Ahead = 2, IsCurrent = true, MergedIntoDefault = false, LastCommitDate = at.AddDays(-41) },
                            new BranchInfo { Name = "main", Upstream = "origin/main", MergedIntoDefault = true, LastCommitDate = at.AddDays(-60) },
                            new BranchInfo { Name = "old-fix", MergedIntoDefault = true, LastCommitDate = at.AddDays(-90) },
                        ],
                        Worktrees = [new WorktreeInfo { Path = "/dev/widget", Branch = "feature/login" }],
                    },
                },
                new DevEntry
                {
                    Root = "/dev",
                    Name = "Reference Docs",
                    Path = "/dev/Reference Docs",
                    Kind = EntryKind.NonGitFolder,
                    Folder = new FolderSummary { FileCount = 3, DirectoryCount = 0, SampleNames = ["spec.pdf"] },
                    LastModified = at.AddDays(-200),
                },
            ],
        };
    }
}
