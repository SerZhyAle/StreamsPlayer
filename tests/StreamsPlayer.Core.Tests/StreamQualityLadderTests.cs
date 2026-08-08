using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0071 criterion 5 and the ladder half of criterion 7: what a master playlist is allowed to mean.
/// The reference body is the real 2026-08-08 playlist of the reported channel, kept verbatim - every
/// trap this parser has to survive came from it, and a paraphrase would quietly drop them.
/// </summary>
public sealed class StreamQualityLadderTests
{
    private const string ReferencePlaylist =
        """
        #EXTM3U
        #EXT-X-VERSION:5
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=446000,CODECS="mp4a.40.2,avc1.42C01F",RESOLUTION=426x240
        210-req_offset_2800000-req_window_0-3k_v5.m3u8
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=2096000,CODECS="mp4a.40.2,avc1.4D4028",RESOLUTION=1024x576
        210-req_offset_2800000-req_window_0-1k_v5.m3u8
        #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=796000,CODECS="mp4a.40.2,avc1.4D401F",RESOLUTION=640x360
        210-req_offset_2800000-req_window_0-2k_v5.m3u8
        #EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=6318,CODECS="avc1.42C01F",RESOLUTION=426x240,URI="210-iframes-vid-3k_v5.m3u8"
        #EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=18803,CODECS="avc1.4D4028",RESOLUTION=1024x576,URI="210-iframes-vid-1k_v5.m3u8"
        #EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=8188,CODECS="avc1.4D401F",RESOLUTION=640x360,URI="210-iframes-vid-2k_v5.m3u8"
        """;

    /// <summary>The three rungs the reference playlist offers, ascending.</summary>
    public static readonly StreamQualityRung[] Reference =
    [
        new(446_000, 426, 240),
        new(796_000, 640, 360),
        new(2_096_000, 1024, 576)
    ];

    [Fact]
    public void TheReferencePlaylist_YieldsItsThreeRenditionsAscending()
    {
        Assert.Equal(Reference, StreamQualityLadder.Read(ReferencePlaylist));
    }

    // The trick-play entries are not renditions. One of them declares 1024x576 at 18803 bps, which as a
    // rung would sit at the bottom of the ladder and make "step down" step up to full resolution.
    [Fact]
    public void IFrameOnlyEntries_AreNotRungs()
    {
        var ladder = StreamQualityLadder.Read(ReferencePlaylist);

        Assert.DoesNotContain(ladder, rung => rung.BandwidthBps < 400_000);
    }

    [Fact]
    public void ARungIsSpelled_AsBitrateAndResolution()
    {
        Assert.Equal("2096k/1024x576", new StreamQualityRung(2_096_000, 1024, 576).Describe());
    }

    // Criterion 5: a media playlist is a single quality. There is nothing to choose between.
    [Fact]
    public void AMediaPlaylist_HasNoLadder()
    {
        var playlist =
            """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:4
            #EXTINF:4.000,
            segment-1.ts
            #EXTINF:4.000,
            segment-2.ts
            """;

        Assert.Empty(StreamQualityLadder.Read(playlist));
    }

    [Fact]
    public void TextThatIsNotAPlaylist_HasNoLadder()
    {
        Assert.Empty(StreamQualityLadder.Read("<html><body>404</body></html>"));
    }

    [Fact]
    public void AnEmptyBody_HasNoLadder()
    {
        Assert.Empty(StreamQualityLadder.Read(string.Empty));
    }

    // Criterion 5 again: one rendition offered as a master playlist is still one quality.
    [Fact]
    public void ASingleVariant_HasNoLadder()
    {
        var playlist =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=446000,RESOLUTION=426x240
            low.m3u8
            """;

        Assert.Empty(StreamQualityLadder.Read(playlist));
    }

    // The ceiling is a resolution, so a rendition without one cannot be excluded by it. Reporting the
    // rest of the ladder would describe a limit that does not hold - criterion 6's honesty clause.
    [Fact]
    public void AVariantWithoutAResolution_VoidsTheWholeLadder()
    {
        var playlist =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=446000,RESOLUTION=426x240
            low.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2096000
            high.m3u8
            """;

        Assert.Empty(StreamQualityLadder.Read(playlist));
    }

