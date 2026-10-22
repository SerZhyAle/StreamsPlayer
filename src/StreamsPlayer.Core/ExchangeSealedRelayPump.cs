using System.Globalization;

namespace StreamsPlayer.Core;

/// <summary>
/// An incremental HTTP/1.1 chunked-body reader (RFC 9112 section 7.1): bytes of the wire in, the chunks' data out,
/// in whatever pieces the wire delivers. The relay answers its listeners chunked (`DEVICE-EXCHANGE` 7.6), and a
/// sealed relay's records are read from inside that body by their own length, so the framing is stripped first.
/// Chunk extensions and trailers are skipped; a malformed size line ends the body as an error.
/// </summary>
internal sealed class ExchangeChunkedBodyDecoder
{
    private const int MaximumSizeLine = 64;
    private const long MaximumChunk = 16 * 1024 * 1024;

    private enum Phase { SizeLine, Data, DataEnd, Trailer, Done }

    private readonly List<byte> _line = [];
    private Phase _phase = Phase.SizeLine;
    private long _remaining;

    public bool Done => _phase == Phase.Done;

    /// <summary>Feeds wire bytes; the data bytes found are appended to <paramref name="data"/>. False when the framing is malformed.</summary>
    public bool Feed(ReadOnlySpan<byte> wire, List<byte[]> data)
    {
        var index = 0;
        while (index < wire.Length && _phase != Phase.Done)
        {
            switch (_phase)
            {
                case Phase.SizeLine:
                    if (!ReadLine(wire, ref index, out var line))
                    {
                        break;
                    }

                    var text = System.Text.Encoding.ASCII.GetString(line.ToArray());
                    var semicolon = text.IndexOf(';');
                    if (!long.TryParse(semicolon < 0 ? text.Trim() : text[..semicolon].Trim(), NumberStyles.AllowHexSpecifier,
                            CultureInfo.InvariantCulture, out var size) || size < 0 || size > MaximumChunk)
                    {
                        return false;
                    }

                    _remaining = size;
                    _phase = size == 0 ? Phase.Trailer : Phase.Data;
                    break;
                case Phase.Data:
                    var take = (int)Math.Min(_remaining, wire.Length - index);
                    data.Add(wire.Slice(index, take).ToArray());
                    index += take;
                    _remaining -= take;
                    if (_remaining == 0)
                    {
                        _phase = Phase.DataEnd;
                    }

                    break;
                case Phase.DataEnd:
                    if (!ReadLine(wire, ref index, out var end))
                    {
                        break;
                    }

                    if (end.Count != 0)
                    {
                        return false;
                    }

                    _phase = Phase.SizeLine;
                    break;
                case Phase.Trailer:
                    // Trailer lines run to an empty line; their content is of no use to a media body.
                    if (!ReadLine(wire, ref index, out var trailer))
                    {
                        break;
                    }

                    if (trailer.Count == 0)
                    {
                        _phase = Phase.Done;
                    }

                    break;
            }
        }

        return true;
    }

    // Collects one line into a small buffer across calls; true when a line ending was consumed.
    private bool ReadLine(ReadOnlySpan<byte> wire, ref int index, out List<byte> line)
    {
        line = _line;
        while (index < wire.Length)
        {
            var value = wire[index++];
            if (value == (byte)'\n')
            {
                if (_line.Count > 0 && _line[^1] == (byte)'\r')
                {
                    _line.RemoveAt(_line.Count - 1);
                }

                line = [.. _line];
                _line.Clear();
                return true;
            }

            if (_line.Count >= MaximumSizeLine)
            {
                _line.Clear();
                _phase = Phase.Done;
                throw new InvalidDataException("A chunked line is longer than any size line.");
            }

            _line.Add(value);
        }

        return false;
    }
}

/// <summary>
/// The listener's sealed-relay loop: reads the relay's body, strips the chunked framing when there is one, feeds the
/// records to the decoder and writes only authenticated clear media to the player. The player's response head is
/// written when the first clear byte is ready, because the true media type travels inside the key ring.
/// </summary>
internal static class ExchangeSealedRelayPump
{
    /// <summary>
    /// Runs until the relay ends (<see cref="ExchangeSealedRelayStatus.Ok"/>), a record names an epoch this listener holds
    /// no key for, or a record fails. Nothing unauthenticated is ever written to <paramref name="player"/>.
    /// </summary>
    internal static async Task<ExchangeSealedRelayStatus> RunAsync(
        Stream relay, byte[] leftover, bool chunked, ExchangeSealedRelayDecoder decoder,
        Func<string, Task> beginPlayerResponse, Stream player, Action noteActivity, CancellationToken cancellationToken)
    {
        var chunkedBody = chunked ? new ExchangeChunkedBodyDecoder() : null;
        var buffer = new byte[16384];
        var segments = new List<byte[]>();
        var clear = new List<byte[]>();

        async Task<ExchangeSealedRelayStatus> HandleAsync(ReadOnlyMemory<byte> wire)
        {
            segments.Clear();
            if (chunkedBody is null)
            {
                segments.Add(wire.ToArray());
            }
            else
            {
                try
                {
                    if (!chunkedBody.Feed(wire.Span, segments))
                    {
                        return ExchangeSealedRelayStatus.Failed;
                    }
                }
                catch (InvalidDataException)
                {
                    return ExchangeSealedRelayStatus.Failed;
                }
            }

            foreach (var segment in segments)
            {
                clear.Clear();
                var status = decoder.Feed(segment, clear);
                if (clear.Count > 0 && decoder.MediaType is { } mediaType)
                {
                    await beginPlayerResponse(mediaType).ConfigureAwait(false);
                    foreach (var media in clear)
                    {
                        await player.WriteAsync(media, cancellationToken).ConfigureAwait(false);
                    }

                    await player.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (status != ExchangeSealedRelayStatus.Ok)
                {
                    return status;
                }
            }

            return ExchangeSealedRelayStatus.Ok;
        }

        if (leftover.Length > 0)
        {
            var first = await HandleAsync(leftover).ConfigureAwait(false);
            if (first != ExchangeSealedRelayStatus.Ok)
            {
                return first;
            }
        }

        while (!cancellationToken.IsCancellationRequested && chunkedBody is not { Done: true })
        {
            var read = await relay.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return ExchangeSealedRelayStatus.Ok; // the broadcast ended
            }

            noteActivity();
            var status = await HandleAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            if (status != ExchangeSealedRelayStatus.Ok)
            {
                return status;
            }
        }

        return ExchangeSealedRelayStatus.Ok;
    }
}
