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
            using var connection = await ExchangeTlsConnection
                .OpenAsync(account.Host, account.Port, account.Pin, cancellationToken, account.PinKey)
                .ConfigureAwait(false);
            await ExchangeProtocol.WriteAsync(connection.Stream, new
            {
                schemaVersion = 2, type = "hello", deviceId = account.DeviceId, deviceToken = account.Token,
                productVersion = ProductInfo.Version, keepaliveSeconds = 30
            }, false, cancellationToken).ConfigureAwait(false);
            using var welcome = await ExchangeHandshake.ReadAsync(connection.Stream, "welcome", cancellationToken).ConfigureAwait(false);
            if (ExchangeProtocol.String(welcome.RootElement, "type") == "refused")
            {
                return;
            }

            await ExchangeProtocol.WriteAsync(connection.Stream, new
            {
                schemaVersion = 2, type = "attach", tunnelId = tunnel.TunnelId
            }, true, cancellationToken).ConfigureAwait(false);

            await using var remote = await inner.AcceptAsync(connection.Stream, cancellationToken).ConfigureAwait(false);
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

    private static bool IsConnectionFailure(Exception exception) => exception is IOException or SocketException
        or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException;
}
