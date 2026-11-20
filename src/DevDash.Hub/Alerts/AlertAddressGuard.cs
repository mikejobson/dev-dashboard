using System.Net;
using System.Net.Sockets;

namespace DevDash.Hub.Alerts;

/// <summary>
/// Where alerts may not go: link-local addresses (169.254.0.0/16, fe80::/10), which is where cloud metadata
/// services live, and the other well-known metadata addresses. Loopback and private ranges stay allowed:
/// Home Assistant and ntfy usually sit on the LAN or the tailnet. The check runs on the address actually
/// connected to, after DNS, so a name that resolves to a metadata address is refused too.
/// </summary>
public static class AlertAddressGuard
{
    public const string Refusal = "Alerts are never sent to a link-local or cloud metadata address.";

    private static readonly IPAddress[] Metadata =
    [
        IPAddress.Parse("100.100.100.200"), // Alibaba Cloud
        IPAddress.Parse("fd00:ec2::254"),   // AWS, over IPv6
    ];

    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }
        }
        else if (address.IsIPv6LinkLocal)
        {
            return true;
        }

        return Metadata.Any(m => m.Equals(address));
    }

    /// <summary>A refusal for an address whose host is a blocked IP literal, checked before anything is sent. Names are checked when connecting.</summary>
    public static string? WhyRefused(Uri? uri) =>
        uri is not null && IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var ip) && IsBlocked(ip) ? Refusal : null;

    /// <summary>The handler for <see cref="AlertSender"/>: no proxy (the guard must see the real destination), and every connection checked.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        ConnectCallback = ConnectAsync,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <summary>Resolves the host, drops blocked addresses, and connects to what is left; refuses when nothing is.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(a => !IsBlocked(a)).ToArray();
        if (allowed.Length == 0)
        {
            throw new HttpRequestException(Refusal);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
