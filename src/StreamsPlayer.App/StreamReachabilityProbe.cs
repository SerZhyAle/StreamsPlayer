using System.Net.NetworkInformation;
using System.Net.Sockets;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0041: failure-path-only connectivity gate, the sibling of <see cref="PlaybackStatusProbe"/>. Before the
/// bounded recovery ladder is spent on a fresh open failure, it asks the stream's own host and port whether
/// they answer. Not ICMP: CDN and cloud hosts routinely drop pings while serving media, and the endpoint
/// probed here is exactly the one the engine needs, which is what makes a refusal conclusive.
/// </summary>
/// <remarks>
/// Best-effort and total: every error or timeout resolves to a verdict, never to an exception. SP-0168: it
/// contacts the channel's own host and no other - there is no reference host - and only a definite answer (a
/// refusal, a name that does not exist) skips the recovery ladder; a timeout never does. It runs only when a
/// foreground playback has already failed - never on the grid-preview path, never to pre-check or mark
/// channels.
/// </remarks>
internal static class StreamReachabilityProbe
{
    // Bounds the wait for one answer, DNS included; what the timeout means is decided in Core, not here.
    private const int ConnectTimeoutMs = 1_500;

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

            var outcome = await ConnectAsync(endpoint.Host, endpoint.Port, token);
            if (token.IsCancellationRequested)
            {
                return PlaybackReachability.NotProbed; // the caller is tearing down; its own checks stop the flow
            }

            return PlaybackReachabilityRules.Verdict(outcome, endpoint.IsLocal);
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
        {
            // The interface query itself failed: nothing is known, so today's behaviour applies.
            return PlaybackReachability.NotProbed;
        }
    }

    // A DNS failure surfaces as a SocketException too; Core tells a missing name from a transient failure.
    private static async Task<ConnectOutcome> ConnectAsync(string host, int port, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ConnectTimeoutMs);
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return ConnectOutcome.Connected;
        }
        catch (SocketException ex)
        {
            return PlaybackReachabilityRules.OutcomeOf(ex.SocketErrorCode);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ArgumentException)
        {
            return ConnectOutcome.Inconclusive; // the timeout, or an address the socket layer rejects
        }
    }
}
