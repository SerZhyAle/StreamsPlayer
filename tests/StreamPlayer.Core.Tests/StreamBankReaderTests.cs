using System.IO.Compression;
using System.Text;
using StreamPlayer.Core;

namespace StreamPlayer.Core.Tests;

public sealed class StreamBankReaderTests
{
    [Fact]
    public void Read_LoadsCsvAndOptionalAtlasFromSameZip()
    {
        using var zip = CreateZip(csvFirst: true, includeAtlas: true);

        var bank = StreamBankReader.Read(zip);

        Assert.True(bank.CsvWasFirstEntry);
        Assert.Single(bank.Entries);
        Assert.Equal([1, 2, 3], bank.FaviconAtlas);
        Assert.Equal(0, bank.MaximumFaviconIndex);
    }

    [Fact]
    public void Read_RejectsBankWhoseCsvIsNotEntryZero()
    {
        using var zip = CreateZip(csvFirst: false, includeAtlas: true);
        Assert.Throws<InvalidDataException>(() => StreamBankReader.Read(zip));
    }

    [Fact]
    public void Read_ToleratesMissingAtlas()
    {
        using var zip = CreateZip(csvFirst: true, includeAtlas: false);
        Assert.Null(StreamBankReader.Read(zip).FaviconAtlas);
    }

    private static MemoryStream CreateZip(bool csvFirst, bool includeAtlas)
    {
        var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (!csvFirst)
            {
                Write(archive.CreateEntry("favicon-atlas.png"), [1, 2, 3]);
            }

            Write(
                archive.CreateEntry("streams.csv"),
                Encoding.UTF8.GetBytes("name,url,media_kind,favicon_index\nOne,https://example.test/live,AUDIO,0"));

            if (csvFirst && includeAtlas)
            {
                Write(archive.CreateEntry("favicon-atlas.png"), [1, 2, 3]);
            }
        }

        result.Position = 0;
        return result;
    }

    private static void Write(ZipArchiveEntry entry, byte[] bytes)
    {
        using var stream = entry.Open();
        stream.Write(bytes);
    }
}
