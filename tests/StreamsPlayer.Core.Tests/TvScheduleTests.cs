using System.IO.Compression;
using System.Text;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0075: parsing, binding and storage of the TV schedule. Every case is a way the feature could show a
/// wrong programme (worse than none), keep more than it promised, or reach into the user's catalog.
/// </summary>
public sealed class TvScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE tv SYSTEM "xmltv.dtd">
        <tv generator-info-name="test">
          <channel id="One.uk"><display-name>Channel One</display-name><display-name>C1</display-name></channel>
          <channel id="Two.uk"><display-name>Channel Two HD</display-name></channel>
          <channel id="Dup.a"><display-name>Shared</display-name></channel>
          <channel id="Dup.b"><display-name>Shared</display-name></channel>
          <programme start="20260925140000 +0200" stop="20260925150000 +0200" channel="One.uk"><title lang="en">News</title></programme>
          <programme start="20260925150000 +0200" stop="20260925160000 +0200" channel="One.uk"><title>Film</title><title>Other</title></programme>
          <programme start="20260925100000 +0000" stop="20260925110000 +0000" channel="One.uk"><title>Ended</title></programme>
          <programme start="20260928100000 +0000" stop="20260928110000 +0000" channel="One.uk"><title>Too far</title></programme>
          <programme start="20260925113000" channel="Two.uk"><title>Open</title></programme>
          <programme start="20260925123000" stop="20260925130000" channel="Two.uk"><title>After</title></programme>
          <programme start="20260925120000 +0000" stop="20260925130000 +0000"><title>No channel</title></programme>
          <programme start="bad" stop="20260925130000 +0000" channel="One.uk"><title>Bad time</title></programme>
        </tv>
        """;

    private static IReadOnlyList<TvScheduleChannel> ParseSample() =>
        XmltvParser.Parse(Encoding.UTF8.GetBytes(Sample), Now);

    private static StreamChannel Channel(string title, MediaKind kind = MediaKind.Video, string? url = null) => new()
    {
        Id = Guid.NewGuid(),
        Url = url ?? $"https://example.test/{Uri.EscapeDataString(title)}.m3u8",
        Title = title,
        MediaKind = kind,
        SourceOrigin = SourceOrigin.Manual,
        AddedAt = Now
    };

    [Fact]
    public void ParseKeepsOffsetsAndTheWindowAndDropsBrokenProgrammes()
    {
        var channels = ParseSample();
        var one = channels.Single(channel => channel.Id == "One.uk");

        Assert.Equal(["Channel One", "C1"], one.Names);
        Assert.Equal(["News", "Film"], one.Programmes.Select(programme => programme.Title));
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero), one.Programmes[0].Start.ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(2), one.Programmes[0].Start.Offset);
    }

    [Fact]
    public void OpenEndedProgrammeIsClosedByItsSuccessorAndATimeWithoutOffsetIsUtc()
    {
        var two = ParseSample().Single(channel => channel.Id == "Two.uk");

        Assert.Equal("Open", two.Programmes[0].Title);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 12, 30, 0, TimeSpan.Zero), two.Programmes[0].Stop);
    }

    [Fact]
    public void AnOpenEndedProgrammeIsClosedByItsRealSuccessorEvenWhenThatHasEnded()
    {
        // A has no stop; B, which really follows it, is already over. If B were dropped before A is closed,
        // C would close A instead and A would read as on air now - a wrong title, worse than none.
        const string body = """
            <tv>
              <programme start="20260925100000 +0000" channel="x"><title>A</title></programme>
              <programme start="20260925110000 +0000" stop="20260925113000 +0000" channel="x"><title>B</title></programme>
              <programme start="20260925130000 +0000" stop="20260925140000 +0000" channel="x"><title>C</title></programme>
            </tv>
            """;

        var channel = Assert.Single(XmltvParser.Parse(Encoding.UTF8.GetBytes(body), Now));
        var reading = TvScheduleQueries.NowAndNext(channel.Programmes, Now);

        Assert.Null(reading.Now);
        Assert.Equal("C", reading.Next?.Title);
    }

    [Fact]
    public void GzipBodyIsReadTheSameAsPlain()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(Sample));
        }

        var channels = XmltvParser.Parse(buffer.ToArray(), Now);

        Assert.Equal(ParseSample().Count, channels.Count);
    }

    [Theory]
    [InlineData("<html><body>not a schedule</body></html>")]
    [InlineData("this is not xml")]
    public void NonXmltvIsRejected(string body)
    {
        Assert.Throws<InvalidDataException>(() => XmltvParser.Parse(Encoding.UTF8.GetBytes(body), Now));
    }

    [Theory]
    [InlineData("20260925140000 +0200", 12)]
    [InlineData("20260925140000 -0130", 15)]
    [InlineData("20260925140000", 14)]
    [InlineData("20260925140000 UTC", 14)]
    public void TimesResolveToTheRightUtcHour(string value, int utcHour)
    {
        var parsed = XmltvParser.ParseTime(value);

        Assert.NotNull(parsed);
        Assert.Equal(utcHour, parsed.Value.UtcDateTime.Hour);
    }

    [Theory]
    [InlineData("20260925140000 CEST")]
    [InlineData("2026-09-25")]
    [InlineData("")]
    public void UnreadableTimesAreRefusedRatherThanGuessed(string value)
    {
        Assert.Null(XmltvParser.ParseTime(value));
    }

    [Theory]
    [InlineData("Channel One HD", "channelone")]
    [InlineData("Channel One (1080p) [Geo-blocked]", "channelone")]
    [InlineData("Première  Télé", "premieretele")]
    [InlineData("TV5 Monde", "tv5monde")]
    [InlineData("HD", "hd")]
    [InlineData("  ", null)]
    public void NamesNormalize(string input, string? expected)
    {
        Assert.Equal(expected, TvScheduleMatcher.NormalizeName(input));
    }

    [Fact]
    public void AmbiguousNamesBindNothingAndRadioNeverAutoMatches()
    {
        var document = new TvScheduleDocument(TvScheduleDocument.CurrentSchemaVersion, "https://s.test/g.xml", Now, ParseSample());
        var catalog = new[]
        {
            Channel("Channel One FHD"),
            Channel("Channel Two"),
            Channel("Shared"),
            Channel("Channel One", MediaKind.Audio),
            Channel("Unknown station")
        };

        var index = TvScheduleIndex.Build(document, [], catalog);

        Assert.Equal("One.uk", index.ChannelFor(catalog[0].Url)?.Id);
        Assert.Equal("Two.uk", index.ChannelFor(catalog[1].Url)?.Id);
        Assert.Null(index.ChannelFor(catalog[2].Url));
        Assert.Null(index.ChannelFor(catalog[3].Url));
        Assert.Null(index.ChannelFor(catalog[4].Url));
        Assert.Equal(2, index.MatchedCount);
        Assert.Equal(4, index.EligibleCount);
    }

    [Fact]
    public void UserBindingWinsIncludingAnExplicitNone()
    {
        var document = new TvScheduleDocument(TvScheduleDocument.CurrentSchemaVersion, "https://s.test/g.xml", Now, ParseSample());
        var matched = Channel("Channel One");
        var rebound = Channel("Something else");

        var index = TvScheduleIndex.Build(
            document,
            [new TvScheduleBinding(matched.Url, null), new TvScheduleBinding(rebound.Url.Replace("https://example.test", "HTTPS://EXAMPLE.TEST"), "Two.uk")],
            [matched, rebound]);

        Assert.Null(index.ChannelFor(matched.Url));
        Assert.Equal("Two.uk", index.ChannelFor(rebound.Url)?.Id);
    }

    [Fact]
    public void NowAndNextFollowTheClockAndStaleDataShowsNothing()
    {
        var one = ParseSample().Single(channel => channel.Id == "One.uk");

        var atNoon = TvScheduleQueries.NowAndNext(one.Programmes, Now);
        Assert.Equal("News", atNoon.Now?.Title);
        Assert.Equal("Film", atNoon.Next?.Title);

        var atChange = TvScheduleQueries.NowAndNext(one.Programmes, Now.AddHours(1));
        Assert.Equal("Film", atChange.Now?.Title);
        Assert.Null(atChange.Next);

        Assert.True(TvScheduleQueries.NowAndNext(one.Programmes, Now.AddDays(1)).IsEmpty);
    }

    [Fact]
    public void RunningOutIsReportedNearTheEndOfTheData()
    {
        var document = new TvScheduleDocument(TvScheduleDocument.CurrentSchemaVersion, "https://s.test/g.xml", Now, ParseSample());
        var index = TvScheduleIndex.Build(document, [], []);

        Assert.True(index.IsRunningOut(Now));
        Assert.False(TvScheduleIndex.Empty.IsRunningOut(Now));
    }

    [Fact]
    public void ProgrammeCapKeepsTheEarliest()
    {
        var builder = new StringBuilder("<tv>");
        var total = TvScheduleLimits.MaximumProgrammes + 50;
        for (var index = 0; index < total; index++)
        {
            var start = Now.AddMinutes(index % 2000).ToString("yyyyMMddHHmmss") + " +0000";
            var stop = Now.AddMinutes(index % 2000 + 1).ToString("yyyyMMddHHmmss") + " +0000";
            builder.Append($"<programme start=\"{start}\" stop=\"{stop}\" channel=\"c{index / 2000}\"><title>t</title></programme>");
        }

        builder.Append("</tv>");

        var channels = XmltvParser.Parse(Encoding.UTF8.GetBytes(builder.ToString()), Now);

        Assert.Equal(TvScheduleLimits.MaximumProgrammes, channels.Sum(channel => channel.Programmes.Count));
    }

    [Fact]
    public async Task StoreRoundTripsReadsCorruptionAsNothingAndDeletesBothFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sp0075-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TvScheduleStore(directory);
            var document = new TvScheduleDocument(TvScheduleDocument.CurrentSchemaVersion, "https://s.test/g.xml", Now, ParseSample());

            Assert.True(await store.SaveScheduleAsync(document));
            Assert.True(await store.SaveBindingsAsync([new TvScheduleBinding("https://a.test/x", null)]));

            var loaded = await store.LoadScheduleAsync();
            Assert.NotNull(loaded);
            Assert.Equal(document.Channels.Count, loaded.Channels.Count);
            Assert.Equal(document.Channels[0].Programmes[0], loaded.Channels[0].Programmes[0]);
            Assert.Null((await store.LoadBindingsAsync()).Single().ScheduleChannelId);

            await File.WriteAllTextAsync(store.SchedulePath, "{ not json");
            Assert.Null(await store.LoadScheduleAsync());

            await File.WriteAllTextAsync(store.SchedulePath, "{\"schemaVersion\":99,\"sourceUrl\":\"x\",\"channels\":[]}");
            Assert.Null(await store.LoadScheduleAsync());

            Assert.Equal(2, store.Delete());
            Assert.False(store.HasAnyFile);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("https://example.test/guide.xml.gz", true)]
    [InlineData("http://example.test/guide.xml", true)]
    [InlineData("file:///C:/guide.xml", false)]
    [InlineData("ftp://example.test/guide.xml", false)]
    [InlineData("guide.xml", false)]
    [InlineData("", false)]
    public void OnlyHttpSourcesAreAccepted(string text, bool accepted)
    {
        Assert.Equal(accepted, TvScheduleService.TryParseSource(text, out _));
    }
}
