using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Mcp;

/// <summary>
/// Authenticates MCP clients by <c>Authorization: Bearer ddm_…</c>, against <see cref="McpTokenService"/> only.
/// This is the only scheme <c>/mcp</c> accepts: a browser's Tailscale identity is not enough, and an
/// agent key is not an MCP token.
/// </summary>
public sealed class McpTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    McpTokenService tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "McpToken";
    public const string TokenIdClaim = "devdash:mcp-token-id";
    public const string TokenNameClaim = "devdash:mcp-token-name";
    public const string ScopeClaim = "devdash:mcp-scope";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = await tokens.ValidateAsync(header[bearer.Length..], Context.RequestAborted);
        if (token is null)
        {
            return AuthenticateResult.Fail("Unknown or revoked MCP token.");
        }

        var claims = new List<Claim>
        {
            new(TokenIdClaim, token.Id.ToString(CultureInfo.InvariantCulture)),
            new(TokenNameClaim, token.Name),
            new(ScopeClaim, token.Scope),
            new(ClaimTypes.Name, "mcp:" + token.Name),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"devdash-mcp\"";
        return Task.CompletedTask;
    }
}

/// <summary>Who is calling, from the token's claims.</summary>
public static class McpCaller
{
    public static string TokenName(ClaimsPrincipal? user) => user?.FindFirst(McpTokenAuthenticationHandler.TokenNameClaim)?.Value ?? "unknown";

    /// <summary>What the audit trail records as "queued by" for an action an MCP client asked for.</summary>
    public static string QueuedBy(ClaimsPrincipal? user) => "mcp:" + TokenName(user);
}
