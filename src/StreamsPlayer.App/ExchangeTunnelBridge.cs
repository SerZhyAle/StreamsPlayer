using System.IO;
using System.Net;
using System.Net.Sockets;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

/// <summary>
/// SP-0217 / `DEVICE-EXCHANGE` 7.5, the producer's side of one tunnel: on `open` the producer opens its
/// own TLS data stream to the account's server, sends `hello` then `attach {tunnelId}`, and bridges the
/// server's stream to the broadcast's own local service - the loopback half of the LAN share, which is
/// the one address a tunnel may always reach (constraint 4: from this machine only, unless the LAN
/// opt-in is on). From the first attached byte the server interprets nothing, and neither does this:
/// a two-way byte pump until either side closes.
/// </summary>
internal static class ExchangeTunnelBridge
{
    /// <summary>
    /// Item L: a bridge that moves no byte in either direction for ten minutes is ended, so a dead peer cannot hold one
    /// forever. The clock restarts with every chunk - a stream that keeps playing is never cut at a fixed age.
    /// </summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// S7-1: the whole set-up of one tunnel - the data stream, hello, welcome and the inner TLS accept - gets the same fifteen
    /// seconds every other exchange connection gets, so a stalled server or a listener that never finishes the handshake
    /// cannot hold one of the sixteen per-broadcast slots until the control session ends. The pump that follows is not bound by it.
    /// </summary>
    internal static readonly TimeSpan SetupDeadline = TimeSpan.FromSeconds(15);

    /// <summary>
    /// SP-0226 / item DM: the attached stream is answered with a TLS server (<paramref name="inner"/>, the certificate made
    /// for this broadcast) before anything is bridged, so what the exchange server relays is TLS records and nothing
    /// else. A listener that does not accept the leaf hangs up, and the tunnel simply ends.
    /// </summary>
    internal static async Task RunAsync(ExchangeSourceService exchange, ExchangeTunnelOpen tunnel, int localPort,
        CancellationToken cancellationToken, ExchangeTunnelCertificate inner)
    {
        try
        {
            var account = exchange.Account;
            // S7-1: set-up is bound by its own deadline on top of the session token; the pump below gets the session token only.
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            setup.CancelAfter(SetupDeadline);
            using var connection = await ExchangeTlsConnection
                .OpenAsync(account.Host, account.Port, account.Pin, setup.Token, account.PinKey)
                .ConfigureAwait(false);
            await ExchangeProtocol.WriteAsync(connection.Stream, new
            {
                schemaVersion = 2, type = "hello", deviceId = account.DeviceId, deviceToken = account.Token,
                productVersion = ProductInfo.Version, keepaliveSeconds = 30
            }, false, setup.Token).ConfigureAwait(false);
            using var welcome = await ExchangeHandshake.ReadAsync(connection.Stream, "welcome", setup.Token).ConfigureAwait(false);
            if (ExchangeProtocol.String(welcome.RootElement, "type") == "refused")
            {
                return;
            }

            await ExchangeProtocol.WriteAsync(connection.Stream, new
            {
                schemaVersion = 2, type = "attach", tunnelId = tunnel.TunnelId
            }, true, setup.Token).ConfigureAwait(false);

            await using var remote = await inner.AcceptAsync(connection.Stream, setup.Token).ConfigureAwait(false);
            setup.CancelAfter(Timeout.InfiniteTimeSpan); // set-up is done; the idle timeout owns the bridge from here
            using var local = new TcpClient();
            await local.ConnectAsync(IPAddress.Loopback, localPort, cancellationToken).ConfigureAwait(false);
            await PumpAsync(remote, local.GetStream(), IdleTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            // A tunnel that cannot be bridged ends; the listener that asked for it is answered by the
            // server's own window. Nothing is retried in the background.
        }
    }

    internal static async Task PumpAsync(Stream remote, Stream local, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(idleTimeout);
        // CancelAfter on a source that has not fired replaces the pending deadline, so a chunk in either direction pushes it out.
        void Moved() => idle.CancelAfter(idleTimeout);
        // Either direction ending is the tunnel's end; the other copy finishes with it.
        await Task.WhenAll(CopyToAsync(remote, local, Moved, idle.Token),
            CopyToAsync(local, remote, Moved, idle.Token)).ConfigureAwait(false);
    }

    private static async Task CopyToAsync(Stream from, Stream to, Action moved, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                moved();
            }

            to.Close();
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            // Either side closing is the tunnel's own end, never a fault to report.
        }
    }

    // S7-2: the exchange loop's own classification, so a malformed welcome (InvalidDataException, JsonException from
    // ExchangeHandshake.ReadAsync) ends this fire-and-forget bridge like any other dead connection instead of faulting unobserved.
    private static bool IsConnectionFailure(Exception exception) => ExchangeSourceService.IsConnectionFailure(exception);
}
