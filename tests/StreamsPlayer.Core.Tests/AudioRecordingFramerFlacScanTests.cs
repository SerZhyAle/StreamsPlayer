using System.Diagnostics;
using System.Text;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// S14-1: the FLAC frame-end scan folds each byte once, however the bytes arrive; one corrupt frame costs that
/// frame and not the recording; and a stream built to make the scan quadratic is answered in bounded time.
/// </summary>
public sealed class AudioRecordingFramerFlacScanTests
{
    private const int FrameBytes = 700;

    // Past the sniff length, so the first Push classifies the stream, reads the header and frames the rest.
    private const int StartBytes = 5000;

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(4096)]
    public void Whole_frames_come_out_whatever_the_chunking(int chunk)
    {
        var frames = Frames(40);
        var (framer, recorded) = Record(Stream(frames), chunk);

        Assert.True(framer.Ready);
        Assert.NotEmpty(recorded);

        // Recording began inside frame 7 (the frame in flight is dropped); the last frame has no successor to
        // end it, so it is withheld. Everything between comes out whole, in order, byte for byte.
        var first = frames.FindIndex(frame => frame.SequenceEqual(recorded[0]));
        Assert.InRange(first, 6, 9);
        Assert.Equal(frames.Count - 1 - first, recorded.Count);
        for (var index = 0; index < recorded.Count; index++)
        {
            Assert.Equal(frames[first + index], recorded[index]);
        }
    }

    [Fact]
    public void One_corrupt_frame_costs_that_frame_and_not_the_recording()
    {
        const int count = 1300; // about 900 KB: far enough past the corrupt frame to give up on its sync
        var frames = Frames(count);
        const int corrupt = 20;
        frames[corrupt][400] ^= 0x55;

        var (framer, recorded) = Record(Stream(frames), 4096);

        Assert.True(framer.Ready);
        Assert.Equal(frames[count - 2], recorded[^1]);
        Assert.DoesNotContain(recorded, output => output.SequenceEqual(frames[corrupt]));
    }

    [Fact]
    public void A_stream_built_to_defeat_the_end_scan_is_answered_in_bounded_time()
    {
        // A valid frame header (its own CRC-8 holds) every 16 bytes: every position is a candidate end whose
        // CRC-16 has to be checked, which used to cost the whole prefix each time.
        var unit = new List<byte> { 0xFF, 0xF8, 0x19, 0x18, 0x00 };
        unit.Add(Crc8(unit));
        unit.AddRange(Enumerable.Repeat((byte)0x11, 10));
        var stream = new List<byte>(StreamInfo());
        for (var index = 0; index < 56_000; index++)
        {
            stream.AddRange(unit);
        }

        var framer = new AudioRecordingFramer("audio/flac");
        var clock = Stopwatch.StartNew();
        framer.Push(stream.ToArray());
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the end scan took {clock.Elapsed}");
    }

    private static (AudioRecordingFramer Framer, List<byte[]> Recorded) Record(byte[] stream, int chunk)
    {
        var framer = new AudioRecordingFramer("audio/flac");
        Assert.Empty(framer.Push(stream.AsSpan(0, StartBytes)));
        Assert.True(framer.Ready);
        framer.Begin();
        var recorded = new List<byte[]>();
        for (var offset = StartBytes; offset < stream.Length; offset += chunk)
        {
            recorded.AddRange(framer.Push(stream.AsSpan(offset, Math.Min(chunk, stream.Length - offset))));
        }

        return (framer, recorded);
    }

    private static byte[] Stream(IEnumerable<byte[]> frames)
    {
        var stream = new List<byte>(StreamInfo());
        foreach (var frame in frames)
        {
            stream.AddRange(frame);
        }

        return [.. stream];
    }

    // "fLaC" and one STREAMINFO block (the last metadata block, 34 bytes): 42 bytes.
    private static byte[] StreamInfo() => [.. "fLaC"u8.ToArray(), 0x80, 0, 0, 34, .. new byte[34]];

    private static List<byte[]> Frames(int count) => Enumerable.Range(0, count).Select(Frame).ToList();

    // Fixed block size, 16-bit stereo at 44.1 kHz, the frame number in UTF-8 form, then filler that never
    // contains 0xFF (so the filler cannot hold a sync of its own), then the CRC-16 that closes the frame.
    private static byte[] Frame(int number)
    {
        var frame = new List<byte> { 0xFF, 0xF8, 0x19, 0x18 };
        frame.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(number)));
        frame.Add(Crc8(frame));
        for (var index = 0; frame.Count < FrameBytes - 2; index++)
        {
            frame.Add((byte)((number * 31 + index * 7) % 200));
        }

        var crc = Crc16(frame);
        frame.Add((byte)(crc >> 8));
        frame.Add((byte)crc);
        return [.. frame];
    }

    private static byte Crc8(IEnumerable<byte> bytes)
    {
        byte crc = 0;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 7 : crc << 1);
        }

        return crc;
    }

    private static ushort Crc16(IEnumerable<byte> bytes)
    {
        ushort crc = 0;
        foreach (var value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
        }

        return crc;
    }
}
