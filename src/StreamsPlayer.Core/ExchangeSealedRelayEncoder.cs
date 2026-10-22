using System.Security.Cryptography;

namespace StreamsPlayer.Core;

/// <summary>
/// The broadcaster's side of a sealed relay (`DEVICE-EXCHANGE` item DL): the group keys by purpose (the account's
/// own devices, one per guest grant, one per outward link), the epoch and its key, the header chunk with the key
/// ring, and every media chunk sealed. It knows no media type beyond the string it is given: audio and video use
/// it alike. State changes (a key dropped, a new epoch) are serialized; the caller opens a replacement upload
/// with <see cref="HeaderChunk"/> after each one.
/// </summary>
public sealed class ExchangeSealedRelayEncoder : IDisposable
{
    /// <summary>One epoch key seals at most 2^32 records (item DL).</summary>
    public const long MaximumRecordsPerEpoch = 1L << 32;

    private readonly object _gate = new();
    private readonly string _broadcastId;
    private readonly Dictionary<string, byte[]> _groupKeys = [];
    private byte[] _epochKey;
    private long _records;

    public ExchangeSealedRelayEncoder(string broadcastId, string mediaType)
    {
        if (!ExchangeSealedRelayRecords.IsSafeMediaType(mediaType))
        {
            throw new ArgumentException("A sealed relay needs a plain media type.", nameof(mediaType));
        }

        _broadcastId = broadcastId;
        MediaType = mediaType;
        Epoch = 1;
        _epochKey = RandomNumberGenerator.GetBytes(ExchangeSealedRelayRecords.EpochKeyLength);
    }

    public string MediaType { get; }

    public uint Epoch { get; private set; }

    /// <summary>Whether the epoch key has sealed as many records as it may; the caller opens a new epoch before the next chunk.</summary>
    public bool NeedsNewEpoch
    {
        get
        {
            lock (_gate)
            {
                return _records >= MaximumRecordsPerEpoch;
            }
        }
    }

    /// <summary>Makes a group key (32 random bytes) for <paramref name="purpose"/> and returns a copy; an existing one is replaced.</summary>
    public byte[] NewGroupKey(string purpose)
    {
        var key = RandomNumberGenerator.GetBytes(ExchangeSealedRelayRecords.GroupKeyLength);
        lock (_gate)
        {
            Replace(purpose, key);
        }

        return (byte[])key.Clone();
    }

    /// <summary>The group key for a purpose, or null when there is none.</summary>
    public byte[]? GroupKey(string purpose)
    {
        lock (_gate)
        {
            return _groupKeys.TryGetValue(purpose, out var key) ? (byte[])key.Clone() : null;
        }
    }

    /// <summary>Adopts a group key made elsewhere (a hand-over, or a test); it must be 32 bytes.</summary>
    public void SetGroupKey(string purpose, ReadOnlySpan<byte> key)
    {
        if (key.Length != ExchangeSealedRelayRecords.GroupKeyLength)
        {
            throw new ArgumentException("A group key is 32 bytes.", nameof(key));
        }

        lock (_gate)
        {
            Replace(purpose, key.ToArray());
        }
    }

    public int GroupKeyCount
    {
        get
        {
            lock (_gate)
            {
                return _groupKeys.Count;
            }
        }
    }

    /// <summary>Drops the group key of a purpose (an own device removed, a grant or a link withdrawn). The caller opens a new epoch next.</summary>
    public bool DropGroupKey(string purpose)
    {
        lock (_gate)
        {
            if (!_groupKeys.Remove(purpose, out var key))
            {
                return false;
            }

            CryptographicOperations.ZeroMemory(key);
            return true;
        }
    }

    /// <summary>
    /// Starts the next epoch with a fresh epoch key. Group keys dropped or replaced since the last epoch no
    /// longer open anything from here on; the boxes carrying the new group keys go out first, then the
    /// replacement upload starts with <see cref="HeaderChunk"/>.
    /// </summary>
    public uint NewEpoch()
    {
        lock (_gate)
        {
            CryptographicOperations.ZeroMemory(_epochKey);
            _epochKey = RandomNumberGenerator.GetBytes(ExchangeSealedRelayRecords.EpochKeyLength);
            _records = 0;
            return ++Epoch;
        }
    }

    /// <summary>
    /// The payload of the header chunk (flag `0x02`): the key ring first, then the media record of the format
    /// header when the format has one (PAT and PMT for MPEG-TS; none for ADTS).
    /// </summary>
    public byte[] HeaderChunk(ReadOnlySpan<byte> formatHeader)
    {
        lock (_gate)
        {
            if (_groupKeys.Count == 0)
            {
                throw new InvalidOperationException("A key ring needs at least one group key.");
            }

            var ring = ExchangeSealedRelayRecords.BuildKeyRing(_broadcastId, Epoch, MediaType, _epochKey, _groupKeys.Values);
            if (formatHeader.IsEmpty)
            {
                return ring;
            }

            var media = Seal(ExchangeUploadChunks.Header, formatHeader);
            var chunk = new byte[ring.Length + media.Length];
            ring.CopyTo(chunk, 0);
            media.CopyTo(chunk, ring.Length);
            return chunk;
        }
    }

    /// <summary>The payload of a media chunk carrying <paramref name="flag"/>: one media record.</summary>
    public byte[] SealChunk(byte flag, ReadOnlySpan<byte> media)
    {
        lock (_gate)
        {
            return Seal(flag, media);
        }
    }

    private byte[] Seal(byte flag, ReadOnlySpan<byte> media)
    {
        if (_records >= MaximumRecordsPerEpoch)
        {
            throw new InvalidOperationException("The epoch key has sealed its last record; open a new epoch.");
        }

        _records++;
        return ExchangeSealedRelayRecords.BuildMedia(_broadcastId, Epoch, flag, _epochKey, media);
    }

    private void Replace(string purpose, byte[] key)
    {
        if (_groupKeys.TryGetValue(purpose, out var old))
        {
            CryptographicOperations.ZeroMemory(old);
        }
        else if (_groupKeys.Count >= ExchangeSealedRelayRecords.MaximumRingEntries)
        {
            throw new InvalidOperationException("A key ring lists at most 64 group keys.");
        }

        _groupKeys[purpose] = key;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var key in _groupKeys.Values)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            _groupKeys.Clear();
            CryptographicOperations.ZeroMemory(_epochKey);
        }
    }
}
