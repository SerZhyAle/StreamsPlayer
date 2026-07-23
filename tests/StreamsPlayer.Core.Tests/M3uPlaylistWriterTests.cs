using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class M3uPlaylistWriterTests
{
    private static StreamChannel Channel(string title, string url, long sortIndex = 0) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = title,
        MediaKind = StreamMediaKindClassifier.Classify(url),
        SourceOrigin = SourceOrigin.Imported,
        SortIndex = sortIndex,
        AddedAt = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public void Write_EmitsExtM3uHeaderThenExtInfAndUrlPerChannel()
    {
        var body = M3uPlaylistWriter.Write([Channel("Radio One", "https://a.test/live")]);

        Assert.StartsWith("#EXTM3U", body);
        Assert.Contains("#EXTINF:-1,Radio One", body);
        Assert.Contains("https://a.test/live", body);
    }

    [Fact]
    public void Write_RoundTripsTitlesAndOrderThroughParser()
    {
        var channels = new[]
        {
            Channel("First", "https://a.test/1", 0),
            Channel("Second", "https://b.test/2", 1),
            Channel("Third", "rtsp://c.test/3", 2)
        };

        var reparsed = M3uPlaylistParser.Parse(M3uPlaylistWriter.Write(channels));

        Assert.Equal(new[] { "First", "Second", "Third" }, reparsed.Select(entry => entry.Title));
        Assert.Equal(new[] { "https://a.test/1", "https://b.test/2", "rtsp://c.test/3" }, reparsed.Select(entry => entry.Url));
    }

    [Fact]
    public void Write_FlattensNewlinesInTitleSoOneChannelStaysOneEntry()
    {
        var body = M3uPlaylistWriter.Write([Channel("Broken\r\nTitle", "https://a.test/live")]);
        var reparsed = M3uPlaylistParser.Parse(body);

        Assert.Single(reparsed);
        Assert.DoesNotContain('\n', reparsed[0].Title);
    }

    [Fact]
    public void Write_ProducesValidUtf8WithoutBom()
    {
        var body = M3uPlaylistWriter.Write([Channel("Радио", "https://a.test/live")]);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(body);

        // No BOM prefix, and a strict-UTF-8 round-trip preserves the Cyrillic title.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Contains("Радио", M3uImportService.DecodeUtf8(bytes));
    }
}
