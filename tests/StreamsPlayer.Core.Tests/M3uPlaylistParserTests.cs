using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class M3uPlaylistParserTests
{
    [Fact]
    public void Parse_AssociatesExtInfWithNextUrl()
    {
        const string playlist = "#EXTM3U\n#EXTINF:-1,Channel One\nhttps://example.test/live.m3u8\n" +
                                "https://radio.test/live\n";

        var entries = M3uPlaylistParser.Parse(playlist);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Channel One", entries[0].Title);
        Assert.Equal(MediaKind.Video, entries[0].MediaKind);
        Assert.Equal("radio.test", entries[1].Title);
    }

    [Fact]
    public void Parse_HlsManifestImportsNoChannels()
    {
        const string manifest = "#EXTM3U\n#EXT-X-VERSION:3\nsegment.ts";
        Assert.Empty(M3uPlaylistParser.Parse(manifest));
    }
}
