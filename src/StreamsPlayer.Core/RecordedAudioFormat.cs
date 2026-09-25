namespace StreamsPlayer.Core;

/// <summary>What the first bytes of a station's response turned out to be.</summary>
public enum RecordedAudioBody
{
    /// <summary>Audio in a format a file extension can name.</summary>
    Audio,

    /// <summary>A PLS or M3U list pointing at the real stream - follow it, never record it.</summary>
    Playlist,

    /// <summary>An HLS manifest: segmented media a plain byte copy cannot record.</summary>
    HlsManifest,

    /// <summary>Neither the server's content type nor the bytes identify the audio.</summary>
    Unknown
}

/// <summary>The verdict on a station response: what it is and, for audio, the extension its file takes.</summary>
public readonly record struct RecordedAudioKind(RecordedAudioBody Body, string? Extension)
{
    public static RecordedAudioKind Of(string extension) => new(RecordedAudioBody.Audio, extension);
}

/// <summary>
/// SP-0121: names a radio recording from what the station actually sent. The recorder copies the stream's bytes
/// unchanged, so the extension is the only thing that tells a player how to open the file - a fixed <c>.mp3</c>
/// mislabelled every AAC and Ogg station and stored a station given as a playlist link as a few hundred bytes of
/// playlist text. Platform-neutral so every rule here is tested.
/// <para>The bytes win over the header. Stations routinely send <c>audio/mpeg</c> for AAC, and a generic
/// <c>application/octet-stream</c> is common; the header decides only when the bytes carry no signature this
/// knows, and the bytes of a stream can start mid-frame, so the frame-sync scan looks past the first byte.</para>
/// </summary>
public static class RecordedAudioFormat
{
    /// <summary>How many leading bytes <see cref="Classify"/> wants to see; fewer is accepted.</summary>
    public const int SniffLength = 4096;

    public const string Mp3 = ".mp3";
    public const string Aac = ".aac";
    public const string Ogg = ".ogg";
    public const string Opus = ".opus";
    public const string Flac = ".flac";
    public const string Wav = ".wav";

    /// <summary>
    /// Classifies a station response from its content type, its address and its first bytes.
    /// </summary>
    public static RecordedAudioKind Classify(string? contentType, string? address, ReadOnlySpan<byte> head)
    {
        var mediaType = MediaType(contentType);
        if (LooksLikeText(head))
        {
            var text = System.Text.Encoding.UTF8.GetString(head);
            if (text.Contains("#EXT-X-", StringComparison.OrdinalIgnoreCase))
            {
                return new(RecordedAudioBody.HlsManifest, null);
            }

            if (IsPlaylistMediaType(mediaType) || HasPlaylistSuffix(address) || StationPlaylist.LooksLikePlaylist(text))
            {
                return new(RecordedAudioBody.Playlist, null);
            }
        }

        if (FromSignature(head) is { } signature)
        {
            return RecordedAudioKind.Of(signature);
        }

        if (FromMediaType(mediaType) is { } declared)
        {
            return RecordedAudioKind.Of(declared);
        }

        return new(RecordedAudioBody.Unknown, null);
    }

