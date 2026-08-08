using System.Globalization;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0053: the About window's stored half is a pure projection, so its contract is testable without
/// WPF. What matters here is that every property reaches the window exactly once, that an unset
/// property still occupies a line, and that a claim is never dressed up as a measurement.
/// </summary>
public sealed class ChannelFactSheetTests
{
    private static readonly DateTimeOffset Added = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void EveryStoredPropertyReachesTheSheetExactlyOnce()
    {
        var facts = ChannelFactSheet.Describe(FullChannel(), ["Morning", "Jazz"], CultureInfo.InvariantCulture);

        var labels = facts.Select(fact => fact.LabelKey).ToArray();
        Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("Radio One", Value(facts, "FieldTitle"));
        Assert.Equal("https://example.test/one", Value(facts, "FieldAddress"));
        Assert.Equal("Morning, Jazz", Value(facts, "FieldCollections"));
        // SP-0061: a rubric of the bank's closed set arrives as a key, not as the English word.
        Assert.Equal("TopicNews", Fact(facts, "FieldTopic").ValueKey);
        Assert.Equal("Talk", Value(facts, "Category"));
        Assert.Equal("English", Value(facts, "Language"));
        Assert.Equal("UK", Value(facts, "Country"));
        Assert.Equal("https://example.test", Value(facts, "FieldHomepage"));
        Assert.Equal("HTTPS", Value(facts, "FieldProtocol"));
        Assert.Equal("AAC", Value(facts, "FieldFormat"));
    }

    [Fact]
    public void AnEmptyChannelKeepsTheSameShapeWithEmptyValues()
    {
        var full = ChannelFactSheet.Describe(FullChannel(), ["Morning"], CultureInfo.InvariantCulture);
        var bare = ChannelFactSheet.Describe(BareChannel(), [], CultureInfo.InvariantCulture);

        Assert.Equal(
            full.Select(fact => fact.LabelKey),
            bare.Select(fact => fact.LabelKey));
        Assert.Equal(string.Empty, Value(bare, "FieldTopic"));
        Assert.Equal(string.Empty, Value(bare, "FieldCollections"));
        Assert.Equal(string.Empty, Value(bare, "FieldLastPlayed"));
        // The catalog's live column is three-valued; an unstated stream must not be called on demand.
        Assert.Equal(string.Empty, Value(bare, "FieldLive"));
    }

    [Theory]
    [InlineData(MediaKind.Audio, "KindAudio")]
    [InlineData(MediaKind.Video, "KindVideo")]
    [InlineData(MediaKind.Rtsp, "KindRtsp")]
    public void TheStoredKindBecomesAWord(MediaKind kind, string expected)
    {
        var facts = ChannelFactSheet.Describe(BareChannel() with { MediaKind = kind }, [], CultureInfo.InvariantCulture);

        Assert.Equal(expected, Fact(facts, "FieldType").ValueKey);
    }

    [Theory]
    [InlineData(SourceOrigin.Catalog, "OriginCatalog")]
    [InlineData(SourceOrigin.Manual, "OriginManual")]
    [InlineData(SourceOrigin.Imported, "OriginImported")]
    public void TheStoredOriginBecomesAWord(SourceOrigin origin, string expected)
    {
        var facts = ChannelFactSheet.Describe(BareChannel() with { SourceOrigin = origin }, [], CultureInfo.InvariantCulture);

        Assert.Equal(expected, Fact(facts, "FieldOrigin").ValueKey);
    }

    [Fact]
    public void TheOutcomeAndTheAccessTagBecomeWords()
    {
        var ok = ChannelFactSheet.Describe(BareChannel() with { LastPlayOutcome = PlayOutcome.Ok }, [], CultureInfo.InvariantCulture);
        var failed = ChannelFactSheet.Describe(BareChannel() with { LastPlayOutcome = PlayOutcome.Fail }, [], CultureInfo.InvariantCulture);
        var never = ChannelFactSheet.Describe(BareChannel(), [], CultureInfo.InvariantCulture);
        var locked = ChannelFactSheet.Describe(BareChannel() with { Access = ChannelAccess.GeoRestricted }, [], CultureInfo.InvariantCulture);

        Assert.Equal("StatusVerified", Fact(ok, "FieldLastOutcome").ValueKey);
        Assert.Equal("StatusFailed", Fact(failed, "FieldLastOutcome").ValueKey);
        Assert.Equal("StatusNotPlayed", Fact(never, "FieldLastOutcome").ValueKey);
        Assert.Equal("AboutAccessOpen", Fact(never, "FieldAccess").ValueKey);
        Assert.Equal("RegionRestrictedLabel", Fact(locked, "FieldAccess").ValueKey);
    }

    [Fact]
    public void ARubricOutsideTheClosedSetIsShownAsWritten()
    {
        // SP-0061 criterion 4. A catalog newer than this build, and a hand-typed rubric on a manually
        // added channel, both land here. Showing the raw value is the contract; substituting "General"
        // would invent a claim, and dropping it would hide what the maintainer said.
        var unknown = ChannelFactSheet.Describe(
            BareChannel() with { Topic = "Sea shanties" }, [], CultureInfo.InvariantCulture);

        Assert.Null(Fact(unknown, "FieldTopic").ValueKey);
        Assert.Equal("Sea shanties", Value(unknown, "FieldTopic"));
    }

