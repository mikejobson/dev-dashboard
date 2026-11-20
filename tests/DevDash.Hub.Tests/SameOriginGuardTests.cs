using DevDash.Hub.Auth;
using Microsoft.AspNetCore.Http;

namespace DevDash.Hub.Tests;

public sealed class SameOriginGuardTests
{
    private static HttpRequest Request(string method, string? site = null, string? origin = null, string? mode = null, bool websocket = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Host = new HostString("devdash.example.ts.net");
        if (site is not null) { context.Request.Headers["Sec-Fetch-Site"] = site; }
        if (origin is not null) { context.Request.Headers.Origin = origin; }
        if (mode is not null) { context.Request.Headers["Sec-Fetch-Mode"] = mode; }
        if (websocket) { context.Request.Headers.Upgrade = "websocket"; }
        return context.Request;
    }

    [Fact]
    public void Refuses_what_another_site_sends_on_a_users_behalf()
    {
        Assert.True(SameOriginGuard.IsCrossSite(Request("POST", site: "cross-site", origin: "https://evil.example")));
        Assert.True(SameOriginGuard.IsCrossSite(Request("POST", site: "same-site", origin: "https://other.example.ts.net")));
        Assert.True(SameOriginGuard.IsCrossSite(Request("GET", site: "cross-site", origin: "https://evil.example", websocket: true)));
        Assert.True(SameOriginGuard.IsCrossSite(Request("GET", site: "cross-site", mode: "cors")));
        Assert.True(SameOriginGuard.IsCrossSite(Request("POST", origin: "https://evil.example")));     // an older browser: Origin only
    }

    [Fact]
    public void Allows_the_dashboard_itself_links_from_elsewhere_and_non_browser_clients()
    {
        Assert.False(SameOriginGuard.IsCrossSite(Request("POST", site: "same-origin", origin: "https://devdash.example.ts.net")));
        Assert.False(SameOriginGuard.IsCrossSite(Request("GET", site: "same-origin", websocket: true)));
        Assert.False(SameOriginGuard.IsCrossSite(Request("GET", site: "cross-site", mode: "navigate")));  // a link from GitHub or an alert
        Assert.False(SameOriginGuard.IsCrossSite(Request("POST")));                                        // agents, MCP clients, curl
        Assert.False(SameOriginGuard.IsCrossSite(Request("POST", origin: "https://devdash.example.ts.net")));
    }
}

public sealed class SafeLinkTests
{
    [Theory]
    [InlineData("https://app.t3.codes/threads/abc", "https://app.t3.codes/threads/abc")]
    [InlineData("http://localhost:3773/t/1", "http://localhost:3773/t/1")]
    [InlineData("javascript:alert(document.domain)", null)]
    [InlineData(" JavaScript:alert(1)", null)]
    [InlineData("data:text/html,<script>alert(1)</script>", null)]
    [InlineData("vbscript:msgbox", null)]
    [InlineData("//evil.example/x", null)]
    [InlineData("repo?key=x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_http_and_https_links_reach_an_href(string? url, string? expected) =>
        Assert.Equal(expected, Fmt.SafeLink(url));
}