    private static string MediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        var semicolon = contentType.IndexOf(';');
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim().ToLowerInvariant();
    }

    private static bool IsPlaylistMediaType(string mediaType) => mediaType is
        "audio/x-scpls" or "application/pls+xml" or "audio/scpls"
        or "audio/x-mpegurl" or "audio/mpegurl" or "application/x-mpegurl" or "application/vnd.apple.mpegurl";

    private static bool HasPlaylistSuffix(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return path.EndsWith(".pls", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FromMediaType(string mediaType) => mediaType switch
    {
        "audio/mpeg" or "audio/mp3" or "audio/mpeg3" or "audio/x-mpeg" => Mp3,
        "audio/aac" or "audio/aacp" or "audio/x-aac" => Aac,
        "audio/ogg" or "application/ogg" or "audio/x-ogg" or "audio/vorbis" => Ogg,
        "audio/opus" => Opus,
        "audio/flac" or "audio/x-flac" => Flac,
        "audio/wav" or "audio/x-wav" or "audio/wave" => Wav,
        _ => null
    };

    private static string? FromSignature(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("ID3"u8))
        {
            return Mp3;
        }

        if (head.StartsWith("OggS"u8))
        {
            // An Ogg stream says what it carries in its first page; Opus is worth its own extension because a
            // player that sees .ogg may expect Vorbis.
            return head.IndexOf("OpusHead"u8) >= 0 ? Opus : Ogg;
        }

        if (head.StartsWith("fLaC"u8))
        {
            return Flac;
        }

        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return Wav;
        }

        return FromFrameSync(head);
    }

    /// <summary>
    /// MPEG audio and ADTS share the 12-bit sync word and differ in the layer field: ADTS (AAC) always has layer
    /// 00, MPEG audio never does. One lucky pair of bytes is not evidence, so a candidate counts only when a
    /// second sync of the same kind follows at the frame length its header states.
    /// </summary>
    private static string? FromFrameSync(ReadOnlySpan<byte> head)
    {
        for (var i = 0; i + 7 <= head.Length; i++)
        {
            if (head[i] != 0xFF || (head[i + 1] & 0xE0) != 0xE0)
            {
                continue;
            }

            if ((head[i + 1] & 0xF6) == 0xF0)
            {
                var length = ((head[i + 3] & 0x03) << 11) | (head[i + 4] << 3) | (head[i + 5] >> 5);
                if (length >= 7 && IsAdtsAt(head, i + length))
                {
                    return Aac;
                }

                continue;
            }

            if (MpegFrameLength(head, i) is { } frame && IsMpegAt(head, i + frame))
            {
                return Mp3;
            }
        }

        return null;
    }

    private static bool IsAdtsAt(ReadOnlySpan<byte> head, int at) =>
        at + 1 < head.Length && head[at] == 0xFF && (head[at + 1] & 0xF6) == 0xF0;

    private static bool IsMpegAt(ReadOnlySpan<byte> head, int at) =>
        at + 1 < head.Length && head[at] == 0xFF && (head[at + 1] & 0xE0) == 0xE0 && (head[at + 1] & 0x06) != 0;

    private static readonly int[] Mpeg1Layer3Kbps = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] Mpeg2Layer3Kbps = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
    private static readonly int[] Mpeg1Rates = [44100, 48000, 32000, 0];

    /// <summary>Frame length of an MPEG layer III header at <paramref name="at"/>, or null when it is not one.</summary>
    private static int? MpegFrameLength(ReadOnlySpan<byte> head, int at)
    {
        if (at + 3 >= head.Length)
        {
            return null;
        }

        var version = (head[at + 1] >> 3) & 0x03; // 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5, 1 = reserved
        var layer = (head[at + 1] >> 1) & 0x03;   // 1 = layer III
        var bitrateIndex = head[at + 2] >> 4;
        var rateIndex = (head[at + 2] >> 2) & 0x03;
        var padding = (head[at + 2] >> 1) & 0x01;
        if (version == 1 || layer != 1 || rateIndex == 3)
        {
            return null;
        }

        var kbps = version == 3 ? Mpeg1Layer3Kbps[bitrateIndex] : Mpeg2Layer3Kbps[bitrateIndex];
        var rate = Mpeg1Rates[rateIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
        if (kbps == 0 || rate == 0)
        {
            return null;
        }

        var samplesFactor = version == 3 ? 144 : 72;
        return samplesFactor * kbps * 1000 / rate + padding;
    }

    /// <summary>
    /// True when the head is plain text - no control bytes other than whitespace. A playlist or a manifest is
    /// text; audio is not, and this keeps a binary stream that happens to contain "[playlist]" from being read as one.
    /// </summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.IsEmpty)
        {
            return false;
        }

        foreach (var b in head)
        {
            if (b < 0x20 && b is not (byte)'\r' and not (byte)'\n' and not (byte)'\t')
            {
                return false;
            }
        }

        return true;
    }
}
