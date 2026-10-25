using System.Buffers.Binary;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class BroadcastMpegTsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderContainsValidPatAndPmtWithTheActualTracks(bool audio)
    {
        var header = new BroadcastMpegTs(audio).Header();
        Assert.Equal(376, header.Length);
        Assert.Equal(0, Pid(header));
        Assert.Equal(BroadcastMpegTs.ProgramMapPid, Pid(header.AsSpan(188)));
        foreach (var offset in new[] { 0, 188 })
        {
            var packet = header.AsSpan(offset, 188);
            Assert.Equal(0x47, packet[0]);
            var sectionLength = ((packet[6] & 15) << 8) | packet[7];
            Assert.Equal(0u, Crc(packet.Slice(5, sectionLength + 3)));
        }
        var pmt = header.AsSpan(188);
        Assert.Equal(0x1b, pmt[17]);
        Assert.Equal(audio ? 0x17 : 0x12, pmt[7]);
        if (audio) Assert.Equal(0x0f, pmt[22]);
    }

    [Fact]
    public void OnlyAnIdrIsJoinableAndEveryIdrRepeatsTheProgramTables()
    {
        var mux = new BroadcastMpegTs(false);
        var idr = mux.Video([0, 0, 0, 1, 0x65, 1], 0, true);
        var predicted = mux.Video([0, 0, 0, 1, 0x41, 2], 333333, false);
        var secondIdr = mux.Video([0, 0, 0, 1, 0x65, 3], 666666, true);
        Assert.True(idr.Joinable);
        Assert.False(predicted.Joinable);
        Assert.True(secondIdr.Joinable);
        Assert.Equal(0, Pid(idr.Payload));
        Assert.Equal(0, Pid(secondIdr.Payload));
        Assert.Equal(0, Pid(predicted.Payload));
        Assert.Equal(0x50, idr.Payload[376 + 5]);
    }

    [Fact]
    public void VariableRatePicturesAlwaysRepeatTablesAndAudioKeepsThemAlive()
    {
        var mux = new BroadcastMpegTs(false);
        _ = mux.Video([1], 0, true);
        Assert.Equal(0, Pid(mux.Video([1], 2_499_999, false).Payload));
        Assert.Equal(0, Pid(mux.Video([1], 2_500_000, false).Payload));
        var audio = new BroadcastMpegTs(true);
        _ = audio.Video([1], 0, true);
        Assert.Equal(BroadcastMpegTs.AudioPid, Pid(audio.Audio([1], 1_999_999).Payload));
        Assert.Equal(0, Pid(audio.Audio([1], 2_000_000).Payload));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(170)]
    [InlineData(171)]
    [InlineData(184)]
    [InlineData(800000)]
    public void PacketizationPreservesThePayloadAndNeverExceedsTheSealedChunkLimit(int length)
    {
        var input = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var result = new BroadcastMpegTs(false).Video(input, 10_000_000, true).Payload;
        Assert.Equal(0, result.Length % 188);
        Assert.True(result.Length + ExchangeSealedRelayRecords.MediaOverhead <= ExchangeUploadChunks.MaximumPayload);
        using var pes = new MemoryStream();
        byte continuity = 0;
        for (var offset = 376; offset < result.Length; offset += 188)
        {
            var packet = result.AsSpan(offset, 188);
            Assert.Equal(BroadcastMpegTs.VideoPid, Pid(packet));
            Assert.Equal(continuity, (byte)(packet[3] & 15));
            continuity = (byte)((continuity + 1) & 15);
            var start = (packet[3] & 0x20) != 0 ? 5 + packet[4] : 4;
            pes.Write(packet[start..]);
        }
        var bytes = pes.ToArray();
        Assert.Equal(new byte[] { 0, 0, 1, 0xe0 }, bytes[..4]);
        Assert.Equal(90000L, Pts(bytes.AsSpan(9, 5)));
        Assert.Equal(input, bytes[14..]);
    }

    [Fact]
    public void AudioHasItsOwnPidAndNeverMakesAVideoJoinPoint()
    {
        var mux = new BroadcastMpegTs(true);
        _ = mux.Video([1], 20_000_000, true);
        var audio = mux.Audio([0xff, 0xf1, 1, 2, 3], 20_000_000);
        Assert.False(audio.Joinable);
        Assert.Equal(BroadcastMpegTs.AudioPid, Pid(audio.Payload));
        var start = 5 + audio.Payload[4];
        var pes = audio.Payload.AsSpan(start);
        Assert.Equal(0xc0, pes[3]);
        Assert.Equal(13, BinaryPrimitives.ReadUInt16BigEndian(pes.Slice(4, 2)));
        Assert.Equal(180000L, Pts(pes.Slice(9, 5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastMpegTs(false).Audio([1], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastMpegTs(true).Video(new byte[800001], 0, true));
    }

    private static int Pid(ReadOnlySpan<byte> packet) => ((packet[1] & 31) << 8) | packet[2];
    private static long Pts(ReadOnlySpan<byte> p) => (long)(p[0] & 14) << 29 | (long)p[1] << 22
        | (long)(p[2] & 254) << 14 | (long)p[3] << 7 | (uint)p[4] >> 1;

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint remainder = uint.MaxValue;
        foreach (var b in bytes)
        {
            remainder ^= (uint)b << 24;
            for (var i = 0; i < 8; ++i)
                remainder = (remainder << 1) ^ ((remainder & 0x80000000) == 0 ? 0u : 0x04c11db7u);
        }
        return remainder;
    }
}
