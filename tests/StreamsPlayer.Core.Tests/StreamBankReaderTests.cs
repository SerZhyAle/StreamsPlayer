using System.IO.Compression;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class StreamBankReaderTests
{
    private static readonly byte[] ValidPngAtlas =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D,
        0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x20,
        0x00, 0x00, 0x00, 0x20,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    [Fact]
    public void Read_LoadsCsvAndOptionalAtlasFromSameZip()
    {
        using var zip = CreateZip(csvFirst: true, atlasBytes: ValidPngAtlas);

        var bank = StreamBankReader.Read(zip);

        Assert.True(bank.CsvWasFirstEntry);
        Assert.Single(bank.Entries);
        Assert.Equal(ValidPngAtlas, bank.FaviconAtlas);
        Assert.Equal(0, bank.MaximumFaviconIndex);
    }

    [Fact]
    public void Read_RejectsBankWhoseCsvIsNotEntryZero()
    {
        using var zip = CreateZip(csvFirst: false, atlasBytes: ValidPngAtlas);
        Assert.Throws<InvalidDataException>(() => StreamBankReader.Read(zip));
    }

    [Fact]
    public void Read_ToleratesMissingAtlas()
    {
        using var zip = CreateZip(csvFirst: true, atlasBytes: null);
        Assert.Null(StreamBankReader.Read(zip).FaviconAtlas);
    }

    [Fact]
    public void Read_HandlesReorderedAndExtraColumns()
    {
        const string csv = "extra_info,url,topic,name,favicon_index\nIgnored,https://example.test/reordered,News,Reordered Station,42";
        using var zip = CreateZip(csvFirst: true, atlasBytes: null, csvContent: csv);

        var bank = StreamBankReader.Read(zip);

        var entry = Assert.Single(bank.Entries);
        Assert.Equal("Reordered Station", entry.Title);
        Assert.Equal("https://example.test/reordered", entry.Url);
        Assert.Equal("News", entry.Topic);
        Assert.Equal(42, entry.FaviconIndex);
    }

    [Fact]
    public void Read_HandlesMissingOptionalColumns()
    {
        const string csv = "name,url\nMinimal Station,https://example.test/minimal";
        using var zip = CreateZip(csvFirst: true, atlasBytes: null, csvContent: csv);

        var bank = StreamBankReader.Read(zip);

        var entry = Assert.Single(bank.Entries);
        Assert.Equal("Minimal Station", entry.Title);
        Assert.Equal("https://example.test/minimal", entry.Url);
        Assert.Null(entry.FaviconIndex);
    }

    [Fact]
    public void Read_DiscardsAtlasWhenNonPngBytes()
    {
        using var zip = CreateZip(csvFirst: true, atlasBytes: [1, 2, 3, 4, 5]);

        var bank = StreamBankReader.Read(zip);

        Assert.Single(bank.Entries);
        Assert.Null(bank.FaviconAtlas);
    }

    // SP-0184 (A16-3): the header, not the byte count, declares what a decode will allocate.
    [Theory]
    [InlineData(100_000, 100_000)]
    [InlineData(16_384, 16_384)]
    public void Read_DropsAnAtlasWhoseHeaderDeclaresMorePixelsThanTheCeiling(int width, int height)
    {
        using var zip = CreateZip(csvFirst: true, atlasBytes: PngHeaderOnly(width, height));

        var bank = StreamBankReader.Read(zip);

        Assert.Single(bank.Entries);
        Assert.Null(bank.FaviconAtlas);
    }

    [Fact]
    public void Read_KeepsAnAtlasTheSizeOfAFullBank()
    {
        // 16 tiles of 32 pixels per row, one row per 16 channels: 100 000 channels.
        var atlas = PngHeaderOnly(512, 200_000);
        using var zip = CreateZip(csvFirst: true, atlasBytes: atlas);

        Assert.Equal(atlas, StreamBankReader.Read(zip).FaviconAtlas);
    }

    private static byte[] PngHeaderOnly(int width, int height)
    {
        var bytes = (byte[])ValidPngAtlas.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }
    [Fact]
    public void Read_RejectsInvalidUtf8Bytes()
    {
        var invalidUtf8 = new byte[]
        {
            (byte)'n', (byte)'a', (byte)'m', (byte)'e', (byte)',', (byte)'u', (byte)'r', (byte)'l', (byte)'\n',
            0xFF, 0xFE, 0xFD
        };

        using var zip = CreateZipFromBytes(csvFirst: true, csvBytes: invalidUtf8, atlasBytes: null);
        var failure = Assert.Throws<InvalidDataException>(() => StreamBankReader.Read(zip));
        Assert.IsAssignableFrom<DecoderFallbackException>(failure.InnerException);
    }

    [Fact]
    public void Read_RejectsMalformedCsvQuotes()
    {
        const string brokenCsv = "name,url\n\"Unclosed quote,https://example.test/broken";
        using var zip = CreateZip(csvFirst: true, atlasBytes: null, csvContent: brokenCsv);

        var failure = Assert.Throws<InvalidDataException>(() => StreamBankReader.Read(zip));
        Assert.IsAssignableFrom<FormatException>(failure.InnerException);
    }

    // SP-0184 S11-1: callers map "too big" by this type, not by the message text.
    [Fact]
    public void Read_RejectsAnOversizedCsvWithTheLimitException()
    {
        var oversized = new byte[StreamBankReader.MaximumCsvBytes + 1];
        Array.Fill(oversized, (byte)'a');

        using var zip = CreateZipFromBytes(csvFirst: true, csvBytes: oversized, atlasBytes: null);

        var failure = Assert.Throws<InvalidDataException>(() => StreamBankReader.Read(zip));
        Assert.IsType<StreamBankLimitException>(failure.InnerException);
    }

    [Fact]
    public void Read_StripsUtf8Bom()
    {
        var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("name,url\nStation With BOM,https://example.test/bom"))
            .ToArray();

        using var zip = CreateZipFromBytes(csvFirst: true, csvBytes: bomBytes, atlasBytes: null);
        var bank = StreamBankReader.Read(zip);

        var entry = Assert.Single(bank.Entries);
        Assert.Equal("Station With BOM", entry.Title);
        Assert.Equal("https://example.test/bom", entry.Url);
    }

    private static MemoryStream CreateZip(bool csvFirst, byte[]? atlasBytes, string? csvContent = null)
    {
        var csvBytes = Encoding.UTF8.GetBytes(
            csvContent ?? "name,url,media_kind,favicon_index\nOne,https://example.test/live,AUDIO,0");
        return CreateZipFromBytes(csvFirst, csvBytes, atlasBytes);
    }

    private static MemoryStream CreateZipFromBytes(bool csvFirst, byte[] csvBytes, byte[]? atlasBytes)
    {
        var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (!csvFirst && atlasBytes is not null)
            {
                Write(archive.CreateEntry("favicon-atlas.png"), atlasBytes);
            }

            Write(archive.CreateEntry("streams.csv"), csvBytes);

            if (csvFirst && atlasBytes is not null)
            {
                Write(archive.CreateEntry("favicon-atlas.png"), atlasBytes);
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
