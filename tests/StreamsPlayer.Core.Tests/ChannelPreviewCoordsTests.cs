using System.Text.Json;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// The <c>url -&gt; slot</c> sidecar. Shared between the tile pack and the (now unused) sprite sheet, so
/// SP-0091 changed nothing here; the sheet-geometry tests that used to share this file went with the
/// geometry.
/// </summary>
public sealed class ChannelPreviewCoordsTests
{
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
