using DevDash.Contracts;
using DevDash.Core.Cleanup;
using DevDash.Hub;
using DevDash.Hub.Alerts;
using DevDash.Hub.Api;
using DevDash.Hub.Auth;
using DevDash.Hub.Components;
using DevDash.Hub.Data;
using DevDash.Hub.GitHub;
using DevDash.Hub.Mcp;
using DevDash.Hub.Services;
using DevDash.Hub.T3;
using DevDash.Hub.Updates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<HubOptions>(builder.Configuration.GetSection(HubOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection(GitHubOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);

// ---- storage: SQLite through EF Core ------------------------------------------------------------
builder.Services.AddDbContextFactory<DevDashDbContext>((services, options) =>
{
    var connection = builder.Configuration.GetConnectionString("DevDash");
    if (string.IsNullOrWhiteSpace(connection))
    {
        var dir = HubPaths.DataDirectory(services.GetRequiredService<IHostEnvironment>(), services.GetRequiredService<IOptions<HubOptions>>().Value);
        connection = $"Data Source={Path.Combine(dir, "devdash.db")}";
    }

    options.UseSqlite(connection);
});

// Data-protection keys (antiforgery, Blazor circuits) live next to the database so they survive
// container rebuilds instead of being regenerated in the container's home directory.
var hubOptionsAtStartup = builder.Configuration.GetSection(HubOptions.SectionName).Get<HubOptions>() ?? new HubOptions();
builder.Services.AddDataProtection()
    .SetApplicationName("DevDash")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(HubPaths.DataDirectory(builder.Environment, hubOptionsAtStartup), "keys")));

// ---- auth ---------------------------------------------------------------------------------------
// Browser users and agents authenticate differently. The browser side goes through
// IBrowserIdentityProvider so phase 4 can swap in Tailscale identity headers.
var authMode = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()?.Mode ?? "LocalDev";
if (authMode.Equals("LocalDev", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IBrowserIdentityProvider, LocalDevIdentityProvider>();
}
else if (authMode.Equals("Tailscale", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IBrowserIdentityProvider, TailscaleIdentityProvider>();
}
else
{
    throw new InvalidOperationException($"Auth:Mode '{authMode}' is not supported. Use 'LocalDev' or 'Tailscale'.");
}

builder.Services.AddAuthentication(BrowserAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, BrowserAuthenticationHandler>(BrowserAuthenticationHandler.SchemeName, null)
    .AddScheme<AuthenticationSchemeOptions, AgentKeyAuthenticationHandler>(AgentKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder(BrowserAuthenticationHandler.SchemeName).RequireAuthenticatedUser().Build())
    .AddPolicy(ApiEndpoints.AgentPolicy, policy => policy
        .AddAuthenticationSchemes(AgentKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .RequireClaim(AgentKeyAuthenticationHandler.KeyIdClaim));
builder.Services.AddSingleton<ApiKeyService>();

// ---- dashboard state ----------------------------------------------------------------------------
builder.Services.AddSingleton<DashboardNotifier>();
builder.Services.AddSingleton<SnapshotStore>();
builder.Services.AddSingleton<IDescriptionProvider, DefaultDescriptionProvider>();
builder.Services.AddSingleton<RepoCatalog>();
builder.Services.AddSingleton(_ => new CleanupEngine());
builder.Services.AddSingleton<ActionSignals>();
builder.Services.AddSingleton<ActionQueue>();
builder.Services.AddSingleton<RefreshService>();
builder.Services.AddScoped<RefreshBoard>();
builder.Services.AddSingleton<TombstoneStore>();
builder.Services.AddScoped<ActionBoard>();
builder.Services.AddHostedService<ActionExpiryService>();
builder.Services.AddSingleton<FindingsService>();
builder.Services.AddSingleton<SafetyService>();
builder.Services.AddSingleton<NavCounts>();

// ---- GitHub -------------------------------------------------------------------------------------
builder.Services.AddSingleton<IGitHubTokenProvider, GitHubTokenProvider>();
builder.Services.AddHttpClient<GitHubClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<GitHubStore>();
builder.Services.AddSingleton<GitHubPoller>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GitHubPoller>());

// ---- T3 Code threads and GitHub issues (0.7.0) ---------------------------------------------------
builder.Services.AddT3Integration();

// ---- alerts --------------------------------------------------------------------------------------
// No request logging for this client: the addresses it posts to are secrets (a webhook id, an ntfy topic).
builder.Services.AddHttpClient<AlertSender>(c => c.Timeout = TimeSpan.FromSeconds(30)).RemoveAllLoggers();
builder.Services.AddSingleton<AlertSettings>();
builder.Services.AddSingleton<AlertService>();
builder.Services.AddHostedService<AlertWorker>();

// ---- agent releases and updates -----------------------------------------------------------------
builder.Services.AddHttpClient<ReleaseClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<ReleaseStore>();
builder.Services.AddSingleton<ReleasePoller>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReleasePoller>());
builder.Services.AddSingleton<AgentUpdates>();

// ---- MCP server for Claude (§19) ------------------------------------------------------------------
builder.Services.AddDevDashMcp();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = SnapshotSchema.JsonOptions.DefaultIgnoreCondition;
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Create the schema, make sure an agent key exists, and load the last known state.
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<DevDashDbContext>>().CreateDbContextAsync())
{
    await db.Database.EnsureCreatedAsync();

    // EnsureCreated does nothing for a database made by an earlier version, so add any tables it lacks.
    var added = await SchemaUpgrade.CreateMissingTablesAsync(db);
    if (added.Count > 0)
    {
        app.Logger.LogInformation("Added to the existing database: {Names}", string.Join(", ", added));
    }
}

await app.Services.GetRequiredService<ApiKeyService>().EnsureSeedKeyAsync();
await app.Services.GetRequiredService<SnapshotStore>().LoadAsync();
await app.Services.GetRequiredService<GitHubStore>().LoadAsync();
await app.Services.GetRequiredService<ReleaseStore>().LoadAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// Friendly not-found page for browsers. API and agent calls keep their plain status codes.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/mcp"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapDevDashApi();
app.MapDevDashMcp();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
