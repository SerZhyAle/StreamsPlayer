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

        Assert.Equal(IcecastStatusParse.MatchedMount, IcecastStatusParser.ExtractTitle(payload, StreamUri, out var title));
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

        Assert.Equal(IcecastStatusParse.MatchedMount, IcecastStatusParser.ExtractTitle(payload, StreamUri, out var title));
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

        Assert.Equal(IcecastStatusParse.MatchedMount, IcecastStatusParser.ExtractTitle(payload, StreamUri, out var title));
        Assert.Null(title);
    }

    [Fact]
    public void UnreadableJsonIsNotAStatusDocument()
    {
        Assert.Equal(IcecastStatusParse.NotStatusDocument, IcecastStatusParser.ExtractTitle("not json", StreamUri, out _));
        Assert.Equal(
            IcecastStatusParse.NotStatusDocument,
            IcecastStatusParser.ExtractTitle("{ \"server\": \"something else\" }", StreamUri, out _));
    }

    /// <summary>
    /// SP-0172: a readable status document that does not describe the playing mount is an ordinary
    /// absence, not a malformed answer - the two must not share one classification.
    /// </summary>
    [Fact]
    public void AMountlessDocumentIsReadableButUnmatched()
    {
        Assert.Equal(
            IcecastStatusParse.NoMatchingMount,
            IcecastStatusParser.ExtractTitle("{ \"icestats\": { \"source\": [] } }", StreamUri, out _));
        Assert.Equal(
            IcecastStatusParse.NoMatchingMount,
            IcecastStatusParser.ExtractTitle("{ \"icestats\": {} }", StreamUri, out _));
        Assert.Equal(
            IcecastStatusParse.NoMatchingMount,
            IcecastStatusParser.ExtractTitle(
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

        Assert.Equal(IcecastStatusParse.MatchedMount, IcecastStatusParser.ExtractTitle(payload, StreamUri, out var title));
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

        Assert.Equal(IcecastStatusParse.MatchedMount, IcecastStatusParser.ExtractTitle(payload, StreamUri, out var title));
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

        Assert.Equal(IcecastStatusParse.NoMatchingMount, IcecastStatusParser.ExtractTitle(payload, StreamUri, out _));
    }

    /// <summary>
    /// Re-audit D5: a string member holding a lone surrogate escape parses but throws on GetString(); the
    /// payload is then not a status document, and the exception never reaches the fire-and-forget caller.
    /// </summary>
    [Theory]
    [InlineData("""{ "icestats": { "source": { "listenurl": "https://radio.example.test:8443/live/main.mp3", "title": "\ud800" } } }""")]
    [InlineData("""{ "icestats": { "source": { "listenurl": "\ud800", "title": "x" } } }""")]
    [InlineData("""{ "icestats": { "source": [ { "listenurl": "https://radio.example.test:8443/live/main.mp3", "title": "ok \udc00" } ] } }""")]
    public void ALoneSurrogateInAStatusMemberIsNotAStatusDocumentAndDoesNotThrow(string payload)
    {
        var parse = IcecastStatusParser.ExtractTitle(payload, StreamUri, out var title);

        Assert.Equal(IcecastStatusParse.NotStatusDocument, parse);
        Assert.Null(title);
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

        Assert.Equal(IcecastStatusParse.MatchedMount, IcecastStatusParser.ExtractTitle(payload, StreamUri, out var title));
        Assert.NotNull(title);
        Assert.Equal(IcyMetadataParser.MaxTitleLength, title.Length);
        Assert.DoesNotContain('\n', title);
    }
}
