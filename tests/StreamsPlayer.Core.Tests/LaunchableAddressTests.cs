namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0124: only an http, https or rtsp address is ever handed to an engine.</summary>
public sealed class LaunchableAddressTests
{
    [Theory]
    [InlineData("file:///C:/x.mp3")]
    [InlineData(@"\\server\share\x.mp3")]
    [InlineData("radio/x.mp3")]
    [InlineData("http://exa mple/x")]
    [InlineData("ftp://example.test/live")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnAddressOutsideTheThreeSchemesIsNotLaunchable(string? url)
    {
        Assert.False(LaunchableAddress.TryParse(url, out var address));
        Assert.Null(address);
        Assert.False(LaunchableAddress.IsLaunchable(url));
        Assert.False(StreamMediaKindClassifier.IsLaunchable(url));
    }

    [Theory]
    [InlineData("http://example.test/live", "http")]
    [InlineData("HTTP://example.test/live", "http")]
    [InlineData("https://example.test/live", "https")]
    [InlineData("HtTpS://example.test/live", "https")]
    [InlineData("rtsp://camera.test/live", "rtsp")]
    [InlineData("RTSP://camera.test/live", "rtsp")]
    [InlineData("  https://example.test/padded  ", "https")]
    public void TheThreeSchemesInAnyLetterCaseAreLaunchable(string url, string scheme)
    {
        Assert.True(LaunchableAddress.TryParse(url, out var address));
        Assert.Equal(scheme, address.Scheme);
    }

    [Theory]
    [InlineData("http://example.test/live", true)]
    [InlineData("HTTPS://example.test/live", true)]
    [InlineData("rtsp://camera.test/live", false)]
    [InlineData("file:///C:/x.mp3", false)]
    public void AnHttpProbeOpensOnlyHttpAndHttps(string url, bool expected) =>
        Assert.Equal(expected, LaunchableAddress.TryParseHttp(url, out _));

    [Theory]
    [InlineData("https://radio.example.test/live.mp3", "radio.example.test")]
    [InlineData(@"\\server\share\x.mp3", @"\\server\share\x.mp3")]
    [InlineData("http://exa mple/x", "http://exa mple/x")]
    public void TheDefaultTitleNeverThrows(string url, string expected) =>
        Assert.Equal(expected, LaunchableAddress.HostOf(url));

    [Theory]
    [InlineData("file:///C:/x.mp3", PlayOutcome.Ok)]
    [InlineData(@"\\server\share\x.mp3", null)]
    [InlineData("http://exa mple/x", PlayOutcome.Fail)]
    public void AnUnlaunchableRowSaysSoInsteadOfItsOutcome(string url, PlayOutcome? outcome)
    {
        var channel = Channel(url) with { LastPlayOutcome = outcome };
        Assert.Equal("StatusNotLaunchable", ChannelFactSheet.OutcomeKey(channel));
        var line = Assert.Single(ChannelFactSheet.Describe(channel, []), fact => fact.LabelKey == "FieldLastOutcome");
        Assert.Equal("StatusNotLaunchable", line.ValueKey);
    }

    [Theory]
    [InlineData(PlayOutcome.Ok, "StatusVerified")]
    [InlineData(PlayOutcome.Fail, "StatusFailed")]
    [InlineData(null, "StatusNotPlayed")]
    public void ALaunchableRowKeepsItsOutcome(PlayOutcome? outcome, string expected) =>
        Assert.Equal(expected, ChannelFactSheet.OutcomeKey(Channel("https://example.test/live") with { LastPlayOutcome = outcome }));

    private static StreamChannel Channel(string url) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = "Row",
        MediaKind = StreamMediaKindClassifier.Classify(url),
        SourceOrigin = SourceOrigin.Imported,
        AddedAt = DateTimeOffset.UnixEpoch
    };
}
