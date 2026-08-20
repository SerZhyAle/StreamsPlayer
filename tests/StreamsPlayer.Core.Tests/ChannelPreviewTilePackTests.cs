using System.IO.Compression;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0091, source contract item G2. The pack replaced a sprite sheet whose row count now changes on
/// every rebuild, so these fix the two properties that make it safer: a slot either exists or it does
/// not, and a name that is not a slot is not treated as one.
/// </summary>
public sealed class ChannelPreviewTilePackTests
{
    [Fact]
    public void Open_ReadsSlotsByTheirDecimalEntryName()
    {
        using var pack = ChannelPreviewTilePack.Open(Zip(("0", "zero"), ("1", "one"), ("2722", "last")));

        Assert.Equal(3, pack.Count);
        Assert.Equal("zero", Text(pack.Read(0)));
        Assert.Equal("one", Text(pack.Read(1)));
        Assert.Equal("last", Text(pack.Read(2722)));
    }

    // The gap case, and the reason it is not an error: the sidecar and the pack are verified as a pair
    // before they get here, so a slot with no entry means the publisher had no capture for that channel.
    // It is one channel without a picture, not a broken import.
    [Fact]
    public void Read_ReturnsNullForASlotThePackDoesNotCarry()
    {
        using var pack = ChannelPreviewTilePack.Open(Zip(("7", "seven")));

        Assert.Null(pack.Read(8));
        Assert.Null(pack.Read(-1));
    }

    // Slots are not promised to be contiguous, and a caller wanting "any tile" - the codec probe - must
    // ask for one that exists rather than assume 0.
    [Fact]
    public void Slots_ReportsWhatIsActuallyThereRatherThanARange()
    {
        using var pack = ChannelPreviewTilePack.Open(Zip(("100", "a"), ("200", "b")));

        Assert.Equal([100, 200], pack.Slots.Order());
        Assert.Null(pack.Read(0));
    }

    [Fact]
    public void Open_IgnoresEntriesThatAreNotSlots()
    {
        // A README beside the tiles must not break the reader, and a nested "extra/7" must not shadow the
        // real slot 7 - ZipArchiveEntry.Name would report both as "7".
        using var pack = ChannelPreviewTilePack.Open(
            Zip(("7", "real"), ("README.md", "notes"), ("extra/7", "impostor"), ("+7", "signed"), ("07 ", "padded")));

        Assert.Equal(1, pack.Count);
        Assert.Equal("real", Text(pack.Read(7)));
    }

    [Fact]
    public void Open_RefusesBytesThatAreNotAZip()
    {
        Assert.Throws<InvalidDataException>(
            () => ChannelPreviewTilePack.Open(Encoding.UTF8.GetBytes("this is not a zip")));
    }

    /// <summary>
    /// A pack of STORED entries, matching the published container: no compression, entry name is the slot
    /// index as a plain decimal.
    /// </summary>
    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }

    private static string? Text(byte[]? bytes) => bytes is null ? null : Encoding.UTF8.GetString(bytes);
}
