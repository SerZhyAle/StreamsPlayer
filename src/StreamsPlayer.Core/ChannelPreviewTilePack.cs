using System.Globalization;
using System.IO.Compression;

namespace StreamsPlayer.Core;

/// <summary>
/// SP-0091, source contract item G2: the primary channel-preview read path - one ZIP whose entries are
/// the individual tile images, named by slot index.
/// </summary>
/// <remarks>
/// <para>Container contract: STORED (uncompressed) entries, each named as the slot index in plain
/// decimal with no extension; the <c>url -&gt; index</c> sidecar is shared with the sprite sheet, so the
/// index space is the same one <see cref="ChannelPreviewCoords"/> already resolves. Measured against the
/// live artifact on 2026-08-20: 2 723 entries, all STORED, names <c>0</c>..<c>2722</c>, each a RIFF/WEBP
/// file.</para>
/// <para>Why this and not the sheet. A sprite sheet is not randomly addressable, so reading one tile
/// costs a share of decoding the whole thing - and this consumer needs a *subset*: the tiles whose
/// channel is in the local catalog and has no captured frame yet. On the sheet that meant paying a
/// 61,7 Mpx decode (measured +265..491 MB working set, ~1,5 s) before the first tile, whether it wanted
/// 2 000 tiles or two, and the sheet keeps growing. Here the cost is the entries actually read.</para>
/// <para>It also deletes a whole class of silent corruption. The sheet's height follows its tile count
/// and now changes on every rebuild, so a consumer slicing it has to derive geometry from the decoded
/// image or hand out plausible pictures cut from the wrong rectangle. A pack has no geometry to get
/// wrong: an index either names an entry or it does not.</para>
/// <para>Not thread-safe: <see cref="ZipArchive"/> entry reads share the underlying stream, so one
/// instance belongs to one thread.</para>
/// </remarks>
public sealed class ChannelPreviewTilePack : IDisposable
{
    private readonly ZipArchive _archive;
    private readonly Dictionary<int, ZipArchiveEntry> _tiles;

    private ChannelPreviewTilePack(ZipArchive archive, Dictionary<int, ZipArchiveEntry> tiles)
    {
        _archive = archive;
        _tiles = tiles;
    }

    /// <summary>
    /// Opens the pack over its downloaded bytes. Entries whose name is not a plain non-negative decimal
    /// are ignored rather than rejected, so the publisher can add a README beside the tiles without
    /// breaking this reader.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a readable ZIP.</exception>
    public static ChannelPreviewTilePack Open(byte[] pack)
    {
        ArgumentNullException.ThrowIfNull(pack);

        var archive = new ZipArchive(new MemoryStream(pack, writable: false), ZipArchiveMode.Read);
        try
        {
            var tiles = new Dictionary<int, ZipArchiveEntry>();
            foreach (var entry in archive.Entries)
            {
                // FullName, not Name: a nested "extra/7" would present as "7" and quietly shadow the real
                // tile 7. NumberStyles.None rejects signs, spaces and separators, so only a bare decimal
                // is accepted as a slot.
                if (int.TryParse(entry.FullName, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    tiles[index] = entry;
                }
            }

            return new ChannelPreviewTilePack(archive, tiles);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    /// <summary>How many slots this pack carries.</summary>
    public int Count => _tiles.Count;

    /// <summary>
    /// The slot indices this pack carries, in no particular order. Exposed because the slots are not
    /// promised to be contiguous: a caller wanting "any tile" must ask for one that exists rather than
    /// guess <c>0</c>.
    /// </summary>
    public IReadOnlyCollection<int> Slots => _tiles.Keys;

    /// <summary>
    /// The bytes of one tile, or <c>null</c> when the pack has no such slot. A sidecar index with no
    /// entry is a gap to skip, never a fault: the two files are verified as a pair before they get here,
    /// so a missing slot means the publisher shipped a coords row it had no capture for.
    /// </summary>
    /// <exception cref="InvalidDataException">The entry is present but unreadable.</exception>
    public byte[]? Read(int index)
    {
        if (!_tiles.TryGetValue(index, out var entry))
        {
            return null;
        }

        try
        {
            var bytes = new byte[entry.Length];
            using var stream = entry.Open();
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException(
                $"Tile {index} declares {entry.Length} bytes but the pack ends early.", exception);
        }
    }

    public void Dispose() => _archive.Dispose();
}
