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

    // SP-0108, STREAM-BANK `03_catalog_format.md` §2.3: "true" (trimmed, any case) is the only true.
    // "1" and "yes" were read as true before - the inverse of the contract - and are pinned here as false.
    // Blank stays null: the tri-state half is a proposal to the contract owner, not yet the contract.
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("  true  ", true)]
    [InlineData("\"True\"", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("live", false)]
    [InlineData("vod", false)]
    [InlineData("maybe", false)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void Parse_IsLiveFollowsContractBooleanRule(string value, bool? expected)
    {
        var csv = $"name,url,is_live\r\nTest,https://radio.test/live,{value}\r\n";
        Assert.Equal(expected, Assert.Single(StreamCatalogCsvParser.Parse(csv)).IsLive);
    }

    // STREAM-BANK 2.1 item M: an unrecognised non-empty media_kind takes the blank cell's path - the URL
    // classifier - and never becomes a kind of its own.
    [Theory]
    [InlineData("PODCAST", "https://example.test/live.m3u8", MediaKind.Video)]
    [InlineData("audoi", "rtsp://camera.test/live", MediaKind.Rtsp)]
    [InlineData("TV", "https://example.test/radio.mp3", MediaKind.Audio)]
    [InlineData("", "https://example.test/live.mpd", MediaKind.Video)]
    public void Parse_UnrecognisedMediaKindFallsBackToUrlClassifier(string value, string url, MediaKind expected)
    {
        var csv = $"name,url,media_kind\r\nTest,{url},{value}\r\n";
        Assert.Equal(expected, Assert.Single(StreamCatalogCsvParser.Parse(csv)).MediaKind);
    }

    // SP-0088, STREAM-BANK item E: `access` is an opaque token, not a closed set. Blank means open;
    // every non-empty value means a restriction this consumer does not model. SP-0033 had this inverted -
    // it recognised `geo` alone and folded every other token into Open, so a token the producer adds
    // later would read as the *absence* of a restriction, which is the one answer that cannot be right.
    [Theory]
    [InlineData("geo", ChannelAccess.GeoRestricted)]
    [InlineData("GEO", ChannelAccess.GeoRestricted)]
    [InlineData("  geo  ", ChannelAccess.GeoRestricted)]
    [InlineData("", ChannelAccess.Open)]
    [InlineData("   ", ChannelAccess.Open)]
    [InlineData("paywall", ChannelAccess.GeoRestricted)]
    [InlineData("drm", ChannelAccess.GeoRestricted)]
    public void Parse_TreatsAnyNonEmptyAccessTokenAsARestriction(string value, ChannelAccess expected)
    {
        var csv = $"name,url,access\r\nTest,https://radio.test/live,\"{value}\"\r\n";
        Assert.Equal(expected, Assert.Single(StreamCatalogCsvParser.Parse(csv)).Access);
    }

    [Fact]
    public void Parse_MissingAccessColumnMeansOpen()
    {
        const string csv = "name,url\r\nTest,https://radio.test/live\r\n";
        Assert.Equal(ChannelAccess.Open, Assert.Single(StreamCatalogCsvParser.Parse(csv)).Access);
    }
}
