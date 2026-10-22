using System.Text;
using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// DEVICE-EXCHANGE item DL: the sealed relay records, replayed against `vectors/e2e-v1.json` (its `relay` stream
/// is a real producer's output) and round-tripped through the encoder for what a corpus cannot fix.
/// </summary>
public sealed class ExchangeSealedRelayTests
{
    private static readonly JsonDocument Corpus = E2eCorpus.Load();
    private static JsonElement Relay => Corpus.RootElement.GetProperty("relay");
    private static string BroadcastId => Relay.GetProperty("broadcastId").GetString()!;

    private static byte[] GroupKey(string name) => Convert.FromHexString(Relay.GetProperty("groupKeys").GetProperty(name).GetString()!);

    private sealed record Chunk(byte Flag, uint Epoch, byte[] Payload, byte[][] Clear);

    private static List<Chunk> Chunks() => Relay.GetProperty("chunks").EnumerateArray().Select(item =>
    {
        Assert.True(ExchangeBase64Url.TryDecode(item.GetProperty("payload").GetString(), out var payload));
        return new Chunk((byte)item.GetProperty("flag").GetInt32(), item.GetProperty("epoch").GetUInt32(), payload,
            item.GetProperty("clearMedia").EnumerateArray().Select(media => Convert.FromHexString(media.GetString()!)).ToArray());
    }).ToList();

    private static byte[] Join(IEnumerable<byte[]> parts) => parts.SelectMany(part => part).ToArray();

    private static ExchangeSealedRelayDecoder Decoder(params string[] groupKeyNames) =>
        new(BroadcastId, groupKeyNames.Select(GroupKey).ToArray());

    [Fact]
    public void Corpus_ConstantsAndKeyIdsMatchTheProducer()
    {
        Assert.Equal(ExchangeSealedRelayRecords.ContentType, Relay.GetProperty("contentType").GetString());
        Assert.Equal("video/mp2t", Relay.GetProperty("mediaType").GetString());
        var header = Chunks()[0].Payload;
        var ringLength = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
        Assert.Equal(ExchangeSealedRelayRecords.KeyRingType, header[4]);
        var ring = ExchangeSealedRelayRecords.ParseKeyRing(header.AsSpan(5, ringLength - 1));

        Assert.NotNull(ring);
        Assert.Equal(1u, ring.Epoch);
        Assert.Equal("video/mp2t", ring.MediaType);
        Assert.Contains(ring.Entries, entry => entry.KeyId.SequenceEqual(ExchangeSealedRelayRecords.KeyId(GroupKey("own-1"))));
        Assert.Contains(ring.Entries, entry => entry.KeyId.SequenceEqual(ExchangeSealedRelayRecords.KeyId(GroupKey("guest-1"))));
        Assert.Contains(ring.Entries, entry => entry.KeyId.SequenceEqual(ExchangeSealedRelayRecords.KeyId(GroupKey("link-1"))));
    }

    [Theory]
    [InlineData("own-1")]
    [InlineData("guest-1")]
    [InlineData("link-1")]
    public void Corpus_EpochOneOpensToTheClearMediaUnderEachGroupKeyOfTheRing(string groupKey)
    {
        var decoder = Decoder(groupKey);
        var clear = new List<byte[]>();

        foreach (var chunk in Chunks().Where(chunk => chunk.Epoch == 1))
        {
            Assert.Equal(ExchangeSealedRelayStatus.Ok, decoder.Feed(chunk.Payload, clear));
        }

        var expected = Chunks().Where(chunk => chunk.Epoch == 1).SelectMany(chunk => chunk.Clear).ToArray();
        Assert.Equal(expected.Length, clear.Count);
        Assert.Equal(Join(expected), Join(clear));
        Assert.Equal("video/mp2t", decoder.MediaType);
        Assert.Equal(1u, decoder.Epoch);
        Assert.Equal(Convert.FromHexString(Relay.GetProperty("epochKeys").GetProperty("1").GetString()!), decoder.EpochKey(1));
    }

    [Theory]
    [InlineData("own-2")]
    [InlineData("link-1")]
    public void Corpus_EpochTwoOpensForTheKeysItKeptAndTheNewOwnKey(string groupKey)
    {
        var decoder = Decoder(groupKey);
        var clear = new List<byte[]>();

        foreach (var chunk in Chunks().Where(chunk => chunk.Epoch == 2))
        {
            Assert.Equal(ExchangeSealedRelayStatus.Ok, decoder.Feed(chunk.Payload, clear));
        }

        Assert.Equal(Join(Chunks().Where(chunk => chunk.Epoch == 2).SelectMany(chunk => chunk.Clear)), Join(clear));
        Assert.Equal(Convert.FromHexString(Relay.GetProperty("epochKeys").GetProperty("2").GetString()!), decoder.EpochKey(2));
    }

