using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class StreamCatalogCsvParserTests
{
    [Fact]
    public void Parse_UsesHeaderNamesAndRfc4180Quoting()
    {
        const string csv = "unknown,url,name,notes,media_kind,favicon_index,language\r\n" +
                           "ignored,https://radio.test/live,\"Radio, One\",\"line 1\r\nline 2\",AUDIO,7,\"english,german\"\r\n";

        var entry = Assert.Single(StreamCatalogCsvParser.Parse(csv));

        Assert.Equal("Radio, One", entry.Title);
        Assert.Equal(MediaKind.Audio, entry.MediaKind);
        Assert.Equal(7, entry.FaviconIndex);
        Assert.Equal("english,german", entry.Language);
    }

    [Fact]
    public void Parse_DropsBlankRequiredFieldsAndToleratesMissingColumns()
    {
        const string csv = "name,url\n" +
                           "Valid,https://example.test/live.mpd\n" +
                           ",https://example.test/missing-name\n" +
                           "Missing URL,   \n";

        var entry = Assert.Single(StreamCatalogCsvParser.Parse(csv));

        Assert.Equal(MediaKind.Video, entry.MediaKind);
        Assert.Null(entry.FaviconIndex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    public void Parse_InvalidFaviconIndexMeansNoFavicon(string value)
    {
        var csv = $"name,url,favicon_index\nTest,https://example.test/live,{value}";
        Assert.Null(Assert.Single(StreamCatalogCsvParser.Parse(csv)).FaviconIndex);
    }

    [Fact]
    public void Parse_PopulatesOptionalTechnicalMetadata()
    {
        const string csv = "name,url,protocol,format,bitrate,is_live\r\n" +
                           "Test,https://radio.test/live,HLS,AAC,128 kbps,true\r\n";

        var entry = Assert.Single(StreamCatalogCsvParser.Parse(csv));

        Assert.Equal("HLS", entry.Protocol);
        Assert.Equal("AAC", entry.Format);
        Assert.Equal("128 kbps", entry.Bitrate);
        Assert.True(entry.IsLive);
    }

    [Fact]
    public void Parse_MissingTechnicalColumnsLeaveFieldsNull()
    {
        const string csv = "name,url\r\nTest,https://radio.test/live\r\n";

        var entry = Assert.Single(StreamCatalogCsvParser.Parse(csv));

        Assert.Null(entry.Protocol);
        Assert.Null(entry.Format);
        Assert.Null(entry.Bitrate);
        Assert.Null(entry.IsLive);
    }

    [Theory]
    [InlineData("vod", false)]
    [InlineData("maybe", null)]
    [InlineData("", null)]
    public void Parse_TolerantIsLiveClaim(string value, bool? expected)
    {
        var csv = $"name,url,is_live\r\nTest,https://radio.test/live,{value}\r\n";
        Assert.Equal(expected, Assert.Single(StreamCatalogCsvParser.Parse(csv)).IsLive);
    }
}
