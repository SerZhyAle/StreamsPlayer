using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class IcecastStatusParserTests
{
    private static readonly Uri StreamUri = new("https://radio.example.test:8443/live/main.mp3");

    [Fact]
    public void ExtractsTheMatchingMountFromAnArray()
    {
        const string payload = """
            { "icestats": { "source": [
              { "listenurl": "https://radio.example.test:8443/other.mp3", "title": "Other" },
              { "listenurl": "http://radio.example.test:8443/live/main.mp3", "title": "Artist - Track" }
            ] } }
            """;

        Assert.True(IcecastStatusParser.TryExtractTitle(payload, StreamUri, out var title));
        Assert.Equal("Artist - Track", title);
    }

    [Fact]
    public void AcceptsTheSingleSourceShapeAndFallbackTitleField()
    {
        const string payload = """
            { "icestats": { "source": {
              "listenurl": "https://radio.example.test:8443/live/main.mp3",
              "yp_currently_playing": "Programme name"
            } } }
            """;

        Assert.True(IcecastStatusParser.TryExtractTitle(payload, StreamUri, out var title));
        Assert.Equal("Programme name", title);
    }

    [Fact]
    public void TreatsAMatchingMountWithoutATitleAsAValidSilentSource()
    {
        const string payload = """
            { "icestats": { "source": {
              "listenurl": "https://radio.example.test:8443/live/main.mp3"
            } } }
            """;

        Assert.True(IcecastStatusParser.TryExtractTitle(payload, StreamUri, out var title));
        Assert.Null(title);
    }

    [Fact]
    public void RefusesMalformedAndUnrelatedDocuments()
    {
        Assert.False(IcecastStatusParser.TryExtractTitle("not json", StreamUri, out _));
        Assert.False(IcecastStatusParser.TryExtractTitle("{ \"icestats\": { \"source\": [] } }", StreamUri, out _));
        Assert.False(IcecastStatusParser.TryExtractTitle(
            "{ \"icestats\": { \"source\": { \"listenurl\": \"https://radio.example.test:8443/other.mp3\" } } }",
            StreamUri,
            out _));
    }

    [Fact]
    public void MatchesTheOnlySourceOnThePlayingPathWhenTheServerReportsLocalhost()
    {
        const string payload = """
            { "icestats": { "source": [
              { "listenurl": "http://localhost:8000/other.mp3", "title": "Other" },
              { "listenurl": "http://localhost:8000/live/main.mp3", "title": "Artist - Track" }
            ] } }
            """;

        Assert.True(IcecastStatusParser.TryExtractTitle(payload, StreamUri, out var title));
        Assert.Equal("Artist - Track", title);
    }

    [Fact]
    public void AnExactMatchWinsOverAnotherSourceOnTheSamePath()
    {
        const string payload = """
            { "icestats": { "source": [
              { "listenurl": "http://localhost:8000/live/main.mp3", "title": "Wrong" },
              { "listenurl": "https://radio.example.test:8443/live/main.mp3", "title": "Right" }
            ] } }
            """;

        Assert.True(IcecastStatusParser.TryExtractTitle(payload, StreamUri, out var title));
        Assert.Equal("Right", title);
    }

    [Fact]
    public void TwoInexactSourcesOnThePlayingPathStayUnmatched()
    {
        const string payload = """
            { "icestats": { "source": [
              { "listenurl": "http://localhost:8000/live/main.mp3", "title": "One" },
              { "listenurl": "http://relay.example.test:8001/live/main.mp3", "title": "Two" }
            ] } }
            """;

        Assert.False(IcecastStatusParser.TryExtractTitle(payload, StreamUri, out _));
    }

    [Fact]
    public void SanitizesAndBoundsUntrustedStatusText()
    {
        var tooLong = new string('x', IcyMetadataParser.MaxTitleLength + 50);
        var payload = $$"""
            { "icestats": { "source": {
              "listenurl": "https://radio.example.test:8443/live/main.mp3",
              "title": "Artist\n{{tooLong}}"
            } } }
            """;

        Assert.True(IcecastStatusParser.TryExtractTitle(payload, StreamUri, out var title));
        Assert.NotNull(title);
        Assert.Equal(IcyMetadataParser.MaxTitleLength, title.Length);
        Assert.DoesNotContain('\n', title);
    }
}
