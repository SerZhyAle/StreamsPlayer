using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamsPlayer.Core;

/// <summary>
/// The byte formats of a sealed relay (`DEVICE-EXCHANGE` item DL): the record framing inside a chunk payload,
/// the key ring (`0x4B`) and the media record (`0x4D`). Pure functions over bytes; the keys come from the
/// caller and nothing here keeps or prints one. The decoder and the encoder are the two state machines on top.
/// </summary>
public static partial class ExchangeSealedRelayRecords
{
    /// <summary>The content type a sealed upload declares; the true media type travels inside the key ring.</summary>
    public const string ContentType = "application/x-fmsx-sealed";

    public const byte KeyRingType = 0x4B;
    public const byte MediaRecordType = 0x4D;

    /// <summary>The key ring lists at most 64 group keys.</summary>
    public const int MaximumRingEntries = 64;

    public const int KeyIdLength = 8;
    public const int GroupKeyLength = 32;
    public const int EpochKeyLength = 32;
    public const int NonceLength = 12;
    public const int TagLength = 16;

    /// <summary>A record's own frame: 4-byte length, type byte. What a sealed media record adds to its clear bytes.</summary>
    public const int MediaOverhead = 4 + 1 + 4 + NonceLength + TagLength;

    /// <summary>Item U's bound on one chunk; a record rides inside one chunk, so no record may be longer.</summary>
    public const int MaximumRecordLength = ExchangeUploadChunks.MaximumLengthField;

    private static readonly byte[] KeyIdLabel = Encoding.ASCII.GetBytes("fmsx-relay-v1 key-id");

    /// <summary>The media type goes into an HTTP head for the player, so only a plain `type/subtype` token is accepted.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]{0,63}/[A-Za-z0-9][A-Za-z0-9!#$&^_.+-]{0,63}$")]
    private static partial Regex MediaTypePattern();

    public static bool IsSafeMediaType(string? mediaType) => mediaType is not null && MediaTypePattern().IsMatch(mediaType);

    /// <summary>The first 8 bytes of SHA-256 over the label and the group key: how a ring names the key an entry is sealed under.</summary>
    public static byte[] KeyId(ReadOnlySpan<byte> groupKey)
    {
        var input = new byte[KeyIdLabel.Length + groupKey.Length];
        KeyIdLabel.CopyTo(input, 0);
        groupKey.CopyTo(input.AsSpan(KeyIdLabel.Length));
        return SHA256.HashData(input)[..KeyIdLength];
    }

    internal static byte[] RingAad(string broadcastId, uint epoch) =>
        Encoding.UTF8.GetBytes($"relay-ring:{broadcastId}:{epoch}");

    internal static byte[] MediaAad(string broadcastId, uint epoch, byte flag) =>
        Encoding.UTF8.GetBytes($"relay-media:{broadcastId}:{epoch}:{flag}");

    /// <summary>Frames one record: `len` (4 bytes, counting the type byte and the body), `type`, the body.</summary>
    public static byte[] Frame(byte type, ReadOnlySpan<byte> body)
    {
        var record = new byte[4 + 1 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(record, (uint)(1 + body.Length));
        record[4] = type;
        body.CopyTo(record.AsSpan(5));
        return record;
    }

    /// <summary>Builds the key ring record of one epoch: every live group key seals the epoch key once.</summary>
    public static byte[] BuildKeyRing(string broadcastId, uint epoch, string mediaType, ReadOnlySpan<byte> epochKey,
        IReadOnlyCollection<byte[]> groupKeys)
    {
        if (!IsSafeMediaType(mediaType) || groupKeys.Count is 0 or > MaximumRingEntries || epochKey.Length != EpochKeyLength)
        {
            throw new ArgumentException("A key ring needs a plain media type, 1 to 64 group keys and a 32-byte epoch key.");
        }

        var type = Encoding.ASCII.GetBytes(mediaType);
        using var body = new MemoryStream();
        Span<byte> epochBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(epochBytes, epoch);
        body.Write(epochBytes);
        body.WriteByte((byte)type.Length);
        body.Write(type);
        body.WriteByte((byte)groupKeys.Count);
        var aad = RingAad(broadcastId, epoch);
        foreach (var groupKey in groupKeys)
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var wrapped = new byte[EpochKeyLength + TagLength];
            using (var aead = new AesGcm(groupKey, TagLength))
            {
                aead.Encrypt(nonce, epochKey, wrapped.AsSpan(0, EpochKeyLength), wrapped.AsSpan(EpochKeyLength, TagLength), aad);
            }

            body.Write(KeyId(groupKey));
            body.Write(nonce);
            body.Write(wrapped);
        }

        return Frame(KeyRingType, body.ToArray());
    }