    [Theory]
    [InlineData("own-1")]
    [InlineData("guest-1")]
    public void Corpus_EpochTwoIsNotHeldByAKeyTheChangeDropped(string droppedKey)
    {
        var decoder = Decoder(droppedKey);
        var clear = new List<byte[]>();
        var status = ExchangeSealedRelayStatus.Ok;

        foreach (var chunk in Chunks().Where(chunk => chunk.Epoch == 2))
        {
            status = decoder.Feed(chunk.Payload, clear);
            if (status != ExchangeSealedRelayStatus.Ok)
            {
                break;
            }
        }

        Assert.Equal(ExchangeSealedRelayStatus.EpochNotHeld, status);
        Assert.Empty(clear);
        Assert.Null(decoder.EpochKey(2));
    }

    [Fact]
    public void Corpus_AnySplitOfTheBodyGivesTheSameClearMedia()
    {
        var body = Join(Chunks().Where(chunk => chunk.Epoch == 1).Select(chunk => chunk.Payload));
        var expected = Join(Chunks().Where(chunk => chunk.Epoch == 1).SelectMany(chunk => chunk.Clear));

        for (var split = 0; split <= body.Length; split++)
        {
            var decoder = Decoder("own-1");
            var clear = new List<byte[]>();
            Assert.Equal(ExchangeSealedRelayStatus.Ok, decoder.Feed(body.AsSpan(0, split), clear));
            Assert.Equal(ExchangeSealedRelayStatus.Ok, decoder.Feed(body.AsSpan(split), clear));
            Assert.Equal(expected, Join(clear));
        }

        var singles = Decoder("own-1");
        var singleClear = new List<byte[]>();
        foreach (var value in body)
        {
            singles.Feed([value], singleClear);
        }

        Assert.Equal(expected, Join(singleClear));
    }

    [Fact]
    public void Corpus_ATamperedMediaRecordIsNeverPlayed()
    {
        var decoder = Decoder("own-1");
        var clear = new List<byte[]>();
        var chunks = Chunks().Where(chunk => chunk.Epoch == 1).ToList();
        Assert.Equal(ExchangeSealedRelayStatus.Ok, decoder.Feed(chunks[0].Payload, clear));
        var damaged = (byte[])chunks[1].Payload.Clone();
        damaged[^1] ^= 0x01;

        Assert.Equal(ExchangeSealedRelayStatus.Failed, decoder.Feed(damaged, clear));
        Assert.Equal(chunks[0].Clear.Length, clear.Count);
        // Once failed it takes nothing more, so nothing after the damage can be played either.
        Assert.Equal(ExchangeSealedRelayStatus.Failed, decoder.Feed(chunks[2].Payload, clear));
        Assert.Equal(chunks[0].Clear.Length, clear.Count);
    }

    [Fact]
    public void Corpus_ACopiedRecordFromAnotherBroadcastDoesNotOpen()
    {
        var decoder = new ExchangeSealedRelayDecoder("another-broadcast", [GroupKey("own-1")]);
        var clear = new List<byte[]>();

        Assert.Equal(ExchangeSealedRelayStatus.Failed, decoder.Feed(Chunks()[0].Payload, clear));
        Assert.Empty(clear);
    }

    private static byte[] Record(byte type, byte[] body) => ExchangeSealedRelayRecords.Frame(type, body);

    [Fact]
    public void Decoder_SkipsAnUnknownRecordTypeAndRefusesBadFraming()
    {
        using var encoder = new ExchangeSealedRelayEncoder("b1", "audio/aac");
        var key = encoder.NewGroupKey("own");
        var decoder = new ExchangeSealedRelayDecoder("b1", [key]);
        var clear = new List<byte[]>();

        Assert.Equal(ExchangeSealedRelayStatus.Ok, decoder.Feed(Record(0x7E, [1, 2, 3]), clear));
        Assert.Equal(ExchangeSealedRelayStatus.Ok, decoder.Feed(encoder.HeaderChunk([]), clear));
        Assert.Equal(ExchangeSealedRelayStatus.Ok, decoder.Feed(encoder.SealChunk(ExchangeUploadChunks.Joinable, [9, 9]), clear));
        Assert.Equal([9, 9], clear.Single());

        Assert.Equal(ExchangeSealedRelayStatus.Failed, decoder.Feed([0, 0, 0, 0], clear));
        Assert.Equal(ExchangeSealedRelayStatus.Failed, new ExchangeSealedRelayDecoder("b1", [key]).Feed([0xFF, 0xFF, 0xFF, 0xFF], clear));
    }

    [Theory]
    [InlineData("audio/aac", true)]
    [InlineData("video/mp2t", true)]
    [InlineData("application/x-fmsx-sealed", true)]
    [InlineData("video/mp2t\r\nX-Evil: 1", false)]
    [InlineData("video", false)]
    [InlineData("a/b c", false)]
    [InlineData("", false)]
    public void MediaType_IsAPlainTokenBecauseItGoesIntoAnHttpHead(string mediaType, bool safe) =>
        Assert.Equal(safe, ExchangeSealedRelayRecords.IsSafeMediaType(mediaType));

