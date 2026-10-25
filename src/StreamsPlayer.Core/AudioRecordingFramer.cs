using System.Buffers.Binary;

namespace StreamsPlayer.Core;

/// <summary>
/// Retains bounded container initialization from a listening response. A late recording receives fresh
/// headers and complete Ogg pages; the bytes supplied to the listener are never changed.
/// </summary>
public sealed class AudioRecordingFramer(string? contentType)
{
    public const int MaximumHeaderBytes = 1024 * 1024;

    // S14-1: the longest FLAC frame the end scan will follow (a streamable-subset frame tops out near
    // 16384 samples x 8 channels x 4 bytes = 512 KiB) and the scan steps one Push may spend. A sync that
    // reaches the first without a valid end is skipped, so one corrupt frame costs that frame rather than
    // the whole session; a Push that spends the second is hostile input and is refused.
    private const int MaximumFlacFrameBytes = MaximumHeaderBytes * 3 / 4;
    private const int MaximumScanStepsPerPush = MaximumHeaderBytes * 2;
    private const int NeedMoreBytes = 0;
    private const int NotAFrame = -1;
    private const int WorkCapExceeded = -2;

    private static readonly ushort[] FlacCrc16Table = BuildFlacCrc16Table();

    private byte[] _pending = [];
    private int _flacScan;
    private ushort _flacCrc;
    private readonly List<byte[]> _oggHeaders = [];
    private byte[] _header = [];
    private int _oggPackets;
    private int _headerBytes;
    private long _granule;
    private long _granuleBase;
    private uint _sequence;
    private bool _recording;
    private bool _boundary;
    private long _dataPosition;
    private int _blockAlign = 1;
    private bool _initialized;
    private bool _unsupported;
    private bool _discardFrame;
    private uint? _oggSerial;
    private int _oggHeaderPages;

    public string? Extension { get; private set; }
    public bool Ready => _initialized && !_unsupported;

    public byte[] Begin()
    {
        if (!Ready) throw new InvalidOperationException("The audio header is not ready.");
        _recording = true;
        _boundary = true;
        _granuleBase = _granule;
        _sequence = (uint)_oggHeaderPages;
        _discardFrame = _pending.Length > 1 && _pending[0] == 0xFF;
        return _header.ToArray();
    }

