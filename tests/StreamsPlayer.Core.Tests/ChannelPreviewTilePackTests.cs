using System.IO.Compression;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0091, STREAM-BANK item G2. The pack replaced a sprite sheet whose row count now changes on
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

    // The gap case, and the reason it is not an error: a slot with no entry means the publisher shipped
    // a coords row it had no capture for. One channel without a picture, not a broken import - and
    // since SP-0160 the coords hash can no longer prove the pair whole, so the reader must tolerate the
    // gap either way.
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

    // SP-0160, STREAM-BANK item L's structural gate, bounded per tile: one entry declaring more than
    // the per-tile ceiling refuses the pack as a whole before any tile is read - a mis-published pack
    // can no longer end an import part-way with an out-of-memory failure.
    [Fact]
    public void Open_RefusesAPackWhoseEntryDeclaresMoreThanThePerTileCeiling()
    {
        var over = new string('x', (int)ChannelPreviewTilePack.MaximumTileBytes + 1);

        var error = Assert.Throws<InvalidDataException>(
            () => ChannelPreviewTilePack.Open(Zip(("0", "small"), ("1", over))));

        Assert.Contains("Tile 1", error.Message, StringComparison.Ordinal);
        Assert.Contains("per-tile ceiling", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_AcceptsAnEntryExactlyAtThePerTileCeiling()
    {
        var at = new string('x', (int)ChannelPreviewTilePack.MaximumTileBytes);

        using var pack = ChannelPreviewTilePack.Open(Zip(("0", at)));

        Assert.Equal(1, pack.Count);
    }

    // The ceiling gates the tiles this class reads, not the tolerated bystanders: a README beside the
    // tiles is never opened, so its declared size cannot cost memory here.
    [Fact]
    public void Open_GatesOnlyTheTileEntries()
    {
        var over = new string('x', (int)ChannelPreviewTilePack.MaximumTileBytes + 1);

        using var pack = ChannelPreviewTilePack.Open(Zip(("README.md", over), ("0", "small")));

        Assert.Equal(1, pack.Count);
        Assert.Equal("small", Text(pack.Read(0)));
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
