using System.Net.NetworkInformation;
using System.Net.Sockets;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0041: failure-path-only connectivity gate, the sibling of <see cref="PlaybackStatusProbe"/>. Before the
/// bounded recovery ladder is spent on a fresh open failure, it establishes what is unreachable - the
/// channel's host, or the network itself - by connecting to the stream's own host and port. Not ICMP: CDN
/// and cloud hosts routinely drop pings while serving media, and the endpoint probed here is exactly the one
/// the engine needs, which is what makes a refusal conclusive.
/// </summary>
/// <remarks>
/// Best-effort and total: every error or timeout resolves to a verdict, never to an exception. The only
/// second host it ever contacts is the catalog host the app already downloads from, so it adds no outbound
/// destination and changes no privacy claim. It runs only when a foreground playback has already failed -
/// never on the grid-preview path, never to pre-check or mark channels.
/// </remarks>
internal static class StreamReachabilityProbe
{
    // One timeout for both connects, so the worst branch - a dead host, then a network check - is bounded
    // at about three seconds, against the seconds of blind retrying it replaces.
    private const int ConnectTimeoutMs = 1_500;
    private const int CatalogPort = 443;

    // Derived, never a literal: it cannot drift from the catalog address, and it is a host the app already
    // contacts on explicit refresh. Read through the one address parser the App may use (SP-0124).
    private static readonly string CatalogHost = LaunchableAddress.HostOf(StreamCatalogService.CatalogUrl);

    public static async Task<PlaybackReachability> ProbeAsync(string url, CancellationToken token)
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return PlaybackReachability.NetworkUnreachable; // no interface up: no socket is opened at all
            }

            var endpoint = StreamEndpointResolver.TryResolve(url);
            if (endpoint is null)
            {
                return PlaybackReachability.NotProbed;
            }

            if (await CanConnectAsync(endpoint.Host, endpoint.Port, token))
            {
                return PlaybackReachability.HostReachable;
            }

            if (token.IsCancellationRequested)
            {
                return PlaybackReachability.NotProbed; // the caller is tearing down; its own checks stop the flow
            }

            if (endpoint.IsLocal)
            {
                // Decision 5: the internet is irrelevant to a camera on the LAN, so it is not asked - and a
                // local host that does not answer is "not reached", never "broken".
                return PlaybackReachability.NetworkUnreachable;
            }

            var networkWorks = await CanConnectAsync(CatalogHost, CatalogPort, token);
            if (token.IsCancellationRequested)
            {
                return PlaybackReachability.NotProbed;
            }

            return networkWorks ? PlaybackReachability.ChannelUnreachable : PlaybackReachability.NetworkUnreachable;
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
        {
            // The interface query itself failed: nothing is known, so today's behaviour applies.
            return PlaybackReachability.NotProbed;
        }
    }

    // A DNS failure surfaces as a SocketException too, which is what makes a dead host name a channel fault
    // when the network is otherwise healthy.
    private static async Task<bool> CanConnectAsync(string host, int port, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ConnectTimeoutMs);
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }
}
