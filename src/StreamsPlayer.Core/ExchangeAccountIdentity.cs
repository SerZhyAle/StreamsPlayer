using System.Net;

namespace StreamsPlayer.Core;

public static class ExchangeAccountIdentity
{
    /// <summary>AA's stable entry identity, independent of a device id or token recreated after removal.</summary>
    public static string Key(string host, int port, string login) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            NormalizeHost(host) + "\n" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + login.ToLowerInvariant())));

    public static string NormalizeHost(string host) => IPAddress.TryParse(host.Trim('[', ']'), out var address)
        ? address.ToString().ToLowerInvariant()
        : host.ToLowerInvariant();

    public static bool SameEndpoint(string firstHost, int firstPort, string secondHost, int secondPort) =>
        firstPort == secondPort && NormalizeHost(firstHost) == NormalizeHost(secondHost);

    public static bool SameAccount(string firstHost, int firstPort, string firstLogin,
        string secondHost, int secondPort, string secondLogin) =>
        SameEndpoint(firstHost, firstPort, secondHost, secondPort)
        && string.Equals(firstLogin, secondLogin, StringComparison.OrdinalIgnoreCase);
}
