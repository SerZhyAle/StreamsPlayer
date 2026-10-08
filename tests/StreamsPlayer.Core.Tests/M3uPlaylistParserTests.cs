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

    // SP-0126: the title starts after the first comma outside a quoted attribute value.
    [Theory]
    [InlineData("#EXTINF:-1 group-title=\"News, Sport\",BBC", "BBC")]
    [InlineData("#EXTINF:-1 tvg-name=\"a,b\" group-title=\"c,d\",Radio, One", "Radio, One")]
    [InlineData("#EXTINF:-1,Plain", "Plain")]
    public void Analyze_TitleIgnoresCommasInsideQuotedAttributes(string extinf, string expected)
    {
        var preview = M3uPlaylistParser.Analyze($"#EXTM3U\n{extinf}\nhttps://a.test/live\n", Existing());

        Assert.Equal(expected, Assert.Single(preview.NewEntries).Title);
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

    // SP-0184 S11-7: the same stream spelled with another scheme/host case or an explicit default port is a duplicate.
    [Fact]
    public void Analyze_DeduplicatesByUrlIdentityNotSpelling()
    {
        const string playlist =
            "HTTPS://Known.Test:443/live\n" +   // duplicate of the stored row
            "http://Fresh.test/a\n" +           // new
            "http://fresh.test:80/a\n";          // same identity repeated within the file

        var preview = M3uPlaylistParser.Analyze(playlist, Existing("https://known.test/live"));

        Assert.Equal(1, preview.DuplicateCount);
        Assert.Equal(1, preview.NewCount);
        Assert.Equal(1, preview.SkippedCount);
        Assert.Equal("http://Fresh.test/a", preview.NewEntries[0].Url);
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

    // SP-0184 (S14-2): a body of millions of one-line entries must not become millions of channels.
    [Fact]
    public void Analyze_StopsAtTheEntryCeilingAndSaysSo()
    {
        var lines = Enumerable.Range(0, M3uPlaylistParser.MaximumNewEntries + 5)
            .Select(index => $"https://host.test/{index}");

        var preview = M3uPlaylistParser.Analyze(string.Join('\n', lines), Existing());

        Assert.Equal(M3uImportStatus.Ok, preview.Status);
        Assert.Equal(M3uPlaylistParser.MaximumNewEntries, preview.NewEntries.Count);
        Assert.Equal(M3uPlaylistParser.MaximumNewEntries, preview.NewCount);
        Assert.True(preview.Truncated);
    }

    [Fact]
    public void Analyze_ADuplicateAtTheCeilingDoesNotTruncate()
    {
        var lines = Enumerable.Range(0, M3uPlaylistParser.MaximumNewEntries)
            .Select(index => $"https://host.test/{index}")
            .Append("https://host.test/0");

        var preview = M3uPlaylistParser.Analyze(string.Join('\n', lines), Existing());

        Assert.Equal(M3uPlaylistParser.MaximumNewEntries, preview.NewCount);
        Assert.Equal(1, preview.SkippedCount);
        Assert.False(preview.Truncated);
    }
}