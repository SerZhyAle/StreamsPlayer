using System.Buffers.Binary;

namespace StreamsPlayer.Core.Tests;

public sealed class AudioRecordingFramerTests
{
    [Fact]
    public void LateWaveStartKeepsTheFormatAndAlignsTheFirstAndLastSamples()
    {
        var source = Wave(10000, 4);
        var framer = new AudioRecordingFramer("audio/wav");
        Assert.Empty(framer.Push(source.AsSpan(0, 4097)));
        Assert.True(framer.Ready);
        var header = framer.Begin();
        Assert.Equal(44, header.Length);
        var data = Assert.Single(framer.Push(source.AsSpan(4097, 101)));
        Assert.Equal(source.AsSpan(4100, 98).ToArray(), data);
        var aligned = framer.AlignedDataLength(data.Length);
        Assert.Equal(96, aligned);
        var finished = framer.FinalHeader(data.Length);
        Assert.Equal(96u, BinaryPrimitives.ReadUInt32LittleEndian(finished.AsSpan(40)));
        Assert.Equal(132u, BinaryPrimitives.ReadUInt32LittleEndian(finished.AsSpan(4)));
    }

    [Fact]
    public void LateOpusStartRetainsHeadersAndRebasesOnlyWholePages()
    {
        var head = Page(0, 0, 2, "OpusHead"u8.ToArray());
        var tags = Page(1, 0, 0, "OpusTags"u8.ToArray());
        var old = Page(2, 9600, 0, new byte[4096]);
        var framer = new AudioRecordingFramer("audio/opus");
        var initial = head.Concat(tags).Concat(old).ToArray();
        for (var offset = 0; offset < initial.Length; offset += 7)
            Assert.Empty(framer.Push(initial.AsSpan(offset, Math.Min(7, initial.Length - offset))));
        Assert.True(framer.Ready);
        Assert.Equal(RecordedAudioFormat.Opus, framer.Extension);
        Assert.Equal(head.Concat(tags).ToArray(), framer.Begin());

        var continued = Page(3, 12000, 1, new byte[17]);
        Assert.Empty(framer.Push(continued));
        var first = Page(4, 16800, 0, new byte[123]);
        Assert.Empty(framer.Push(first.AsSpan(0, 10)));
        var recorded = Assert.Single(framer.Push(first.AsSpan(10)));
        Assert.Equal(4800, BinaryPrimitives.ReadInt64LittleEndian(recorded.AsSpan(6)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(recorded.AsSpan(18)));
        Assert.Equal(first.AsSpan(27).ToArray(), recorded.AsSpan(27).ToArray());
        Assert.NotEqual(first.AsSpan(22, 4).ToArray(), recorded.AsSpan(22, 4).ToArray());
        framer.End();
        Assert.Empty(framer.Push(Page(5, 19200, 0, new byte[123])));
    }

    [Fact]
    public void AnOggPageSharingTheLastHeaderAndAudioDoesNotReplayThatEarlierAudio()
    {
        var head = Page(0, 0, 2, "OpusHead"u8.ToArray());
        var mixed = new byte[27 + 2 + 8 + 4];
        "OggS"u8.CopyTo(mixed);
        mixed[26] = 2; mixed[27] = 8; mixed[28] = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(mixed.AsSpan(14), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(mixed.AsSpan(18), 1);
        "OpusTags"u8.CopyTo(mixed.AsSpan(29));
        "OLD!"u8.CopyTo(mixed.AsSpan(37));
        var framer = new AudioRecordingFramer("audio/opus");
        Assert.Empty(framer.Push(head.Concat(mixed).Concat(Page(2, 9600, 0, new byte[4096])).ToArray()));
        var prefix = framer.Begin();
        Assert.Equal(head.Length + 27 + 1 + 8, prefix.Length);
        Assert.Equal(-1, prefix.AsSpan().IndexOf("OLD!"u8));
        Assert.Equal(0, BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(head.Length + 6)));
    }

    [Fact]
    public void AacRecordingKeepsOnlyCompleteAdtsFramesAcrossReadsAndStop()
    {
        var frame = new byte[] { 0xFF, 0xF1, 0x50, 0x80, 1, 0x1F, 0xFC, 0 };
        var framer = new AudioRecordingFramer("audio/aac");
        Assert.Empty(framer.Push(Enumerable.Range(0, 512).SelectMany(_ => frame).ToArray()));
        Assert.True(framer.Ready);
        Assert.Empty(framer.Begin());
        Assert.Empty(framer.Push(frame.AsSpan(0, 3)));
        Assert.Equal(frame, Assert.Single(framer.Push(frame.AsSpan(3))));
        Assert.Empty(framer.Push(frame.AsSpan(0, 4)));
        framer.End();
        Assert.Empty(framer.Push(frame.AsSpan(4)));
    }

    [Fact]
    public void HugeOrMalformedHeadersRefuseRecordingWithoutKeepingUnboundedData()
    {
        var framer = new AudioRecordingFramer("audio/flac");
        var bytes = new byte[RecordedAudioFormat.SniffLength];
        "fLaC"u8.CopyTo(bytes); bytes[4] = 0x80; bytes[5] = 0x7F;
        Assert.Empty(framer.Push(bytes));
        Assert.False(framer.Ready);
        Assert.Empty(framer.Push(new byte[AudioRecordingFramer.MaximumHeaderBytes]));
        Assert.False(framer.Ready);
    }

    [Fact]
    public void RawAudioDoesNotReplayTheEarlierListeningBytes()
    {
        var framer = new AudioRecordingFramer("audio/mpeg");
        var old = new byte[4096]; "ID3"u8.CopyTo(old);
        Assert.Empty(framer.Push(old));
        Assert.Empty(framer.Begin());
        var live = new byte[417];
        new byte[] { 0xFF, 0xFB, 0x90, 0 }.CopyTo(live, 0);
        Assert.Equal(live, Assert.Single(framer.Push(live)));
        framer.End();
        Assert.Empty(framer.Push(live));
    }

    internal static byte[] Wave(int sampleBytes, ushort blockAlign)
    {
        var bytes = new byte[44 + sampleBytes];
        "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 44100);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 44100 * blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), sampleBytes);
        for (var i = 44; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static byte[] Page(uint sequence, long granule, byte flags, byte[] payload)
    {
        var laces = payload.Length / 255 + 1;
        var page = new byte[27 + laces + payload.Length];
        "OggS"u8.CopyTo(page); page[5] = flags; page[26] = (byte)laces;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        for (var i = 0; i < laces; i++) page[27 + i] = (byte)Math.Min(255, payload.Length - i * 255);
        payload.CopyTo(page, 27 + laces);
        return page;
    }
}
