using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class IcyMetadataParserTests
{
    [Fact]
    public void ExtractStreamTitle_ReturnsTrackAndIgnoresTrailingFields()
    {
        const string block = "StreamTitle='Artist - Song';StreamUrl='http://example.test';";

        Assert.Equal("Artist - Song", IcyMetadataParser.ExtractStreamTitle(block));
    }

    [Fact]
    public void ExtractStreamTitle_EmptyTitleIsNull()
    {
        Assert.Null(IcyMetadataParser.ExtractStreamTitle("StreamTitle='';"));
    }

    [Fact]
    public void ExtractStreamTitle_MissingTitleFieldIsNull()
    {
        Assert.Null(IcyMetadataParser.ExtractStreamTitle("StreamUrl='http://example.test';"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0\0\0")]
    public void ExtractStreamTitle_EmptyOrWhitespaceInputIsNull(string block)
    {
        Assert.Null(IcyMetadataParser.ExtractStreamTitle(block));
    }

    [Fact]
    public void ExtractStreamTitle_OversizedTitleIsClamped()
    {
        var longTitle = new string('a', IcyMetadataParser.MaxTitleLength + 100);
        var block = $"StreamTitle='{longTitle}';";

        var result = IcyMetadataParser.ExtractStreamTitle(block);

        Assert.NotNull(result);
        Assert.Equal(IcyMetadataParser.MaxTitleLength, result!.Length);
    }

    [Fact]
    public void ExtractStreamTitle_StripsControlCharactersAndCollapsesWhitespace()
    {
        const string block = "StreamTitle='Artist\0 -\t\tSong\r\n';";

        Assert.Equal("Artist - Song", IcyMetadataParser.ExtractStreamTitle(block));
    }

    [Fact]
    public void ExtractStreamTitle_MalformedUnterminatedBlockIsBestEffort()
    {
        // No closing "';" — must not throw and should still surface the value.
        Assert.Equal("Artist - Song", IcyMetadataParser.ExtractStreamTitle("StreamTitle='Artist - Song"));
    }

    [Fact]
    public void ExtractStreamTitle_NullPaddedBlockYieldsCleanTitle()
    {
        // Mirrors a real block decoded from a 16-byte-padded metadata frame.
        const string block = "StreamTitle='Now Playing';\0\0\0\0\0";

        Assert.Equal("Now Playing", IcyMetadataParser.ExtractStreamTitle(block));
    }
}
