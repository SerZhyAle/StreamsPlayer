using System.Net;
using System.Net.Sockets;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0225: whether the target of a connection is on this PC's own network or reached from outside - the
/// "Applies" column of <c>DEVICE-EXCHANGE</c> item CY. Decided from the written address alone; nothing is sent.
/// </summary>
public static class ExchangeDiagnosisRoute
{
    private static readonly string[] LanSuffixes = [".local", ".lan", ".home", ".internal", ".home.arpa"];

    /// <summary><see cref="ExchangeDiagnosisApplies.Network"/> or <see cref="ExchangeDiagnosisApplies.Internet"/>.</summary>
    public static ExchangeDiagnosisApplies Of(string? host)
    {
        var clean = Clean(host);
        if (clean.Length == 0 || string.Equals(clean, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return ExchangeDiagnosisApplies.Network;
        }

        if (IPAddress.TryParse(clean, out var address))
        {
            return IsOwnNetwork(address) ? ExchangeDiagnosisApplies.Network : ExchangeDiagnosisApplies.Internet;
        }

        // A name with no dot is a machine name on the local network; the usual home-network suffixes are too.
        if (!clean.Contains('.'))
        {
            return ExchangeDiagnosisApplies.Network;
        }

        return LanSuffixes.Any(suffix => clean.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ? ExchangeDiagnosisApplies.Network
            : ExchangeDiagnosisApplies.Internet;
    }

    /// <summary>
    /// The host and port of an endpoint address, for the connection check of a failed broadcast: the authority only.
    /// The path and the id behind it are the capability to listen (SP-0201) and are neither returned nor kept.
    /// False for an address with no host or no explicit or scheme-default port.
    /// </summary>
    public static bool TryAuthorityOf(string? address, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed) || parsed.Host.Length == 0 || parsed.Port <= 0)
        {
            return false;
        }

        host = parsed.Host;
        port = parsed.Port;
        return true;
    }

    /// <summary>The address family of a literal address; null for a name.</summary>
    public static AddressFamily? FamilyOf(string? host) =>
        IPAddress.TryParse(Clean(host), out var address) ? address.AddressFamily : null;

    private static string Clean(string? host) => (host ?? string.Empty).Trim().Trim('[', ']').TrimEnd('.');

    private static bool IsOwnNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                // Carrier-grade NAT (RFC 6598): behind a provider's NAT, so not this PC's own network, but not the
                // open internet either - the port-forward step cannot help there, the address step can.
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (bytes[0] & 0xFE) == 0xFC);
    }
}
