using System.Net.Sockets;
using System.Text;

namespace StreamsPlayer.Core;

/// <summary>What the proxy needs to open a sealed relay: the broadcast it listens to and the group key that opens its key ring.</summary>
public sealed record ExchangeSealedRelayOptions(string BroadcastId, byte[] GroupKey)
{
    public override string ToString() => $"ExchangeSealedRelayOptions {{ BroadcastId = {BroadcastId} }}";
}

/// <summary>
/// SP-0226 / `DEVICE-EXCHANGE` item DL: a sealed relay played through the proxy. The relay's body is records; the proxy
/// opens them and answers the player with plain media and the true media type, and a record that does not
/// authenticate ends the connection with `sealed-failed` - nothing is played from it. A media record of an epoch this
/// listener holds no key for makes it reconnect once and read the current header chunk.
/// </summary>
public sealed partial class ExchangeRelayProxy
{
    /// <summary>The relay is dialled again once after an epoch it could not open (item DL), so a key rotation that left this listener in costs no more than that.</summary>
    private const int SealedReconnects = 1;

    private async Task<Stream> RunSealedAsync(
        Stream relayStream, byte[] head, byte[] leftover, TcpClient client, ExchangeSealedRelayOptions options,
        CancellationToken cancellationToken)
    {
        var player = client.GetStream();
        var headWritten = false;
        async Task BeginAsync(string mediaType)
        {
            if (headWritten)
            {
                return;
            }

            headWritten = true;
            await WriteClientHeadAsync(client,
                $"HTTP/1.0 200 OK\r\nContent-Type: {mediaType}\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n")
                .ConfigureAwait(false);
        }

        ConnectedCount++;
        for (var attempt = 0; ; attempt++)
        {
            var decoder = new ExchangeSealedRelayDecoder(options.BroadcastId, [options.GroupKey]);
            var status = await ExchangeSealedRelayPump.RunAsync(relayStream, leftover, IsChunked(head), decoder, BeginAsync,
                player, NoteActivity, cancellationToken).ConfigureAwait(false);
            if (status == ExchangeSealedRelayStatus.Ok)
            {
                return relayStream;
            }

            if (status == ExchangeSealedRelayStatus.EpochNotHeld && attempt < SealedReconnects)
            {
                var again = await ReconnectRelayAsync(relayStream, cancellationToken).ConfigureAwait(false);
                if (again is not null)
                {
                    (relayStream, head, leftover) = again.Value;
                    continue;
                }
            }

            LastFailure = ExchangeSealedState.SealedFailed.Code();
            Failed?.Invoke(LastFailure);
            if (!headWritten)
            {
                await WriteClientHeadAsync(client, "HTTP/1.0 502 Bad Gateway\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
            }

            return relayStream;
        }
    }

    // The same request on a fresh connection, answered 2xx; null when the relay no longer lets this listener in.
    private async Task<(Stream Stream, byte[] Head, byte[] Leftover)?> ReconnectRelayAsync(Stream old, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _activeResources.Remove(old);
        }

        await old.DisposeAsync().ConfigureAwait(false);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(RelayHeadTimeout);
        var stream = await _streamConnector(_relayUrl.Host, _relayUrl.Port, _certFingerprint, connect.Token).ConfigureAwait(false);
        lock (_lock)
        {
            if (_disposed)
            {
                stream.Dispose();
                return null;
            }

            _activeResources.Add(stream);
        }

        await stream.WriteAsync(BuildRelayRequest(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        var (head, leftover) = await ReadRelayHeadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (RelayStatus(head) is not (>= 200 and < 300))
        {
            return null;
        }

        return (stream, head, leftover);
    }

    private static bool IsChunked(byte[] head) =>
        Encoding.ASCII.GetString(head).Split("\r\n").Any(line =>
            line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
            && line.Contains("chunked", StringComparison.OrdinalIgnoreCase));
}
