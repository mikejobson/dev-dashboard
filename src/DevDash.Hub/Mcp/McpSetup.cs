using System.Threading.RateLimiting;
using DevDash.Hub.Updates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace DevDash.Hub.Mcp;

/// <summary><c>Hub:Mcp</c>: the MCP server at <c>/mcp</c> (§19).</summary>
public sealed class McpOptions
{
    /// <summary>Map <c>/mcp</c> at all. Even when on, nothing answers without a token from the Claude page.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Requests per minute per token, tool listings included. Past it the client gets 429.</summary>
    public int RequestsPerMinute { get; set; } = 120;
}

/// <summary>
/// The MCP server: Streamable HTTP at <c>/mcp</c>, stateless (nothing to lose when the hub restarts),
/// bearer tokens only, rate-limited per token. Tools live in the classes listed in <see cref="ToolTypes"/>.
/// </summary>
public static class McpSetup
{
    /// <summary>Any valid MCP token. The endpoint's own policy: browser identity does not satisfy it.</summary>
    public const string Policy = "Mcp";

    /// <summary>A token with the <see cref="McpScopes.ReadActions"/> scope. Put on each tool that queues an action.</summary>
    public const string ActionsPolicy = "McpActions";

    private const string RateLimitPolicy = "mcp";

    /// <summary>
    /// Every class of tools, in one place. To add tools, add a class with <c>[McpServerToolType]</c>
    /// here; mark tools that change anything <c>[Authorize(Policy = McpSetup.ActionsPolicy)]</c>.
    /// </summary>
    public static readonly Type[] ToolTypes = [typeof(DashboardTools), typeof(T3Tools), typeof(ActionTools), typeof(HistoryTools)];

    public static IServiceCollection AddDevDashMcp(this IServiceCollection services)
    {
        services.AddSingleton<McpTokenService>();
        services.AddHttpContextAccessor();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, McpTokenAuthenticationHandler>(McpTokenAuthenticationHandler.SchemeName, null);
        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy
                .AddAuthenticationSchemes(McpTokenAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireClaim(McpTokenAuthenticationHandler.TokenIdClaim))
            .AddPolicy(ActionsPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(McpTokenAuthenticationHandler.ScopeClaim, McpScopes.ReadActions));

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirst(McpTokenAuthenticationHandler.TokenIdClaim)?.Value ?? "",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, http.RequestServices.GetRequiredService<IOptionsMonitor<HubOptions>>().CurrentValue.Mcp.RequestsPerMinute),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "devdash", Title = "DevDash", Version = HubVersion.Current };
                o.ServerInstructions =
                    "DevDash knows the state of every git working tree under ~/Developer on each of the user's machines " +
                    "(as of each agent's last scan), plus their GitHub pull requests and CI. Start with list_projects or " +
                    "at_risk_work; use get_project for one repository in depth. Paths are as the machine reported them. " +
                    "Action tools only queue a request for a machine's agent, which re-validates it; check the outcome with action_status.";
            })
            .WithHttpTransport(o => o.Stateless = true)
            // The cast picks the overload that scans types: a bare array would bind to WithTools<T>(T target).
            .WithTools((IEnumerable<Type>)ToolTypes)
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
            {
                var logger = context.Services!.GetRequiredService<ILoggerFactory>().CreateLogger("DevDash.Hub.Mcp");
                logger.LogInformation("MCP token {Token} called {Tool}", McpCaller.TokenName(context.User), context.Params?.Name);
                return await next(context, ct);
            }))
            .AddAuthorizationFilters();

        return services;
    }

    public static void MapDevDashMcp(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<HubOptions>>().Value.Mcp.Enabled)
        {
            return;
        }

        app.MapMcp("/mcp").RequireAuthorization(Policy).RequireRateLimiting(RateLimitPolicy);
    }
}
