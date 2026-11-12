using System.Security.Cryptography;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0247: replays the USER-PLAYLIST conformance vectors (contract section 4 and the section 9 clarification)
/// through the importer. The files are byte-for-byte copies of the contract store, pinned in
/// <c>Vectors/user-playlist/PROVENANCE.txt</c>.
/// </summary>
public sealed class M3uPlaylistVectorTests
{
    private static string VectorPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Vectors", "user-playlist", name);

    private static M3uImportPreview Import(string name) =>
        M3uPlaylistParser.Analyze(File.ReadAllText(VectorPath(name)), new HashSet<string>(StringComparer.Ordinal));

    [Theory]
    [InlineData("hls-manifest-rejection.m3u8", "edd8c5057b07db68d97848fb6ef0f59829ad645ab9731159c210bba5353eeebc")]
    [InlineData("valid-extended-hls-entry.m3u8", "161fbae86acda2bbcdd0a8bc8d32ad6f184d65003cb6eb9dc27c780c94f9340e")]
    [InlineData("valid-extended.m3u8", "dea1d85abc6d34afc51d02d5d4772f2f572977c9014354f663eda61eba36ca15")]
    [InlineData("valid-playlist.sza-playlist.json", "96f8ba19cbde5348c1465b0cabaae531e41f1303b0fc784fcbd0a520ed5b4067")]
    public void Vectors_HaveTheirRecordedHashes(string name, string sha256)
    {
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(VectorPath(name)))));
    }

    [Fact]
    public void HlsManifestVector_IsRefusedWithNoRows()
    {
        var preview = Import("hls-manifest-rejection.m3u8");

        Assert.Equal(M3uImportStatus.HlsManifest, preview.Status);
        Assert.Empty(preview.NewEntries);
    }

    [Fact]
    public void HlsEntryVector_ImportsAllThreeRows()
    {
        var preview = Import("valid-extended-hls-entry.m3u8");

        Assert.Equal(M3uImportStatus.Ok, preview.Status);
        Assert.Equal(
            ["https://example.com/news/index.m3u8", "https://example.com/sport/master.m3u8", "https://example.com/audio/live.mp3"],
            preview.NewEntries.Select(entry => entry.Url));
        Assert.Equal("Sport Live (mentions #EXT-X-VERSION in its title)", preview.NewEntries[1].Title);
    }

    [Fact]
    public void ExtendedVector_ImportsAllThreeRowsWithTheirTitles()
    {
        var preview = Import("valid-extended.m3u8");

        Assert.Equal(M3uImportStatus.Ok, preview.Status);
        Assert.Equal(
            ["Radio Paradise (Main Mix)", "Public News Stream", "Local Studio Camera"],
            preview.NewEntries.Select(entry => entry.Title));
        Assert.Equal(MediaKind.Rtsp, preview.NewEntries[2].MediaKind);
    }

    [Fact]
    public void JsonCarrierVector_ImportsNothingThroughTheM3uReader()
    {
        // Section 6 item A: the JSON carrier is optional and this product has no JSON reader; the file is
        // not a playlist to the M3U importer, so it yields no channel rather than a partial one.
        var preview = Import("valid-playlist.sza-playlist.json");

        Assert.Empty(preview.NewEntries);
    }

    [Theory]
    [InlineData("#EXTM3U\n  #EXT-X-TARGETDURATION:10\nseg.ts")]
    [InlineData("#EXTM3U\r\n#ext-x-version:3\r\nseg.ts")]
    public void ALineThatBeginsWithTheTag_MakesAManifest(string body)
    {
        var preview = M3uPlaylistParser.Analyze(body, new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(M3uImportStatus.HlsManifest, preview.Status);
    }

    [Fact]
    public void TheTagInsideAnAttributeOrUrl_DoesNotMakeAManifest()
    {
        const string body = "#EXTM3U\n#EXTINF:-1 tvg-name=\"#EXT-X-STREAM-INF\",Odd\nhttps://a.test/live?tag=#EXT-X-KEY\n";

        var preview = M3uPlaylistParser.Analyze(body, new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(M3uImportStatus.Ok, preview.Status);
        Assert.Single(preview.NewEntries);
    }
}
