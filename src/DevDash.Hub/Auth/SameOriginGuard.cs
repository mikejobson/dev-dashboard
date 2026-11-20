namespace DevDash.Hub.Auth;

/// <summary>
/// Refuses requests a browser sends on behalf of another site. Sign-in comes from the network
/// (<c>tailscale serve</c> names the user on every request from their device), not from a cookie, so
/// SameSite rules do not help: any page a tailnet user opens could otherwise POST to the hub's API, or open
/// the Blazor connection, as them. Browsers mark such requests with <c>Origin</c> and <c>Sec-Fetch-Site</c>;
/// agents, MCP clients and curl send neither, so they are unaffected.
/// </summary>
public static class SameOriginGuard
{
    public static IApplicationBuilder UseSameOriginGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (IsCrossSite(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Cross-site request refused.");
                return;
            }

            await next(context);
        });

    /// <summary>
    /// A plain navigation (a link from GitHub or a notification) is fine; anything else from another site is not:
    /// a non-GET request, a WebSocket upgrade, or a script's fetch.
    /// </summary>
    public static bool IsCrossSite(HttpRequest request)
    {
        var safeNavigation = (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && !request.Headers.Upgrade.Any(v => string.Equals(v, "websocket", StringComparison.OrdinalIgnoreCase))
            && !string.Equals(request.Headers["Sec-Fetch-Mode"].ToString(), "cors", StringComparison.OrdinalIgnoreCase);
        if (safeNavigation)
        {
            return false;
        }

        // Every current browser sends Sec-Fetch-Site, and it is the browser's own verdict: use it when present.
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0)
        {
            return !site.Equals("same-origin", StringComparison.OrdinalIgnoreCase) && !site.Equals("none", StringComparison.OrdinalIgnoreCase);
        }

        // Older browsers: compare Origin with the host the request was sent to.

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return false;
        }

        return !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
