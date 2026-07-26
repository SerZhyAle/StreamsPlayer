using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ChannelPreviewAtlasTests
{
    // The shipped 2026-07-26 sheet, measured from the published asset.
    private const int SheetWidth = 8160;
    private const int SheetHeight = 7560;

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(33, 7920, 0)]     // last column of row 0
    [InlineData(34, 0, 135)]      // wraps to row 1
    [InlineData(68, 0, 270)]      // row 2, the README's worked example
    [InlineData(1880, 2400, 7425)] // highest index present in the live sidecar: col 10, row 55
    public void RectFor_MapsIndexToTheContractGrid(int index, int expectedLeft, int expectedTop)
    {
        var rect = ChannelPreviewAtlas.RectFor(index);

        Assert.Equal(expectedLeft, rect.Left);
        Assert.Equal(expectedTop, rect.Top);
        Assert.Equal(ChannelPreviewAtlas.TileWidth, rect.Width);
        Assert.Equal(ChannelPreviewAtlas.TileHeight, rect.Height);
    }

    [Fact]
    public void IsInBounds_AcceptsTheHighestPublishedIndex()
    {
        Assert.True(ChannelPreviewAtlas.IsInBounds(1880, SheetWidth, SheetHeight));
    }

    [Fact]
    public void IsInBounds_RejectsNegativeAndPastTheSheet()
    {
        Assert.False(ChannelPreviewAtlas.IsInBounds(-1, SheetWidth, SheetHeight));
        // 34 cols x 56 rows = 1904 cells, so 1904 is the first index whose row falls off the sheet.
        Assert.False(ChannelPreviewAtlas.IsInBounds(1904, SheetWidth, SheetHeight));
    }

    [Fact]
    public void IsInBounds_RejectsAStaleIndexAgainstAShrunkSheet()
    {
        // A sidecar kept from a larger build must not crop outside a smaller republished sheet.
        Assert.False(ChannelPreviewAtlas.IsInBounds(1880, SheetWidth, 1350));
    }

    [Fact]
    public void Parse_ReadsUrlToIndexPairs()
    {
        var map = ChannelPreviewCoords.Parse(
            """{"https://chan/a.m3u8":0,"https://chan/b.m3u8":33,"https://chan/c.m3u8":68}""");

        Assert.Equal(3, map.Count);
        Assert.Equal(0, map["https://chan/a.m3u8"]);
        Assert.Equal(33, map["https://chan/b.m3u8"]);
        Assert.Equal(68, map["https://chan/c.m3u8"]);
    }

    [Fact]
    public void Parse_SkipsNonIntegerValuesInsteadOfFailing()
    {
        var map = ChannelPreviewCoords.Parse(
            """{"a":1,"b":null,"c":{"x":1},"d":[1],"e":"7","f":true,"g":1.5}""");

        // "e" survives as a numeric string; every other malformed entry is dropped, "a" is untouched.
        Assert.Equal(2, map.Count);
        Assert.Equal(1, map["a"]);
        Assert.Equal(7, map["e"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    public void Parse_TreatsBlankOrNonObjectAsNotInstalled(string json)
    {
        Assert.Empty(ChannelPreviewCoords.Parse(json));
    }

    [Fact]
    public void Parse_ThrowsOnMalformedJson()
    {
        // JsonReaderException derives from JsonException; the contract is the base type.
        Assert.ThrowsAny<JsonException>(() => ChannelPreviewCoords.Parse("{not json"));
    }
}
