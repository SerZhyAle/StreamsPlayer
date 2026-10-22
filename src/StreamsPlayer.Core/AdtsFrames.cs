namespace StreamsPlayer.Core;

/// <summary>One ADTS frame's header, as the upload and LAN paths read it (ISO/IEC 14496-3, table 1.16).</summary>
/// <param name="FrameLength">The complete frame including the header, the CRC when present, and the payload.</param>
/// <param name="HeaderLength">7, or 9 when the frame carries a CRC.</param>
/// <param name="SampleRate">The sample rate the sampling-frequency index names; 0 when the index is reserved.</param>
/// <param name="Channels">The channel configuration; 0 means "defined in a PCE", which a forwarder accepts.</param>
public readonly record struct AdtsHeader(int FrameLength, int HeaderLength, int SampleRate, int Channels);

/// <summary>
/// SP-0217: the ADTS frame rules of `DEVICE-EXCHANGE` 7.6 and `LIVE-BROADCAST` section 2.1, as code -
/// a chunk is always whole frames and joinable, a late LAN listener starts at the next frame. The
/// encoder's output is ADTS, so nothing here decodes audio; it only finds frame boundaries.
/// </summary>
public static class AdtsFrames
{
    private static readonly int[] SamplingRates =
        [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350, 0, 0, 0];

    /// <summary>The longest frame an AAC-LC stream of the contract's shapes produces; a header naming more is not a frame.</summary>
    public const int MaximumFrameLength = 64 * 1024;

    public static bool TryReadHeader(ReadOnlySpan<byte> buffer, out AdtsHeader header)
    {
        header = default;
        if (buffer.Length < 7)
        {
            return false;
        }

        if (buffer[0] != 0xFF || (buffer[1] & 0xF0) != 0xF0 || (buffer[1] & 0x06) != 0x00)
        {
            return false;
        }

        var samplingIndex = (buffer[2] >> 2) & 0x0F;
        var protectionAbsent = (buffer[1] & 0x01) != 0;
        var frameLength = ((buffer[3] & 0x03) << 11) | (buffer[4] << 3) | (buffer[5] >> 5);
        // The channel configuration is three bits across bytes 2 and 3; 0 means "defined in a PCE".
        var channels = (buffer[2] & 0x01) << 2 | (buffer[3] >> 6) & 0x03;
        var headerLength = protectionAbsent ? 7 : 9;
        if (frameLength < headerLength || frameLength > MaximumFrameLength)
        {
            return false;
        }

        header = new AdtsHeader(frameLength, headerLength, SamplingRates[samplingIndex], channels);
        return true;
    }
}

/// <summary>
/// Splits a byte flow of ADTS frames into whole frames, holding back a trailing partial frame: the LAN
/// listener and the relay uploader only ever handle frames that end where they say they do. The flow
/// this class is fed is the encoder's own ADTS output, so a resync never needs to hunt for a syncword;
/// a buffer that grows past a bound without one frame is dropped rather than held forever (defence in
/// depth against an encoder that emits something else after a format change).
/// </summary>
public sealed class AdtsFrameStream
{
    private readonly byte[] _buffer = new byte[AdtsFrames.MaximumFrameLength + 4096];
    private int _length;

    /// <summary>Appends bytes of any framing; only the frames the buffer completes become readable.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > _buffer.Length - _length)
        {
            // Never a frame boundary in this much data: keep nothing rather than grow without end.
            _length = 0;
            return;
        }

        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    /// <summary>The next whole frame, or false when the buffer holds only a partial one.</summary>
    public bool TryReadFrame(out ReadOnlyMemory<byte> frame)
    {
        while (_length >= 7)
        {
            var span = _buffer.AsSpan(0, _length);
            if (AdtsFrames.TryReadHeader(span, out var header))
            {
                if (span.Length < header.FrameLength)
                {
                    // A partial frame is held, never dropped: the encoder's flow is frame-aligned, so
                    // the rest of this frame is the next thing that arrives.
                    break;
                }

                var whole = span[..header.FrameLength].ToArray();
                Consume(header.FrameLength);
                frame = whole;
                return true;
            }

            // The head is genuinely not a frame: resync at the next syncword. Without one, keep a
            // two-byte tail - a header split across appends still reads when its rest arrives.
            var sync = -1;
            for (var index = 1; index + 1 < _length; index++)
            {
                if (span[index] == 0xFF && (span[index + 1] & 0xF6) == 0xF0)
                {
                    sync = index;
                    break;
                }
            }

            Consume(sync >= 0 ? sync : Math.Max(1, _length - 2));
        }

        frame = default;
        return false;
    }

    private void Consume(int count)
    {
        _buffer.AsSpan(count, _length - count).CopyTo(_buffer);
        _length -= count;
    }
}
