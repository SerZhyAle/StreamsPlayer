using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>What one adapter of this PC looks like to the probes: read once per diagnosis, never changed.</summary>
internal sealed record ExchangeAdapterFact(string Name, string Description, NetworkInterfaceType Type, bool IsUp,
    IReadOnlyList<(IPAddress Address, IPAddress? Mask)> Addresses);

/// <summary>
/// SP-0225: the checks this PC can make itself for the ladder of <c>DEVICE-EXCHANGE</c> item CY. Every probe is
/// read-only - one bounded TCP connection to the address the person already uses, a name lookup, and a look at this
/// PC's own adapters; no setting is changed and nothing is sent to any other address. An instance serves one
/// diagnosis, so the connection is made once and its result shared by the steps that read it.
/// </summary>
internal sealed partial class ExchangeDiagnosisProbes(Func<IReadOnlyList<ExchangeAdapterFact>>? adapters = null,
    TimeSpan? connectBudget = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) : IExchangeDiagnosisProbes
{
    internal enum Reach
    {
        /// <summary>Connected: something is listening there.</summary>
        Connected,

        /// <summary>The machine answered and said nothing listens on that port: it is awake, the address or port is off.</summary>
        Refused,

        /// <summary>No answer within the budget, or no route: the other end is off, asleep or out of reach.</summary>
        Silent,

        /// <summary>The name does not resolve from this PC.</summary>
        NameNotResolved
    }

    // Kinds of adapter a person calls a VPN. Teredo, ISATAP and 6to4 are Windows' own IPv6 transition tunnels and
    // are up on a PC with no VPN at all, so a bare "tun" match (the usual shortcut) would blame every machine.
    [GeneratedRegex(@"\b(?:vpn|wireguard|wintun|openvpn|tap-windows|tailscale|zerotier|nordlynx|anyconnect|globalprotect|forticlient|proton)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex VpnNames();

    // Windows reports a refused connection to a closed port only after it has retried the SYN, about two seconds, so a
    // shorter budget would read an awake machine with nothing listening as a machine that does not answer.
    private static readonly TimeSpan DefaultConnectBudget = TimeSpan.FromSeconds(4);
    private const int MaximumAddressesTried = 2;
    private readonly Func<IReadOnlyList<ExchangeAdapterFact>> _adapters = adapters ?? ReadAdapters;
    private readonly TimeSpan _connectBudget = connectBudget ?? DefaultConnectBudget;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve =
        resolve ?? ((name, token) => Dns.GetHostAddressesAsync(name, token));
    private Task<(Reach Reach, IReadOnlyList<IPAddress> Addresses)>? _reach;
    private IReadOnlyList<ExchangeAdapterFact>? _adapterFacts;

    public async Task<ExchangeProbeOutcome> ProbeAsync(ExchangeDiagnosisStep step, ExchangeDiagnosisContext context,
        CancellationToken cancellationToken)
    {
        switch (step)
        {
            case ExchangeDiagnosisStep.Vpn:
                return Facts().Any(fact => fact.IsUp && LooksLikeVpn(fact)) ? ExchangeProbeOutcome.Failed : ExchangeProbeOutcome.Passed;
            case ExchangeDiagnosisStep.PcAsleep or ExchangeDiagnosisStep.AddressLan or ExchangeDiagnosisStep.IpFamily
                or ExchangeDiagnosisStep.ServerInternet or ExchangeDiagnosisStep.PortForward:
                var (reach, addresses) = await ReachAsync(context, cancellationToken).ConfigureAwait(false);
                return Judge(step, context, reach, addresses);
            default:
                // The router's guest network, the server computer's Windows profile and its clock are not visible
                // from here: they are offered to the person, not asserted (item CY).
                return ExchangeProbeOutcome.CouldNotCheck;
        }
    }

    private ExchangeProbeOutcome Judge(ExchangeDiagnosisStep step, ExchangeDiagnosisContext context, Reach reach,
        IReadOnlyList<IPAddress> addresses)
    {
        switch (step)
        {
            case ExchangeDiagnosisStep.PcAsleep:
                // A refusal is an answer: the machine is awake. Only silence is a machine that is off or out of reach.
                return reach switch
                {
                    Reach.Silent => ExchangeProbeOutcome.Failed,
                    Reach.NameNotResolved => ExchangeProbeOutcome.CouldNotCheck,
                    _ => ExchangeProbeOutcome.Passed
                };
            case ExchangeDiagnosisStep.AddressLan:
                return reach is Reach.NameNotResolved or Reach.Refused || NoAdapterSharesTheNetwork(context.Host, reach, addresses)
                    ? ExchangeProbeOutcome.Failed : ExchangeProbeOutcome.Passed;
            case ExchangeDiagnosisStep.IpFamily:
                return addresses.Count == 0
                    ? ExchangeProbeOutcome.CouldNotCheck
                    : addresses.Any(address => HasAddressOfFamily(Facts(), address.AddressFamily))
                        ? ExchangeProbeOutcome.Passed : ExchangeProbeOutcome.Failed;
            case ExchangeDiagnosisStep.ServerInternet:
                return reach == Reach.NameNotResolved ? ExchangeProbeOutcome.Failed : ExchangeProbeOutcome.Passed;
            case ExchangeDiagnosisStep.PortForward:
                return reach == Reach.Refused ? ExchangeProbeOutcome.Failed : ExchangeProbeOutcome.Passed;
            default:
                return ExchangeProbeOutcome.CouldNotCheck;
        }
    }

    private bool NoAdapterSharesTheNetwork(string host, Reach reach, IReadOnlyList<IPAddress> addresses)
    {
        // Only a private IPv4 literal can be tested against the adapters' subnets; a name or a public address
        // says nothing about the local network, and a connection that worked proves the path.
        if (reach == Reach.Connected || !IPAddress.TryParse(host.Trim().Trim('[', ']'), out var literal)
            || literal.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(literal)
            || ExchangeDiagnosisRoute.Of(host) != ExchangeDiagnosisApplies.Network)
        {
            return false;
        }

        return !SharesSubnetWithAny(literal, Facts());
    }

    private Task<(Reach Reach, IReadOnlyList<IPAddress> Addresses)> ReachAsync(ExchangeDiagnosisContext context,
        CancellationToken cancellationToken) => _reach ??= MeasureAsync(context.Host, context.Port, cancellationToken);

    private async Task<(Reach Reach, IReadOnlyList<IPAddress> Addresses)> MeasureAsync(string host, int port,
        CancellationToken cancellationToken)
    {
        var clean = host.Trim().Trim('[', ']').TrimEnd('.');
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(clean, out var literal)
                ? [literal]
                : await _resolve(clean, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return (Reach.NameNotResolved, []);
        }
        catch (ArgumentException)
        {
            return (Reach.NameNotResolved, []);
        }

        if (addresses.Length == 0)
        {
            return (Reach.NameNotResolved, []);
        }

        var refused = false;
        foreach (var address in addresses.Take(MaximumAddressesTried))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_connectBudget);
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), budget.Token).ConfigureAwait(false);
                return (Reach.Connected, addresses);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
            {
                refused = true;
            }
            catch (SocketException)
            {
                // Unreachable network, host down, reset: the same silence for this address.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The budget ran out: nothing answered.
            }
        }

        return (refused ? Reach.Refused : Reach.Silent, addresses);
    }

    private IReadOnlyList<ExchangeAdapterFact> Facts() => _adapterFacts ??= _adapters();

    internal static bool LooksLikeVpn(ExchangeAdapterFact fact) =>
        fact.Type == NetworkInterfaceType.Ppp || VpnNames().IsMatch(fact.Name) || VpnNames().IsMatch(fact.Description);

    internal static bool SharesSubnet(IPAddress host, IPAddress local, IPAddress mask)
    {
        var h = host.GetAddressBytes();
        var l = local.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (h.Length != 4 || l.Length != 4 || m.Length != 4)
        {
            return false;
        }

        for (var index = 0; index < 4; index++)
        {
            if ((h[index] & m[index]) != (l[index] & m[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SharesSubnetWithAny(IPAddress host, IReadOnlyList<ExchangeAdapterFact> facts) =>
        facts.Where(fact => fact.IsUp).SelectMany(fact => fact.Addresses).Any(item =>
            item.Address.AddressFamily == AddressFamily.InterNetwork && item.Mask is { } mask
            && SharesSubnet(host, item.Address, mask));

    /// <summary>
    /// Whether this PC has an address of the family that can reach beyond its own link: a link-local address
    /// (169.254/16, fe80::/10) exists on every adapter and reaches nothing, so it does not count.
    /// </summary>
    internal static bool HasAddressOfFamily(IReadOnlyList<ExchangeAdapterFact> facts, AddressFamily family) =>
        facts.Where(fact => fact.IsUp).SelectMany(fact => fact.Addresses).Any(item =>
            item.Address.AddressFamily == family && !IPAddress.IsLoopback(item.Address)
            && !(family == AddressFamily.InterNetworkV6 ? item.Address.IsIPv6LinkLocal : IsIPv4LinkLocal(item.Address)));

    private static bool IsIPv4LinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static IReadOnlyList<ExchangeAdapterFact> ReadAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Select(adapter =>
            {
                var addresses = new List<(IPAddress, IPAddress?)>();
                try
                {
                    foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                    {
                        addresses.Add((unicast.Address, unicast.Address.AddressFamily == AddressFamily.InterNetwork
                            ? unicast.IPv4Mask : null));
                    }
                }
                catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
                {
                    // An adapter that cannot be read counts as one with no address.
                }

                return new ExchangeAdapterFact(adapter.Name, adapter.Description, adapter.NetworkInterfaceType,
                    adapter.OperationalStatus == OperationalStatus.Up, addresses);
            }).ToArray();
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            return [];
        }
    }
}
