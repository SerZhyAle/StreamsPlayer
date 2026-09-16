using System.Buffers.Binary;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0098: pure-Core, zero-dependency PNG header inspection. Validates signature and IHDR chunk
/// to determine atlas usability and exact pixel bounds without decoding raster data.
/// </summary>
public static class PngHeader
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool TryReadDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        // Signature (8) + IHDR length (4) + IHDR chunk type (4) + Width (4) + Height (4)
        if (bytes.Length < 24)
        {
            return false;
        }

        if (!bytes[..8].SequenceEqual(PngSignature))
        {
            return false;
        }

        // IHDR chunk type: "IHDR" = 0x49, 0x48, 0x44, 0x52
        if (bytes[12] != 0x49 || bytes[13] != 0x48 || bytes[14] != 0x44 || bytes[15] != 0x52)
        {
            return false;
        }

        var chunkLength = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(8, 4));
        if (chunkLength < 8)
        {
            return false;
        }

        width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));

        return width > 0 && height > 0;
    }
}