    public void End() => _recording = false;

    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> bytes)
    {
        if (_unsupported || bytes.IsEmpty) return [];
        if (Extension is null || !_initialized || Extension != RecordedAudioFormat.Wav)
        {
            if (_pending.Length + bytes.Length > MaximumHeaderBytes) return Refuse();
            _pending = [.. _pending, .. bytes];
        }

        if (Extension is null)
        {
            if (_pending.Length < RecordedAudioFormat.SniffLength) return [];
            var kind = RecordedAudioFormat.Classify(contentType, null, _pending);
            if (kind.Body != RecordedAudioBody.Audio) return Refuse();
            Extension = kind.Extension;
        }

        if (Extension is RecordedAudioFormat.Ogg or RecordedAudioFormat.Opus) return OggPages();

        if (!_initialized)
        {
            var headerLength = Extension switch
            {
                RecordedAudioFormat.Wav => WaveHeaderLength(),
                RecordedAudioFormat.Flac => FlacHeaderLength(),
                _ => 0
            };
            if (_unsupported || headerLength < 0) return [];
            _header = _pending[..headerLength];
            bytes = _pending.AsSpan(headerLength);
            _pending = [];
            _initialized = true;
            if (Extension == RecordedAudioFormat.Flac) ClearFlacLength();
            if (Extension != RecordedAudioFormat.Wav) _pending = bytes.ToArray();
        }

        if (Extension != RecordedAudioFormat.Wav) return AudioFrames();

        var position = _dataPosition;
        _dataPosition += bytes.Length;
        if (!_recording) return [];
        if (_boundary && Extension == RecordedAudioFormat.Wav)
        {
            var skip = (int)((_blockAlign - position % _blockAlign) % _blockAlign);
            if (skip >= bytes.Length) return [];
            bytes = bytes[skip..];
        }
        _boundary = false;
        return bytes.IsEmpty ? [] : [bytes.ToArray()];
    }

    /// <summary>WAV sizes describe the saved part, rather than the source's declared length.</summary>
    public byte[] FinalHeader(long dataBytes)
    {
        var header = _header.ToArray();
        if (Extension == RecordedAudioFormat.Wav)
        {
            var length = dataBytes - dataBytes % _blockAlign;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)Math.Min(uint.MaxValue, header.Length + length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(header.Length - 4), (uint)Math.Min(uint.MaxValue, length));
        }
        return header;
    }

    public long AlignedDataLength(long bytes) => bytes - bytes % _blockAlign;

    private int WaveHeaderLength()
    {
        if (_pending.Length < 12) return -1;
        if (!_pending.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !_pending.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            Refuse(); return -1;
        }
        var formatSeen = false;
        for (var offset = 12; offset + 8 <= _pending.Length;)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(_pending.AsSpan(offset + 4));
            if (_pending.AsSpan(offset, 4).SequenceEqual("data"u8))
            {
                if (!formatSeen) { Refuse(); return -1; }
                return offset + 8;
            }
            if (size > MaximumHeaderBytes) { Refuse(); return -1; }
            if (offset + 8L + size > _pending.Length) return -1;
            if (_pending.AsSpan(offset, 4).SequenceEqual("fmt "u8))
            {
                if (size < 16) { Refuse(); return -1; }
                var format = BinaryPrimitives.ReadUInt16LittleEndian(_pending.AsSpan(offset + 8));
                // PCM, IEEE float and extensible PCM have independent sample blocks. Compressed WAV does not.
                if (format is not (1 or 3 or 0xFFFE)) { Refuse(); return -1; }
                _blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(_pending.AsSpan(offset + 20));
                if (_blockAlign == 0) { Refuse(); return -1; }
                formatSeen = true;
            }
            offset += 8 + (int)size + (int)(size & 1);
        }
        return -1;
    }

    private int FlacHeaderLength()
    {
        if (!_pending.AsSpan(0, 4).SequenceEqual("fLaC"u8)) { Refuse(); return -1; }
        for (var offset = 4; offset + 4 <= _pending.Length;)
        {
            var size = (_pending[offset + 1] << 16) | (_pending[offset + 2] << 8) | _pending[offset + 3];
            if (size > MaximumHeaderBytes - offset - 4) { Refuse(); return -1; }
            var end = offset + 4 + size;
            if (end > _pending.Length) return -1;
            if ((_pending[offset] & 0x80) != 0) return end;
            offset = end;
        }
        return -1;
    }

    private void ClearFlacLength()
    {
        // STREAMINFO's sample count and MD5 belong to the original stream, not a captured excerpt.
        if (_header.Length < 42 || (_header[4] & 0x7F) != 0) return;
        _header[21] &= 0xF0;
        _header.AsSpan(22, 4).Clear();
        _header.AsSpan(26, 16).Clear();
    }

    private IReadOnlyList<byte[]> AudioFrames()
    {
        var output = new List<byte[]>();
        var offset = 0;
        var budget = MaximumScanStepsPerPush;
        while (offset + 7 <= _pending.Length)
        {
            var frame = _pending.AsSpan(offset);
            int size;
            if (Extension == RecordedAudioFormat.Flac)
            {
                var header = FlacFrameHeader(frame);
                if (header == 0) break;
                if (header < 0) { offset++; continue; }
                size = FlacFrameEnd(frame, header, ref budget);
                if (size == WorkCapExceeded) return Refuse();
                if (size == NotAFrame) { offset++; continue; }
                if (size == NeedMoreBytes) break;
            }
            else if (Extension == RecordedAudioFormat.Aac)
            {
                if (frame[0] != 0xFF || (frame[1] & 0xF6) != 0xF0 || ((frame[2] >> 2) & 15) >= 13)
                { offset++; continue; }
                size = ((frame[3] & 3) << 11) | (frame[4] << 3) | (frame[5] >> 5);
                if (size < 7) { offset++; continue; }
            }
            else
            {
                if (frame[0] != 0xFF || (frame[1] & 0xE0) != 0xE0 || RecordedAudioFormat.MpegFrameLength(frame, 0) is not { } mpegSize)
                { offset++; continue; }
                size = mpegSize;
            }
            if (offset + size > _pending.Length) break;
            if (_recording && !_discardFrame) output.Add(_pending.AsSpan(offset, size).ToArray());
            _discardFrame = false;
            offset += size;
        }
        _pending = _pending[offset..];
        return output;
    }

    // A FLAC header has a CRC of its own: byte-sync alone can also occur in the compressed samples.
    // 0 means more bytes are needed; -1 means this is not a frame header.
    private static int FlacFrameHeader(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6) return 0;
        if (frame[0] != 0xFF || (frame[1] & 0xFE) != 0xF8 || (frame[2] >> 4) == 0 ||
            (frame[2] & 15) == 15 || (frame[3] >> 4) > 10 || (frame[3] & 1) != 0 ||
            ((frame[3] >> 1) & 7) is 3 or 7) return -1;
        var numberBytes = 1;
        if (frame[4] >= 0x80)
        {
            numberBytes = 0;
            for (var mask = 0x80; (frame[4] & mask) != 0; mask >>= 1) numberBytes++;
            if (numberBytes is < 2 or > 7) return -1;
            if (frame.Length < 4 + numberBytes) return 0;
            for (var index = 5; index < 4 + numberBytes; index++) if ((frame[index] & 0xC0) != 0x80) return -1;
        }
        var blockSize = frame[2] >> 4;
        var rate = frame[2] & 15;
        var crcAt = 4 + numberBytes + (blockSize == 6 ? 1 : blockSize == 7 ? 2 : 0) +
            (rate == 12 ? 1 : rate is 13 or 14 ? 2 : 0);
        if (frame.Length <= crcAt) return 0;
        byte crc = 0;
        foreach (var value in frame[..crcAt])
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 7 : crc << 1);
        }
        return crc == frame[crcAt] ? crcAt + 1 : -1;
    }

    // The frame ends where the CRC-16 of everything so far is zero and a valid header follows. The CRC runs
    // forward one byte per step and the position survives a Push (_flacScan, _flacCrc), so a frame is folded
    // once however its bytes arrive. Only a scan that ran out of bytes leaves state behind, and it does so for
    // the frame that AudioFrames keeps at the front of _pending.
    // Returns the frame size, NeedMoreBytes, NotAFrame (no end within MaximumFlacFrameBytes) or WorkCapExceeded.
    private int FlacFrameEnd(ReadOnlySpan<byte> frame, int headerLength, ref int budget)
    {
        var size = _flacScan;
        var crc = _flacCrc;
        if (size == 0)
        {
            size = headerLength;
            crc = 0;
            foreach (var value in frame[..headerLength]) crc = NextFlacCrc16(crc, value);
            budget -= headerLength;
        }

        while (size + 6 <= frame.Length)
        {
            if (crc == 0)
            {
                var next = FlacFrameHeader(frame[size..]);
                if (next > 0) { _flacScan = 0; return size; }
                if (next == 0) break;
            }

            if (size >= MaximumFlacFrameBytes) { _flacScan = 0; return NotAFrame; }
            if (--budget < 0) return WorkCapExceeded;
            crc = NextFlacCrc16(crc, frame[size++]);
        }

        _flacScan = size;
        _flacCrc = crc;
        return NeedMoreBytes;
    }

    // CRC-16 of a FLAC frame: polynomial 0x8005, zero start, no reflection, no final xor.
    private static ushort NextFlacCrc16(ushort crc, byte value) =>
        (ushort)((crc << 8) ^ FlacCrc16Table[(crc >> 8) ^ value]);

    private static ushort[] BuildFlacCrc16Table()
    {
        var table = new ushort[256];
        for (var index = 0; index < table.Length; index++)
        {
            var crc = (ushort)(index << 8);
            for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
            table[index] = crc;
        }
        return table;
    }

    private IReadOnlyList<byte[]> OggPages()
    {
        var output = new List<byte[]>();
        var offset = 0;
        while (offset + 27 <= _pending.Length)
        {
            if (!_pending.AsSpan(offset, 4).SequenceEqual("OggS"u8) || _pending[offset + 4] != 0) return Refuse();
            var segments = _pending[offset + 26];
            if (offset + 27 + segments > _pending.Length) break;
            var size = 27 + segments;
            for (var i = 0; i < segments; i++)
            {
                var lace = _pending[offset + 27 + i];
                size += lace;
            }
            if (offset + size > _pending.Length) break;
            var page = _pending.AsSpan(offset, size).ToArray();
            offset += size;
            var serial = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(14));
            _oggSerial ??= serial;
            if (_oggSerial != serial) return Refuse();
            if (!_initialized)
            {
                var required = Extension == RecordedAudioFormat.Opus ? 2 : 3;
                var headerSegments = 0;
                var headerPayload = 0;
                while (headerSegments < segments && _oggPackets < required)
                {
                    var lace = page[27 + headerSegments++];
                    headerPayload += lace;
                    if (lace < 255) _oggPackets++;
                }
                // A setup packet and the first audio packet may share a page. Retain only initialization.
                var headerPage = page;
                if (headerSegments < segments)
                {
                    headerPage = new byte[27 + headerSegments + headerPayload];
                    page.AsSpan(0, 27).CopyTo(headerPage);
                    headerPage[26] = (byte)headerSegments;
                    page.AsSpan(27, headerSegments).CopyTo(headerPage.AsSpan(27));
                    page.AsSpan(27 + segments, headerPayload).CopyTo(headerPage.AsSpan(27 + headerSegments));
                    headerPage[5] &= 0xFB;
                    BinaryPrimitives.WriteInt64LittleEndian(headerPage.AsSpan(6), 0);
                    headerPage.AsSpan(22, 4).Clear();
                    BinaryPrimitives.WriteUInt32LittleEndian(headerPage.AsSpan(22), OggChecksum(headerPage));
                }
                _headerBytes += headerPage.Length;
                if (_headerBytes > MaximumHeaderBytes) return Refuse();
                _oggHeaders.Add(headerPage);
                if (_oggPackets >= required)
                {
                    _header = [.. _oggHeaders.SelectMany(header => header)];
                    _oggHeaderPages = _oggHeaders.Count;
                    _oggHeaders.Clear();
                    _initialized = true;
                    _granule = Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(6)));
                }
                continue;
            }
            var granule = BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(6));
            if (granule >= 0) _granule = granule;
            if (!_recording) continue;
            if (_boundary && (page[5] & 1) != 0)
            {
                if (granule >= 0) _granuleBase = granule;
                continue;
            }
            _boundary = false;
            page[5] &= 0xFD;
            if (granule >= 0) BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), Math.Max(0, granule - _granuleBase));
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), _sequence++);
            page.AsSpan(22, 4).Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), OggChecksum(page));
            output.Add(page);
        }
        _pending = _pending[offset..];
        return output;
    }

    private static uint OggChecksum(ReadOnlySpan<byte> page)
    {
        uint crc = 0;
        foreach (var value in page)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }

    private IReadOnlyList<byte[]> Refuse()
    {
        _unsupported = true;
        _pending = [];
        return [];
    }
}
