using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class M3uPlaylistParserTests
{
    private static HashSet<string> Existing(params string[] urls) => new(urls, StringComparer.Ordinal);

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

    [Fact]
    public void Analyze_HlsManifestReportsManifestStatusAndZeroCounts()
    {
        var preview = M3uPlaylistParser.Analyze("#EXTM3U\n#EXT-X-VERSION:3\nsegment.ts", Existing());

        Assert.Equal(M3uImportStatus.HlsManifest, preview.Status);
        Assert.Empty(preview.NewEntries);
        Assert.Equal(0, preview.NewCount);
        Assert.Equal(0, preview.DuplicateCount);
        Assert.Equal(0, preview.InvalidCount);
        Assert.Equal(0, preview.SkippedCount);
    }

    [Fact]
    public void Analyze_CommentOnlyBodyReportsEmpty()
    {
        var preview = M3uPlaylistParser.Analyze("#EXTM3U\n# just a comment\n\n", Existing());
        Assert.Equal(M3uImportStatus.Empty, preview.Status);
    }

    [Fact]
    public void Analyze_CategorisesNewDuplicateInvalidAndSkipped()
    {
        const string playlist =
            "#EXTM3U\n" +
            "#EXTINF:-1,Fresh\nhttps://a.test/live\n" +   // new
            "https://known.test/live\n" +                 // duplicate (already stored)
            "https://a.test/live\n" +                     // skipped (repeat within file)
            "not-a-url\n" +                               // invalid
            "ftp://nope.test/x\n";                        // invalid (unlaunchable scheme)

        var preview = M3uPlaylistParser.Analyze(playlist, Existing("https://known.test/live"));

        Assert.Equal(M3uImportStatus.Ok, preview.Status);
        Assert.Equal(1, preview.NewCount);
        Assert.Equal("Fresh", preview.NewEntries[0].Title);
        Assert.Equal(1, preview.DuplicateCount);
        Assert.Equal(2, preview.InvalidCount);
        Assert.Equal(1, preview.SkippedCount);
    }

    [Fact]
    public void Analyze_ImportedProvenanceIsAppliedByCaller_EntriesCarryNoOrigin()
    {
        // CatalogEntry has no provenance; the App stamps SourceOrigin.Imported. Guard the parser contract:
        // it emits launchable entries only, classified by URL.
        var preview = M3uPlaylistParser.Analyze("rtsp://cam.test/stream\n", Existing());
        Assert.Single(preview.NewEntries);
        Assert.Equal(MediaKind.Rtsp, preview.NewEntries[0].MediaKind);
    }
}
