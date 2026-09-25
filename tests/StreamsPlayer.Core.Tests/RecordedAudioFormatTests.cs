using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0121: a radio recording is named from what the station sent (C-09). The bytes decide before the header,
/// a playlist is recognised rather than recorded, and an HLS manifest is refused rather than copied.
/// </summary>
public sealed class RecordedAudioFormatTests
{
    // MPEG-1 layer III, 128 kbps, 44.1 kHz, no padding: 417-byte frames.
    private static byte[] Mp3Frames(int count, int offset = 0)
    {
        var data = new byte[offset + 417 * count + 4];
        for (var i = 0; i < count; i++)
        {
            var at = offset + 417 * i;
            data[at] = 0xFF;
            data[at + 1] = 0xFB;
            data[at + 2] = 0x90;
            data[at + 3] = 0x64;
        }

        return data;
    }

    // ADTS AAC frames of 200 bytes each.
    private static byte[] AdtsFrames(int count)
    {
        const int length = 200;
        var data = new byte[length * count + 8];
        for (var i = 0; i < count; i++)
        {
            var at = length * i;
            data[at] = 0xFF;
            data[at + 1] = 0xF1;
            data[at + 2] = 0x50;
            data[at + 3] = (byte)(0x80 | ((length >> 11) & 0x03));
            data[at + 4] = (byte)((length >> 3) & 0xFF);
            data[at + 5] = (byte)(((length & 0x07) << 5) | 0x1F);
            data[at + 6] = 0xFC;
        }

        return data;
    }

    [Fact]
    public void Mp3FramesAreMp3() =>
        Assert.Equal(RecordedAudioKind.Of(".mp3"), RecordedAudioFormat.Classify("audio/mpeg", null, Mp3Frames(3)));

    [Fact]
    public void Mp3JoinedMidFrameIsStillMp3() =>
        Assert.Equal(".mp3", RecordedAudioFormat.Classify(null, null, Mp3Frames(3, offset: 91)).Extension);

    [Fact]
    public void AdtsSentAsAudioMpegIsAac() =>
        Assert.Equal(".aac", RecordedAudioFormat.Classify("audio/mpeg", null, AdtsFrames(4)).Extension);

    [Fact]
    public void Id3TagIsMp3() =>
        Assert.Equal(".mp3", RecordedAudioFormat.Classify("application/octet-stream", null, "ID3\u0004\0\0\0\0\0\0"u8).Extension);

    [Fact]
    public void OggVorbisIsOgg()
    {
        var head = Encoding.ASCII.GetBytes("OggS\0\u0002\0\0\0\0\0\0\0\0\u0001vorbis");
        Assert.Equal(".ogg", RecordedAudioFormat.Classify("audio/ogg", null, head).Extension);
    }

    [Fact]
    public void OggOpusIsOpus()
    {
        var head = Encoding.ASCII.GetBytes("OggS\0\u0002\0\0\0\0\0\0\0\0\u0001OpusHead");
        Assert.Equal(".opus", RecordedAudioFormat.Classify("audio/ogg", null, head).Extension);
    }

    [Fact]
    public void FlacIsFlac() =>
        Assert.Equal(".flac", RecordedAudioFormat.Classify(null, null, "fLaC\0\0\0\u0010"u8).Extension);

    [Theory]
    [InlineData("audio/aacp", ".aac")]
    [InlineData("audio/aac; charset=binary", ".aac")]
    [InlineData("AUDIO/MPEG", ".mp3")]
    [InlineData("application/ogg", ".ogg")]
    public void UnrecognisedBytesFallBackToTheHeader(string contentType, string expected)
    {
        var noise = new byte[64];
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] = (byte)(i * 7 + 1 | 0x01);
        }

        Assert.Equal(expected, RecordedAudioFormat.Classify(contentType, null, noise).Extension);
    }

    [Fact]
    public void NothingRecognisableIsUnknown()
    {
        var noise = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x10, 0x11 };
        Assert.Equal(RecordedAudioBody.Unknown, RecordedAudioFormat.Classify("application/octet-stream", null, noise).Body);
    }

    [Theory]
    [InlineData("audio/x-scpls", "[playlist]\nFile1=http://a.example/stream\n")]
    [InlineData("text/plain", "[playlist]\r\nNumberOfEntries=1\r\nFile1=http://a.example/stream\r\n")]
    [InlineData("audio/x-mpegurl", "#EXTM3U\n#EXTINF:-1,Station\nhttp://a.example/stream\n")]
    [InlineData("text/html", "http://a.example/stream\n")]
    public void PlaylistBodyIsAPlaylist(string contentType, string body) =>
        Assert.Equal(RecordedAudioBody.Playlist, RecordedAudioFormat.Classify(contentType, "http://a.example/listen", Encoding.UTF8.GetBytes(body)).Body);

    [Fact]
    public void HlsManifestIsRefused()
    {
        var body = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\nseg1.aac\n");
        Assert.Equal(RecordedAudioBody.HlsManifest, RecordedAudioFormat.Classify("application/vnd.apple.mpegurl", null, body).Body);
    }

    [Fact]
    public void BinaryStreamBehindAPlaylistSuffixIsStillAudio() =>
        Assert.Equal(".mp3", RecordedAudioFormat.Classify("audio/mpeg", "http://a.example/live.m3u", Mp3Frames(3)).Extension);
}
