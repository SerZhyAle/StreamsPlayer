using System.Buffers.Binary;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0217 / `DEVICE-EXCHANGE` 7.6: the chunk framing of a relay upload - a 4-byte unsigned big-endian
/// length counting one flag byte plus the payload, the flag byte, the payload. The 1 MiB bound is on
/// the length field (section 15, item U); a length of zero ends the broadcast cleanly. The flag bits:
/// `0x01` a listener may start here (joinable), `0x02` a header chunk, kept and replayed before a
/// listener's first joinable chunk; other bits zero.
/// </summary>
public static class ExchangeUploadChunks
{
    /// <summary>The flag bit that marks a chunk a listener may start at.</summary>
    public const byte Joinable = 0x01;

    /// <summary>The flag bit that marks a header chunk (PAT and PMT travel together in one, for a video mode).</summary>
    public const byte Header = 0x02;

    /// <summary>Item U: the length field counts the flag byte plus the payload and is at most 1 MiB.</summary>
    public const int MaximumLengthField = 1048576;

    public const int MaximumPayload = MaximumLengthField - 1;

    /// <exception cref="InvalidDataException">A payload that would push the length field past item U's bound.</exception>
    public static async Task WriteAsync(Stream stream, byte flags, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length + 1 > MaximumLengthField)
        {
            throw new InvalidDataException("An upload chunk exceeds the 1 MiB bound of the length field.");
        }

        var header = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(payload.Length + 1));
        header[4] = flags;
        await stream.WriteAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The clean end: a length of zero.</summary>
    public static async Task WriteEndAsync(Stream stream, CancellationToken cancellationToken)
    {
        var end = new byte[4];
        await stream.WriteAsync(end.AsMemory(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one chunk the way a server does, for the local protocol tests. Returns false at the
    /// zero-length end; an unknown flag bit is kept as it is - the server is content-agnostic.
    /// </summary>
    public static async Task<(byte Flags, byte[] Payload)?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0)
        {
            return null;
        }

        if (length > MaximumLengthField)
        {
            throw new InvalidDataException("An upload chunk exceeds the 1 MiB bound of the length field.");
        }

        var frame = new byte[length];
        await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        return (frame[0], frame[1..]);
    }
}
