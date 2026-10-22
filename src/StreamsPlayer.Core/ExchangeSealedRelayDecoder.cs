using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StreamsPlayer.Core;

/// <summary>What the decoder wants the caller to do after a feed (`DEVICE-EXCHANGE` item DL).</summary>
public enum ExchangeSealedRelayStatus
{
    /// <summary>Everything fed so far was understood; more bytes may follow.</summary>
    Ok,
    /// <summary>A media record names an epoch this listener holds no key for: reconnect once and read the current header chunk.</summary>
    EpochNotHeld,
    /// <summary>A record is malformed or does not authenticate. Nothing from it was played; the listener stops with `sealed-failed`.</summary>
    Failed
}

/// <summary>
/// The listener's side of a sealed relay: bytes of the HTTP body in, clear media out. It reads records by their
/// own length however HTTP split the body, opens the key ring with the group key it holds, keeps the epoch key
/// of the ring it opened, and plays a media record only after its tag authenticates. After
/// <see cref="ExchangeSealedRelayStatus.Failed"/> or <see cref="ExchangeSealedRelayStatus.EpochNotHeld"/> it
/// accepts nothing further.
/// </summary>
/// <remarks>
/// The flag byte of the chunk a media record rode in is part of its associated data but never reaches a
/// listener - the server consumes it. A record that shares its chunk with the key ring is the header chunk
/// (`0x02`); any other is tried at the flag that worked last, then at the other flags a producer sets. A
/// wrong flag simply fails the tag, so the cost is a few AEAD calls and no byte is ever played unauthenticated.
/// </remarks>
public sealed class ExchangeSealedRelayDecoder
{
    private static readonly byte[] FlagOrder = [ExchangeUploadChunks.Joinable, 0x00, ExchangeUploadChunks.Header,
        ExchangeUploadChunks.Header | ExchangeUploadChunks.Joinable];

    // Two epochs are enough: the one playing and the one a replacement upload is about to start.
    private const int KeptEpochs = 2;

    private readonly string _broadcastId;
    private readonly IReadOnlyList<byte[]> _groupKeys;
    private readonly SortedDictionary<uint, byte[]> _epochKeys = [];
    private byte[] _buffer = new byte[4096];
    private int _count;
    private byte _lastFlag = ExchangeUploadChunks.Joinable;
    private bool _afterRing;

    public ExchangeSealedRelayDecoder(string broadcastId, IReadOnlyList<byte[]> groupKeys)
    {
        _broadcastId = broadcastId;
        _groupKeys = groupKeys;
    }

    /// <summary>The true media type from the key ring; null until a ring has been opened.</summary>
    public string? MediaType { get; private set; }

    /// <summary>The epoch of the last key ring opened; zero before one.</summary>
    public uint Epoch { get; private set; }

    public ExchangeSealedRelayStatus Status { get; private set; } = ExchangeSealedRelayStatus.Ok;

    internal byte[]? EpochKey(uint epoch) => _epochKeys.TryGetValue(epoch, out var key) ? key : null;

    /// <summary>Feeds the next bytes of the body; every media record that completed and authenticated is appended to <paramref name="clearMedia"/>.</summary>
    public ExchangeSealedRelayStatus Feed(ReadOnlySpan<byte> body, ICollection<byte[]> clearMedia)
    {
        if (Status != ExchangeSealedRelayStatus.Ok)
        {
            return Status;
        }

        if (_count + body.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + body.Length));
        }

        body.CopyTo(_buffer.AsSpan(_count));
        _count += body.Length;

        var offset = 0;
        while (_count - offset >= 4)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(offset, 4));
            if (length is 0 or > ExchangeSealedRelayRecords.MaximumRecordLength)
            {
                return Stop(ExchangeSealedRelayStatus.Failed);
            }

            if (_count - offset - 4 < length)
            {
                break;
            }

            var record = _buffer.AsSpan(offset + 4, (int)length);
            var result = Handle(record[0], record[1..], clearMedia);
            if (result != ExchangeSealedRelayStatus.Ok)
            {
                return Stop(result);
            }

            offset += 4 + (int)length;
        }

        if (offset > 0)
        {
            Buffer.BlockCopy(_buffer, offset, _buffer, 0, _count - offset);
            _count -= offset;
        }

        return Status;
    }

    private ExchangeSealedRelayStatus Handle(byte type, ReadOnlySpan<byte> body, ICollection<byte[]> clearMedia)
    {
        switch (type)
        {
            case ExchangeSealedRelayRecords.KeyRingType:
                return OpenRing(body);
            case ExchangeSealedRelayRecords.MediaRecordType:
                return OpenMedia(body, clearMedia);
            default:
                // Item DL: a record of an unknown type is skipped.
                return ExchangeSealedRelayStatus.Ok;
        }
    }

    private ExchangeSealedRelayStatus OpenRing(ReadOnlySpan<byte> body)
    {
        var ring = ExchangeSealedRelayRecords.ParseKeyRing(body);
        if (ring is null || MediaType is not null && !string.Equals(MediaType, ring.MediaType, StringComparison.Ordinal))
        {
            return ExchangeSealedRelayStatus.Failed;
        }

        byte[]? epochKey;
        try
        {
            epochKey = ring.TryOpen(_broadcastId, _groupKeys);
        }
        catch (CryptographicException)
        {
            return ExchangeSealedRelayStatus.Failed;
        }

        MediaType = ring.MediaType;
        _afterRing = true;
        if (epochKey is null)
        {
            // The ring lists none of the keys this listener holds: its epoch cannot be opened, and the first
            // media record of it says so. A ring is not an error by itself.
            return ExchangeSealedRelayStatus.Ok;
        }

        _epochKeys[ring.Epoch] = epochKey;
        Epoch = ring.Epoch;
        while (_epochKeys.Count > KeptEpochs)
        {
            var oldest = _epochKeys.Keys.First();
            CryptographicOperations.ZeroMemory(_epochKeys[oldest]);
            _epochKeys.Remove(oldest);
        }

        return ExchangeSealedRelayStatus.Ok;
    }

    private ExchangeSealedRelayStatus OpenMedia(ReadOnlySpan<byte> body, ICollection<byte[]> clearMedia)
    {
        if (!ExchangeSealedRelayRecords.TryReadMediaEpoch(body, out var epoch))
        {
            return ExchangeSealedRelayStatus.Failed;
        }

        if (!_epochKeys.TryGetValue(epoch, out var epochKey))
        {
            return ExchangeSealedRelayStatus.EpochNotHeld;
        }

        // Right after a ring the record may be the header chunk's own (ring and format header ride together) or,
        // for a format with no header such as ADTS, the first joinable chunk of the stream.
        var candidates = (_afterRing ? FlagOrder.Prepend(_lastFlag).Prepend(ExchangeUploadChunks.Header) : FlagOrder.Prepend(_lastFlag))
            .Distinct().ToArray();
        _afterRing = false;
        foreach (var flag in candidates)
        {
            if (ExchangeSealedRelayRecords.TryOpenMedia(_broadcastId, body, epochKey, flag) is { } media)
            {
                _lastFlag = flag == ExchangeUploadChunks.Header ? _lastFlag : flag;
                clearMedia.Add(media);
                return ExchangeSealedRelayStatus.Ok;
            }
        }

        return ExchangeSealedRelayStatus.Failed;
    }

    private ExchangeSealedRelayStatus Stop(ExchangeSealedRelayStatus status)
    {
        Status = status;
        _count = 0;
        foreach (var key in _epochKeys.Values)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        _epochKeys.Clear();
        return status;
    }
}