    [Fact]
    public void ANumericBitrateClaimGetsTheSharedUnitAndAnythingElseIsPassedThrough()
    {
        var numeric = ChannelFactSheet.Describe(BareChannel() with { Bitrate = "128k" }, [], CultureInfo.InvariantCulture);
        var prose = ChannelFactSheet.Describe(BareChannel() with { Bitrate = "variable" }, [], CultureInfo.InvariantCulture);

        Assert.Equal("BitrateValue", Fact(numeric, "FieldBitrate").ValueFormatKey);
        Assert.Equal("128", Fact(numeric, "FieldBitrate").Text);
        Assert.Null(Fact(prose, "FieldBitrate").ValueFormatKey);
        Assert.Equal("variable", Fact(prose, "FieldBitrate").Text);
    }

    [Fact]
    public void AnUnmeasuredStreamIsOneLineSayingSo()
    {
        var facts = ChannelFactSheet.DescribeTransmission(null, measured: false, CultureInfo.InvariantCulture);

        var only = Assert.Single(facts);
        Assert.Equal(ChannelFactGroup.Stream, only.Group);
        Assert.Equal("AboutUnavailable", only.ValueKey);
        // Labelled with the group, not with a property: the stream was not reached, so nothing is
        // known about its video format in particular.
        Assert.Equal("AboutGroupStream", only.LabelKey);
    }

    [Fact]
    public void AMeasurementReportsOnlyWhatTheEngineSupplied()
    {
        var facts = ChannelFactSheet.DescribeTransmission(
            new StreamTransmission("H264 - MPEG-4 AVC", 1920, 1080, 25, "MPEG AAC Audio", 2, 48000, 3200.5),
            measured: true,
            CultureInfo.InvariantCulture);

        Assert.Equal("H264 - MPEG-4 AVC", Fact(facts, "FieldVideoCodec").Text);
        Assert.Equal("1920×1080", Fact(facts, "FieldResolution").Text);
        Assert.Equal("25", Fact(facts, "FieldFrameRate").Text);
        Assert.Equal("2", Fact(facts, "FieldAudioChannels").Text);
        Assert.Equal("48,000", Fact(facts, "FieldSampleRate").Text);
        Assert.Equal("3200.5", Fact(facts, "FieldObservedBitrate").Text);
    }

    [Fact]
    public void ASilentMeasurementLeavesLinesEmptyRatherThanReportingZero()
    {
        var facts = ChannelFactSheet.DescribeTransmission(
            new StreamTransmission(null, 0, 0, null, null, 0, 0, null),
            measured: true,
            CultureInfo.InvariantCulture);

        Assert.All(facts, fact => Assert.Null(fact.Text));
        Assert.Equal(string.Empty, Value(facts, "FieldResolution"));
        Assert.Equal(string.Empty, Value(facts, "FieldSampleRate"));
    }

    [Fact]
    public void TheRenderedTextCarriesEveryLineAndSeparatesTheGroups()
    {
        var facts = ChannelFactSheet
            .Describe(FullChannel(), ["Morning"], CultureInfo.InvariantCulture)
            .Concat(ChannelFactSheet.DescribeTransmission(null, measured: false, CultureInfo.InvariantCulture))
            .ToArray();

        var text = ChannelFactSheet.Render(facts, key => $"<{key}>");
        var lines = text.Split(Environment.NewLine, StringSplitOptions.None);

        Assert.Contains("<FieldTitle>: Radio One", lines);
        Assert.Contains("<FieldBitrate>: <BitrateValue>", lines);
        Assert.Contains("<AboutGroupStream>: <AboutUnavailable>", lines);
        // One blank line for each group boundary - the claim/measurement split has to survive the copy.
        Assert.Equal(2, lines.Count(line => line.Length == 0) - 1);
    }

    [Fact]
    public void TheValueFormatWrapsTheNumberInItsTranslatedUnit()
    {
        var fact = new ChannelFact(ChannelFactGroup.Stream, "FieldSampleRate", "48,000", ValueFormatKey: "SampleRateValue");

        Assert.Equal("48,000 Hz", ChannelFactSheet.ResolveValue(fact, key => key == "SampleRateValue" ? "{0} Hz" : key));
    }

    private static StreamChannel FullChannel() => new()
    {
        Id = Guid.NewGuid(),
        Url = "https://example.test/one",
        Title = "Radio One",
        MediaKind = MediaKind.Video,
        SourceOrigin = SourceOrigin.Manual,
        AddedAt = Added,
        LastPlayedAt = Added.AddDays(1),
        LastPlayOutcome = PlayOutcome.Ok,
        Pinned = true,
        Category = "Talk",
        Topic = "News",
        Language = "English",
        Country = "UK",
        Homepage = "https://example.test",
        Protocol = "HTTPS",
        Format = "AAC",
        Bitrate = "128k",
        IsLive = true,
        Access = ChannelAccess.GeoRestricted
    };

    private static StreamChannel BareChannel() => new()
    {
        Id = Guid.NewGuid(),
        Url = "https://example.test/bare",
        Title = "Bare",
        MediaKind = MediaKind.Audio,
        SourceOrigin = SourceOrigin.Catalog,
        AddedAt = Added
    };

    private static ChannelFact Fact(IReadOnlyList<ChannelFact> facts, string labelKey) =>
        facts.Single(fact => fact.LabelKey == labelKey);

    private static string Value(IReadOnlyList<ChannelFact> facts, string labelKey) =>
        ChannelFactSheet.ResolveValue(Fact(facts, labelKey), key => key);
}
