using System.Net;
using DevDash.Hub;
using DevDash.Hub.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDash.Hub.Tests;

public sealed class TailscaleIdentityTests
{
    private static TailscaleIdentityProvider Provider(string allowed = "")
    {
        var monitor = new StaticMonitor(new AuthOptions { Mode = "Tailscale", AllowedUsers = allowed });
        return new TailscaleIdentityProvider(monitor, NullLogger<TailscaleIdentityProvider>.Instance);
    }

    private static DefaultHttpContext Request(string remote, string? login, string? name = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (login is not null) { context.Request.Headers[TailscaleIdentityProvider.LoginHeader] = login; }
        if (name is not null) { context.Request.Headers[TailscaleIdentityProvider.NameHeader] = name; }
        return context;
    }

    [Fact]
    public void Trusts_identity_headers_from_the_local_serve_proxy()
    {
        var identity = Provider().Resolve(Request("127.0.0.1", "user@example.com", "Example User"));
        Assert.NotNull(identity);
        Assert.Equal("user@example.com", identity.Login);
        Assert.Equal("Example User", identity.DisplayName);
    }

    [Fact]
    public void Ignores_headers_from_a_peer_that_reached_the_port_directly()
    {
        Assert.Null(Provider().Resolve(Request("100.64.1.2", "user@example.com")));
    }

    [Fact]
    public void Refuses_requests_without_a_user_such_as_tagged_devices()
    {
        Assert.Null(Provider().Resolve(Request("127.0.0.1", null)));
    }

    [Theory]
    [InlineData("user@example.com", true)]
    [InlineData("USER@example.com", true)]
    [InlineData("someone@example.com", false)]
    public void Applies_the_allowlist(string login, bool allowed)
    {
        var identity = Provider("user@example.com, other@github").Resolve(Request("::1", login));
        Assert.Equal(allowed, identity is not null);
    }

    private sealed class StaticMonitor(AuthOptions value) : IOptionsMonitor<AuthOptions>
    {
        public AuthOptions CurrentValue => value;
        public AuthOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<AuthOptions, string?> listener) => null;
    }
}