    [Fact]
    public void AVariantWithoutABandwidth_VoidsTheWholeLadder()
    {
        var playlist =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=446000,RESOLUTION=426x240
            low.m3u8
            #EXT-X-STREAM-INF:RESOLUTION=1024x576
            high.m3u8
            """;

        Assert.Empty(StreamQualityLadder.Read(playlist));
    }

    [Fact]
    public void AnUnreadableBandwidth_VoidsTheWholeLadder()
    {
        var playlist =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=446000,RESOLUTION=426x240
            low.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=fast,RESOLUTION=1024x576
            high.m3u8
            """;

        Assert.Empty(StreamQualityLadder.Read(playlist));
    }

    // The spec fixes no attribute order, and the reference playlist does not lead with BANDWIDTH either.
    [Fact]
    public void AttributeOrderDoesNotMatter()
    {
        var playlist =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:RESOLUTION=426x240,CODECS="avc1.42C01F",BANDWIDTH=446000
            low.m3u8
            #EXT-X-STREAM-INF:CODECS="avc1.4D4028",RESOLUTION=1024x576,BANDWIDTH=2096000
            high.m3u8
            """;

        StreamQualityRung[] expected = [new(446_000, 426, 240), new(2_096_000, 1024, 576)];
        Assert.Equal(expected, StreamQualityLadder.Read(playlist));
    }

    // A naive split on ',' turns CODECS="mp4a.40.2,avc1.4D4028" into two broken attributes and loses
    // whatever followed it - which on the reference playlist is RESOLUTION, voiding every rung.
    [Fact]
    public void ACommaInsideAQuotedValue_DoesNotSplitTheAttributeList()
    {
        var playlist =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=446000,CODECS="mp4a.40.2,avc1.42C01F",RESOLUTION=426x240
            low.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2096000,CODECS="mp4a.40.2,avc1.4D4028",RESOLUTION=1024x576
            high.m3u8
            """;

        Assert.Equal(2, StreamQualityLadder.Read(playlist).Count);
    }

    [Fact]
    public void WindowsLineEndingsAndBlankLinesReadTheSame()
    {
        // Normalised first so the case is the same whatever this source file's own line endings are.
        var playlist = ReferencePlaylist.Replace("\r\n", "\n").Replace("\n", "\r\n\r\n");

        Assert.Equal(Reference, StreamQualityLadder.Read(playlist));
    }

    [Fact]
    public void AByteOrderMarkBeforeTheHeader_IsStillAPlaylist()
    {
        Assert.Equal(Reference, StreamQualityLadder.Read("\uFEFF" + ReferencePlaylist));
    }

    // Two renditions at the same rate are one rung to choose between; keeping both would make a
    // step down land on a rendition that costs exactly as much as the one it replaced.
    [Fact]
    public void TwoVariantsAtTheSameBandwidth_CollapseToOneRung()
    {
        var playlist =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=446000,RESOLUTION=426x240
            low.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=446000,RESOLUTION=426x240
            low-backup.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2096000,RESOLUTION=1024x576
            high.m3u8
            """;

        Assert.Equal([new StreamQualityRung(446_000, 426, 240), new StreamQualityRung(2_096_000, 1024, 576)],
            StreamQualityLadder.Read(playlist));
    }

    // A tag with nothing after it names no stream; it must not become a rung, and it must not void the
    // ladder either - a truncated tail is not the same thing as an under-declared rendition.
    [Fact]
    public void AVariantTagWithNoUriLine_IsSkipped()
    {
        var playlist =
            """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=446000,RESOLUTION=426x240
            low.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=796000,RESOLUTION=640x360
            mid.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2096000,RESOLUTION=1024x576
            """;

        Assert.Equal([new StreamQualityRung(446_000, 426, 240), new StreamQualityRung(796_000, 640, 360)],
            StreamQualityLadder.Read(playlist));
    }
}
