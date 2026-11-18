using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Auth;

/// <summary>Authenticates agents by <c>Authorization: Bearer &lt;api key&gt;</c>.</summary>
public sealed class AgentKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyService keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AgentKey";
    public const string KeyIdClaim = "devdash:key-id";
    public const string MachineClaim = "devdash:machine-id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var key = await keys.ValidateAsync(header[bearer.Length..], Context.RequestAborted);
        if (key is null)
        {
            return AuthenticateResult.Fail("Unknown or revoked API key.");
        }

        var claims = new List<Claim> { new(KeyIdClaim, key.Id.ToString(CultureInfo.InvariantCulture)) };
        if (key.MachineId is not null)
        {
            claims.Add(new Claim(MachineClaim, key.MachineId));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
