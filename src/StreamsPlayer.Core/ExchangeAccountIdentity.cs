using System.Net;

namespace StreamsPlayer.Core;

public static class ExchangeAccountIdentity
{
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
