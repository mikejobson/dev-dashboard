using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Auth;

public sealed record BrowserIdentity(string Login, string? DisplayName = null);

/// <summary>
/// Works out who is behind a browser request. This is the seam for phase 4, where an implementation
/// reads <c>Tailscale-User-Login</c> from requests that arrive through the <c>tailscale serve</c> proxy
/// and checks it against an allowlist.
/// </summary>
public interface IBrowserIdentityProvider
{
    /// <summary>Returns the user, or null when the request must be refused.</summary>
    BrowserIdentity? Resolve(HttpContext context);
}

/// <summary>Local development: anyone on this machine is the single local user.</summary>
public sealed class LocalDevIdentityProvider(IOptionsMonitor<AuthOptions> options) : IBrowserIdentityProvider
{
    public BrowserIdentity? Resolve(HttpContext context)
    {
        var auth = options.CurrentValue;
        if (auth.RequireLoopback)
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !IPAddress.IsLoopback(remote))
            {
                return null;
            }
        }

        return new BrowserIdentity(auth.LocalUser, "Local user");
    }
}

/// <summary>
/// Behind <c>tailscale serve</c>: the proxy runs in the same network namespace and connects over loopback,
/// adding <c>Tailscale-User-Login</c> / <c>Tailscale-User-Name</c> for requests from user-owned devices.
/// Tagged devices get no identity headers and are refused (agents use API keys).
/// <para>
/// Headers are trusted only on a loopback connection to <see cref="AuthOptions.ServeAddress"/> (127.0.0.2),
/// the address serve proxies to. Loopback alone is not enough: in userspace mode tailscaled forwards a
/// tailnet peer's direct connection to the node's IP to 127.0.0.1 on the same port, so a check for
/// "came from loopback" let any device on the tailnet name itself in the header.
/// </para>
/// </summary>
public sealed class TailscaleIdentityProvider(IOptionsMonitor<AuthOptions> options, ILogger<TailscaleIdentityProvider> logger) : IBrowserIdentityProvider
{
    public const string LoginHeader = "Tailscale-User-Login";
    public const string NameHeader = "Tailscale-User-Name";

    public BrowserIdentity? Resolve(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote) || !ArrivedAtServeAddress(context, options.CurrentValue))
        {
            return null;
        }

        var login = context.Request.Headers[LoginHeader].ToString().Trim();
        if (login.Length == 0)
        {
            return null;
        }

        var allowed = options.CurrentValue.AllowedUserSet;
        if (allowed.Count > 0 && !allowed.Contains(login))
        {
            logger.LogWarning("Refused tailnet user {Login}: not in Auth:AllowedUsers.", login);
            return null;
        }

        var name = context.Request.Headers[NameHeader].ToString().Trim();
        return new BrowserIdentity(login, name.Length > 0 ? name : null);
    }

    private static bool ArrivedAtServeAddress(HttpContext context, AuthOptions auth)
    {
        var local = context.Connection.LocalIpAddress;
        if (local is null || !IPAddress.TryParse(auth.ServeAddress, out var expected))
        {
            return false;
        }

        return (local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local).Equals(expected);
    }
}

public sealed class BrowserAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IBrowserIdentityProvider identities) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Browser";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = identities.Resolve(Context);
        if (identity is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.Login),
            new(ClaimTypes.Name, identity.DisplayName ?? identity.Login),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
