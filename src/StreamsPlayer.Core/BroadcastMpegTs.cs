namespace StreamsPlayer.Core;

/// <summary>One access unit on the shared LAN/relay pipeline. Only a video IDR, or a whole audio frame, is joinable.</summary>
public sealed record BroadcastMediaChunk(byte[] Payload, bool Joinable);

/// <summary>LIVE-BROADCAST C/H: one H.264 access unit per PES, AAC in ADTS, repeated program tables and a 90 kHz clock.</summary>
public sealed class BroadcastMpegTs(bool audio)
{
    public const int PacketSize = 188;
    public const int VideoPid = 0x100;
    public const int AudioPid = 0x101;
    public const int ProgramMapPid = 0x1000;
    public const int MaximumAccessUnitBytes = 800_000;
    private readonly byte[] _continuity = new byte[8192];
    private long _lastTables = -1;
    private readonly object _gate = new();

    /// <summary>The upload's format header. Keyframe chunks repeat these tables for a late LAN listener too.</summary>
    public byte[] Header()
    {
        lock (_gate)
        {
            return Tables();
        }
    }

    public BroadcastMediaChunk Video(ReadOnlySpan<byte> annexB, long time100ns, bool keyframe)
    {
        if (annexB.Length is 0 or > MaximumAccessUnitBytes || time100ns < 0)
            throw new ArgumentOutOfRangeException(nameof(annexB));
        lock (_gate)
        {
            using var output = new MemoryStream();
            // Repeating on every picture also holds the 250 ms bound at variable capture rates.
            output.Write(Tables());
            _lastTables = time100ns;
            var pts = time100ns * 9 / 1000;
            WritePes(output, VideoPid, 0xe0, annexB, pts, keyframe, pcr: true);
            var bytes = output.ToArray();
            if (bytes.Length + ExchangeSealedRelayRecords.MediaOverhead > ExchangeUploadChunks.MaximumPayload)
                throw new InvalidDataException("A video access unit exceeds the sealed upload bound.");
            return new(bytes, keyframe);
        }
    }

    public BroadcastMediaChunk Audio(ReadOnlySpan<byte> adts, long time100ns)
    {
        if (!audio || adts.Length is 0 or > 65527 || time100ns < 0)
            throw new ArgumentOutOfRangeException(nameof(adts));
        lock (_gate)
        {
            using var output = new MemoryStream();
            if (_lastTables < 0 || time100ns - _lastTables >= 2_000_000)
            {
                output.Write(Tables());
                _lastTables = time100ns;
            }
            WritePes(output, AudioPid, 0xc0, adts, time100ns * 9 / 1000, randomAccess: false, pcr: false);
            return new(output.ToArray(), false);
        }
    }

    private byte[] Tables()
    {
        using var output = new MemoryStream();
        WriteSection(output, 0, [0x00, 0xb0, 0x0d, 0x00, 0x01, 0xc1, 0x00, 0x00, 0x00, 0x01, 0xf0, 0x00]);
        byte length = audio ? (byte)0x17 : (byte)0x12;
        var section = new List<byte>
        {
            0x02, 0xb0, length, 0x00, 0x01, 0xc1, 0x00, 0x00,
            0xe1, 0x00, 0xf0, 0x00, 0x1b, 0xe1, 0x00, 0xf0, 0x00
        };
        if (audio) section.AddRange([0x0f, 0xe1, 0x01, 0xf0, 0x00]);
        WriteSection(output, ProgramMapPid, section.ToArray());
        return output.ToArray();
    }

    private void WriteSection(Stream output, int pid, byte[] section)
    {
        var packet = Enumerable.Repeat((byte)0xff, PacketSize).ToArray();
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | pid >> 8);
        packet[2] = (byte)pid;
        packet[3] = (byte)(0x10 | Next(pid));
        packet[4] = 0;
        section.CopyTo(packet, 5);
        uint crc = 0xffffffff;
        foreach (var value in section)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? crc << 1 ^ 0x04c11db7 : crc << 1;
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5 + section.Length), crc);
        output.Write(packet);
    }

    private void WritePes(Stream output, int pid, byte streamId, ReadOnlySpan<byte> payload,
        long timestamp, bool randomAccess, bool pcr)
    {
        var pes = new byte[payload.Length + 14];
        pes[2] = 1;
        pes[3] = streamId;
        var length = pid == VideoPid ? 0 : payload.Length + 8;
        pes[4] = (byte)(length >> 8);
        pes[5] = (byte)length;
        pes[6] = 0x80;
        pes[7] = 0x80;
        pes[8] = 5;
        var pts = (ulong)timestamp & 0x1ffffffff;
        pes[9] = (byte)(0x21 | ((pts >> 30) & 7) << 1);
        pes[10] = (byte)(pts >> 22);
        pes[11] = (byte)((pts >> 14) & 0xfe | 1);
        pes[12] = (byte)(pts >> 7);
        pes[13] = (byte)((pts << 1) & 0xfe | 1);
        payload.CopyTo(pes.AsSpan(14));
        var offset = 0;
        while (offset < pes.Length)
        {
            var first = offset == 0;
            var requiredAdaptation = first && pcr ? 8 : 0;
            var take = Math.Min(184 - requiredAdaptation, pes.Length - offset);
            var adaptation = 184 - take;
            var packet = Enumerable.Repeat((byte)0xff, PacketSize).ToArray();
            packet[0] = 0x47;
            packet[1] = (byte)((first ? 0x40 : 0) | pid >> 8);
            packet[2] = (byte)pid;
            packet[3] = (byte)((adaptation > 0 ? 0x30 : 0x10) | Next(pid));
            if (adaptation > 0)
            {
                packet[4] = (byte)(adaptation - 1);
                if (adaptation > 1) packet[5] = (byte)((first && randomAccess ? 0x40 : 0) | (first && pcr ? 0x10 : 0));
                if (first && pcr)
                {
                    packet[6] = (byte)(pts >> 25);
                    packet[7] = (byte)(pts >> 17);
                    packet[8] = (byte)(pts >> 9);
                    packet[9] = (byte)(pts >> 1);
                    packet[10] = (byte)((pts & 1) << 7 | 0x7e);
                    packet[11] = 0;
                }
            }
            pes.AsSpan(offset, take).CopyTo(packet.AsSpan(4 + adaptation));
            output.Write(packet);
            offset += take;
        }
    }

    private byte Next(int pid)
    {
        var result = _continuity[pid];
        _continuity[pid] = (byte)((result + 1) & 15);
        return result;
    }
}
