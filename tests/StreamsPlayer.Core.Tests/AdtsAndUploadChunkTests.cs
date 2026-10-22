using System.Buffers.Binary;
using StreamsPlayer.Core;
using Xunit;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0217's format tests, no network: an audio upload's chunks are whole ADTS frames and all joinable,
/// and no chunk's length field exceeds the 1 MiB bound of item U.
/// </summary>
public sealed class AdtsAndUploadChunkTests
{
    /// <summary>Builds one synthetic AAC-LC ADTS frame - a header that names its own length, then that many bytes.</summary>
    private static byte[] Frame(int sampleRate = 44100, int channels = 1, int payloadBytes = 371)
    {
        var samplingIndex = sampleRate switch
        {
            96000 => 0, 88200 => 1, 64000 => 2, 48000 => 3, 44100 => 4, 32000 => 5, 24000 => 6, 22050 => 7,
            16000 => 8, 12000 => 9, 11025 => 10, 8000 => 11, _ => 4
        };
        var frameLength = 7 + payloadBytes;
        var frame = new byte[frameLength];
        frame[0] = 0xFF;
        frame[1] = 0xF1; // MPEG-4, layer 0, no CRC: protection absent.
        frame[2] = (byte)((1 << 6) | (samplingIndex << 2) | ((channels >> 2) & 1));
        frame[3] = (byte)(((channels & 3) << 6) | ((frameLength >> 11) & 0x03));
        frame[4] = (byte)(frameLength >> 3);
        frame[5] = (byte)(((frameLength & 7) << 5) | 0x1F);
        frame[6] = 0xFC;
        for (var index = 7; index < frameLength; index++)
        {
            frame[index] = (byte)(index * 31 & 0xFF);
        }

        return frame;
    }

    [Fact]
    public void AHeaderNamesItsOwnFrame()
    {
        var frame = Frame();
        Assert.True(AdtsFrames.TryReadHeader(frame, out var header));
        Assert.Equal(frame.Length, header.FrameLength);
        Assert.Equal(7, header.HeaderLength);
        Assert.Equal(44100, header.SampleRate);
        Assert.Equal(1, header.Channels);
    }

    [Fact]
    public void NotAFrameIsRefused()
    {
        Assert.False(AdtsFrames.TryReadHeader(new byte[] { 0x00, 0xF1, 0x40, 0x00, 0x00, 0x00, 0x00 }, out _));
        Assert.False(AdtsFrames.TryReadHeader([], out _));
        // A length below the header is not a frame; a CRC bit makes the header 9 bytes, not 8.
        var crc = Frame();
        crc[1] = 0xF0;
        Assert.True(AdtsFrames.TryReadHeader(crc, out var header));
        Assert.Equal(9, header.HeaderLength);
    }

    [Fact]
    public void TheStreamSplitsOnlyWholeFrames_AcrossAppends()
    {
        var stream = new AdtsFrameStream();
        var first = Frame(payloadBytes: 100);
        var second = Frame(payloadBytes: 50);

        // The two frames arrive as one broken-up flow: half of the first, then the rest and part of the second.
        stream.Append(first.AsSpan(0, 3).ToArray());
        Assert.False(stream.TryReadFrame(out _));
        stream.Append(first.AsSpan(3).ToArray());
        stream.Append(second.AsSpan(0, 20).ToArray());

        Assert.True(stream.TryReadFrame(out var wholeFirst));
        Assert.Equal(first, wholeFirst.ToArray());
        Assert.False(stream.TryReadFrame(out _));

        stream.Append(second.AsSpan(20).ToArray());
        Assert.True(stream.TryReadFrame(out var wholeSecond));
        Assert.Equal(second, wholeSecond.ToArray());
        Assert.False(stream.TryReadFrame(out _));
    }

    /// <summary>The producer's own aggregation, as the acceptance criterion reads it: frames in, whole-frame joinable chunks out, never over the bound.</summary>
    [Fact]
    public async Task UploadedChunksAreWholeAdtsFramesAndAllJoinable()
    {
        var frames = new List<byte[]>();
        for (var index = 0; index < 40; index++)
        {
            frames.Add(Frame(payloadBytes: 200 + index));
        }

        var splitter = new AdtsFrameStream();
        var payload = new MemoryStream();
        foreach (var frame in frames)
        {
            splitter.Append(frame);
            while (splitter.TryReadFrame(out var whole))
            {
                await ExchangeUploadChunks.WriteAsync(payload, ExchangeUploadChunks.Joinable, whole.ToArray(),
                    CancellationToken.None);
            }
        }

        await ExchangeUploadChunks.WriteEndAsync(payload, CancellationToken.None);

        payload.Position = 0;
        var reassembled = new MemoryStream();
        var chunkCount = 0;
        while (await ExchangeUploadChunks.ReadAsync(payload, CancellationToken.None) is { } chunk)
        {
            chunkCount++;
            Assert.Equal(ExchangeUploadChunks.Joinable, chunk.Flags);
            Assert.True(chunk.Payload.Length + 1 <= ExchangeUploadChunks.MaximumLengthField);
            reassembled.Write(chunk.Payload);
        }

        Assert.Equal(frames.Count, chunkCount);
        Assert.Equal(frames.SelectMany(frame => frame).ToArray(), reassembled.ToArray());
    }

    [Fact]
    public async Task TheLengthFieldBoundIsEnforcedOnBothSides()
    {
        using var within = new MemoryStream();
        await ExchangeUploadChunks.WriteAsync(within, ExchangeUploadChunks.Joinable, new byte[ExchangeUploadChunks.MaximumPayload],
            CancellationToken.None);
        within.Position = 0;
        var read = await ExchangeUploadChunks.ReadAsync(within, CancellationToken.None);
        Assert.Equal(ExchangeUploadChunks.MaximumPayload, read!.Value.Payload.Length);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExchangeUploadChunks.WriteAsync(new MemoryStream(), ExchangeUploadChunks.Joinable,
                new byte[ExchangeUploadChunks.MaximumPayload + 1], CancellationToken.None));
    }

    [Fact]
    public async Task TheEndIsALengthOfZero()
    {
        using var stream = new MemoryStream();
        await ExchangeUploadChunks.WriteEndAsync(stream, CancellationToken.None);
        stream.Position = 0;
        Assert.Null(await ExchangeUploadChunks.ReadAsync(stream, CancellationToken.None));
        Assert.Equal(4, stream.Length);
    }

    [Fact]
    public async Task AChunkCarriesItsFlagsByteInItsLength()
    {
        using var stream = new MemoryStream();
        await ExchangeUploadChunks.WriteAsync(stream, ExchangeUploadChunks.Header, new byte[5], CancellationToken.None);
        var written = stream.ToArray();
        // 4-byte length counting the flag byte plus the payload, then the flag, then the payload.
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32BigEndian(written));
        Assert.Equal(ExchangeUploadChunks.Header, written[4]);
    }
}