    /// <summary>Builds one media record: the clear bytes sealed under the epoch key, bound to the broadcast, the epoch and the chunk flag.</summary>
    public static byte[] BuildMedia(string broadcastId, uint epoch, byte flag, ReadOnlySpan<byte> epochKey, ReadOnlySpan<byte> media)
    {
        var body = new byte[4 + NonceLength + media.Length + TagLength];
        BinaryPrimitives.WriteUInt32BigEndian(body, epoch);
        RandomNumberGenerator.Fill(body.AsSpan(4, NonceLength));
        using (var aead = new AesGcm(epochKey, TagLength))
        {
            aead.Encrypt(body.AsSpan(4, NonceLength), media, body.AsSpan(4 + NonceLength, media.Length),
                body.AsSpan(4 + NonceLength + media.Length, TagLength), MediaAad(broadcastId, epoch, flag));
        }

        return Frame(MediaRecordType, body);
    }

    /// <summary>
    /// A parsed key ring: the epoch, the true media type, and each entry's key id with the nonce and the wrapped
    /// epoch key. Opening an entry needs the group key; <see cref="TryOpen"/> does it for a candidate set.
    /// </summary>
    internal sealed record KeyRing(uint Epoch, string MediaType, IReadOnlyList<KeyRingEntry> Entries)
    {
        /// <summary>The epoch key sealed to the first of <paramref name="groupKeys"/> the ring lists; null when none is listed.</summary>
        /// <exception cref="CryptographicException">An entry names a key this device holds but does not authenticate.</exception>
        internal byte[]? TryOpen(string broadcastId, IReadOnlyList<byte[]> groupKeys)
        {
            var aad = RingAad(broadcastId, Epoch);
            foreach (var groupKey in groupKeys)
            {
                var id = KeyId(groupKey);
                foreach (var entry in Entries)
                {
                    if (!CryptographicOperations.FixedTimeEquals(entry.KeyId, id))
                    {
                        continue;
                    }

                    var epochKey = new byte[EpochKeyLength];
                    using var aead = new AesGcm(groupKey, TagLength);
                    aead.Decrypt(entry.Nonce, entry.Wrapped.AsSpan(0, EpochKeyLength),
                        entry.Wrapped.AsSpan(EpochKeyLength, TagLength), epochKey, aad);
                    return epochKey;
                }
            }

            return null;
        }
    }

    internal sealed record KeyRingEntry(byte[] KeyId, byte[] Nonce, byte[] Wrapped);

    /// <summary>Reads a key ring body (the bytes after the type byte); null when it is not shaped as item DL writes it.</summary>
    internal static KeyRing? ParseKeyRing(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4 + 1)
        {
            return null;
        }

        var epoch = BinaryPrimitives.ReadUInt32BigEndian(body);
        int typeLength = body[4];
        var offset = 5;
        if (body.Length < offset + typeLength + 1)
        {
            return null;
        }

        var mediaType = Encoding.ASCII.GetString(body.Slice(offset, typeLength));
        offset += typeLength;
        int count = body[offset++];
        const int entryLength = KeyIdLength + NonceLength + EpochKeyLength + TagLength;
        if (!IsSafeMediaType(mediaType) || count is 0 or > MaximumRingEntries || body.Length != offset + count * entryLength)
        {
            return null;
        }

        var entries = new List<KeyRingEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var entry = body.Slice(offset + index * entryLength, entryLength);
            entries.Add(new KeyRingEntry(entry[..KeyIdLength].ToArray(), entry.Slice(KeyIdLength, NonceLength).ToArray(),
                entry[(KeyIdLength + NonceLength)..].ToArray()));
        }

        return new KeyRing(epoch, mediaType, entries);
    }

    /// <summary>The epoch a media record was sealed in, read before any key is tried.</summary>
    internal static bool TryReadMediaEpoch(ReadOnlySpan<byte> body, out uint epoch)
    {
        epoch = 0;
        if (body.Length < 4 + NonceLength + TagLength)
        {
            return false;
        }

        epoch = BinaryPrimitives.ReadUInt32BigEndian(body);
        return true;
    }

    /// <summary>Opens a media record body under the epoch key and one flag; null when the tag does not authenticate.</summary>
    internal static byte[]? TryOpenMedia(string broadcastId, ReadOnlySpan<byte> body, ReadOnlySpan<byte> epochKey, byte flag)
    {
        if (!TryReadMediaEpoch(body, out var epoch))
        {
            return null;
        }

        var media = new byte[body.Length - 4 - NonceLength - TagLength];
        try
        {
            using var aead = new AesGcm(epochKey, TagLength);
            aead.Decrypt(body.Slice(4, NonceLength), body.Slice(4 + NonceLength, media.Length),
                body.Slice(4 + NonceLength + media.Length, TagLength), media, MediaAad(broadcastId, epoch, flag));
            return media;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
