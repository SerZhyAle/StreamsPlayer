using System.Net;
using System.Net.Sockets;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0041: the host and port a channel's media engine has to connect to, and whether that host is on the
/// user's own network. It is what the failure-path reachability probe needs from an address - and no more.
/// </summary>
/// <param name="Host">The host name or IP literal, without IPv6 brackets.</param>
/// <param name="Port">The explicit port, or the scheme's well-known one.</param>
/// <param name="IsLocal">
/// True for an address the internet has nothing to do with - loopback, a private, shared (100.64.0.0/10) or
/// link-local range, or a
/// short or local-suffixed host name. The recovery gate never network-checks such a host, and never blames
/// it when it does not answer (SP-0041 Decision 5): a camera that is switched off is not a broken channel.
/// </param>
public sealed record StreamEndpoint(string Host, int Port, bool IsLocal);

/// <summary>SP-0041: reads a <see cref="StreamEndpoint"/> from a channel address, or nothing.</summary>
public static class StreamEndpointResolver
{
    private const int HttpPort = 80;
    private const int HttpsPort = 443;
    private const int RtspPort = 554;

    private static readonly string[] LocalSuffixes = [".local", ".lan", ".home", ".internal"];

    /// <summary>
    /// The endpoint of a launchable address (<see cref="LaunchableAddress"/>), or null for anything else - an
    /// address that is never handed to an engine is never probed either, and no port is ever guessed.
    /// </summary>
    public static StreamEndpoint? TryResolve(string? url)
    {
        if (!LaunchableAddress.TryParse(url, out var address))
        {
            return null;
        }

        var port = address.Port > 0 ? address.Port : DefaultPort(address.Scheme);
        if (port is null)
        {
            return null;
        }

        var host = address.Host.Trim('[', ']');
        return new StreamEndpoint(host, port.Value, IsLocalHost(host));
    }

    /// <summary>
    /// Whether a host is on the user's own network rather than the internet. A literal is judged by its
    /// range; a name by its shape, because resolving it here would be the network round trip this exists
    /// to decide about.
    /// </summary>
    public static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            return IsLocalAddress(ip);
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A dotless name is a NetBIOS / mDNS short name - never a public host.
        var trimmed = host.TrimEnd('.');
        return !trimmed.Contains('.') ||
               LocalSuffixes.Any(suffix => trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLocalAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254) ||
                   // SP-0168: 100.64.0.0/10 - carrier-grade NAT and overlay networks such as Tailscale.
                   (b[0] == 100 && (b[1] & 0xC0) == 0x40);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 unique-local, fe80::/10 link-local.
            return ip.IsIPv6LinkLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    private static int? DefaultPort(string scheme) => scheme switch
    {
        "http" => HttpPort,
        "https" => HttpsPort,
        "rtsp" => RtspPort,
        _ => null
    };
}