    [Theory]
    [InlineData("audio/aac", 0)]
    [InlineData("video/mp2t", 188)]
    public void Encoder_RoundTripsThroughTheDecoderForEachGroupKey(string mediaType, int headerBytes)
    {
        using var encoder = new ExchangeSealedRelayEncoder("round-trip", mediaType);
        var own = encoder.NewGroupKey("own");
        var link = encoder.NewGroupKey("link:1");
        var header = Enumerable.Range(0, headerBytes).Select(i => (byte)(i * 7)).ToArray();
        var media = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();

        var stream = Join([encoder.HeaderChunk(header), encoder.SealChunk(ExchangeUploadChunks.Joinable, media),
            encoder.SealChunk(0, media[..188]), encoder.SealChunk(ExchangeUploadChunks.Joinable, media[188..400])]);

        foreach (var key in new[] { own, link })
        {
            var decoder = new ExchangeSealedRelayDecoder("round-trip", [key]);
            var clear = new List<byte[]>();
            Assert.Equal(ExchangeSealedRelayStatus.Ok, decoder.Feed(stream, clear));
            var expected = (headerBytes > 0 ? new[] { header } : []).Concat([media, media[..188], media[188..400]]).ToArray();
            Assert.Equal(expected.Length, clear.Count);
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.Equal(expected[index], clear[index]);
            }

            Assert.Equal(mediaType, decoder.MediaType);
        }
    }

    [Fact]
    public void Encoder_AGroupKeyThatWasNeverInTheRingOpensNothing()
    {
        using var encoder = new ExchangeSealedRelayEncoder("stranger", "audio/aac");
        encoder.NewGroupKey("own");
        var stranger = new ExchangeSealedRelayDecoder("stranger", [new byte[32]]);
        var clear = new List<byte[]>();

        Assert.Equal(ExchangeSealedRelayStatus.Ok, stranger.Feed(encoder.HeaderChunk([]), clear));
        Assert.Equal(ExchangeSealedRelayStatus.EpochNotHeld,
            stranger.Feed(encoder.SealChunk(ExchangeUploadChunks.Joinable, [1]), clear));
        Assert.Empty(clear);
    }

    [Fact]
    public void Encoder_ANewEpochWithoutADroppedKeyLocksThatKeyOut()
    {
        using var encoder = new ExchangeSealedRelayEncoder("epochs", "video/mp2t");
        var own = encoder.NewGroupKey("own");
        var guest = encoder.NewGroupKey("grant:g1");
        var guestDecoder = new ExchangeSealedRelayDecoder("epochs", [guest]);
        var ownDecoder = new ExchangeSealedRelayDecoder("epochs", [own]);
        var clear = new List<byte[]>();
        var first = Join([encoder.HeaderChunk([1]), encoder.SealChunk(1, [2])]);
        Assert.Equal(ExchangeSealedRelayStatus.Ok, guestDecoder.Feed(first, clear));
        Assert.Equal(ExchangeSealedRelayStatus.Ok, ownDecoder.Feed(first, clear));

        Assert.True(encoder.DropGroupKey("grant:g1"));
        Assert.Equal(2u, encoder.NewEpoch());
        var second = Join([encoder.HeaderChunk([1]), encoder.SealChunk(1, [3])]);

        Assert.Equal(ExchangeSealedRelayStatus.Ok, ownDecoder.Feed(second, clear));
        Assert.Equal(2u, ownDecoder.Epoch);
        Assert.Equal(ExchangeSealedRelayStatus.EpochNotHeld, guestDecoder.Feed(second, clear));
    }

    [Fact]
    public void Encoder_RefusesARingWithNoKeysAndAUnsafeMediaType()
    {
        using var encoder = new ExchangeSealedRelayEncoder("nokeys", "audio/aac");

        Assert.Throws<InvalidOperationException>(() => encoder.HeaderChunk([]));
        Assert.Throws<ArgumentException>(() => new ExchangeSealedRelayEncoder("x", "bad type"));
        Assert.Throws<ArgumentException>(() => encoder.SetGroupKey("own", new byte[16]));
    }

    [Fact]
    public void Encoder_ASealedChunkNeverShowsTheMediaOrItsSyncBytes()
    {
        using var encoder = new ExchangeSealedRelayEncoder("sync", "video/mp2t");
        encoder.NewGroupKey("own");
        var transportStream = new byte[188 * 20];
        for (var packet = 0; packet < 20; packet++)
        {
            transportStream[packet * 188] = 0x47;
        }

        var sealedChunk = Join([encoder.HeaderChunk(transportStream[..188]), encoder.SealChunk(1, transportStream)]);

        var syncAtPacketStarts = Enumerable.Range(0, sealedChunk.Length / 188).Count(index => sealedChunk[index * 188] == 0x47);
        Assert.True(syncAtPacketStarts <= 2, "a ciphertext may hold a 0x47 by chance, never at every step");
    }
}
