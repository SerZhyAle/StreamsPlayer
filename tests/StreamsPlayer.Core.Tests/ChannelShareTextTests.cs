using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class ChannelShareTextTests
{
    [Theory]
    [InlineData("http://example.test/live")]
    [InlineData("https://example.test/live.m3u8?token=abc")]
    [InlineData("rtsp://example.test/camera")]
    public void RoundTripKeepsTheAddress(string url)
    {
        var read = ChannelShareText.Read(ChannelShareText.Format(url));

        Assert.Equal(ChannelShareStatus.Ok, read.Status);
        Assert.Equal(url, read.Url);
    }

    [Fact]
    public void FormattedTextIsOneLineWithTheVersionedMarker()
    {
        var text = ChannelShareText.Format("  https://example.test/live  ");

        Assert.Equal("SPCH1 https://example.test/live", text);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void BareAddressIsAccepted()
    {
        var read = ChannelShareText.Read("https://example.test/live");

        Assert.Equal(ChannelShareStatus.Ok, read.Status);
        Assert.Equal("https://example.test/live", read.Url);
    }

    [Theory]
    [InlineData("SPCH2 https://example.test/live")]
    [InlineData("SPCH99999999999999 https://example.test/live")]
    public void ANewerFormatIsRefusedRatherThanImported(string text)
    {
        var read = ChannelShareText.Read(text);

        Assert.Equal(ChannelShareStatus.UnsupportedVersion, read.Status);
        Assert.Equal(string.Empty, read.Url);
    }

    [Theory]
    [InlineData("SPCH1")]
    [InlineData("SPCH1 not-a-url")]
    [InlineData("SPCH1 ftp://example.test/live")]
    public void AMarkedTextWithoutALaunchableAddressIsInvalid(string text)
    {
        var read = ChannelShareText.Read(text);

        Assert.Equal(ChannelShareStatus.InvalidAddress, read.Status);
        Assert.Equal(string.Empty, read.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("hello")]
    [InlineData("ftp://example.test/live")]
    [InlineData("file:///c:/movie.mp4")]
    public void UnrelatedTextIsNotAShareText(string? text)
    {
        var read = ChannelShareText.Read(text);

        Assert.Equal(ChannelShareStatus.NotShareText, read.Status);
        Assert.Equal(string.Empty, read.Url);
    }

    [Fact]
    public void SurroundingLinesDoNotDefeatTheRead()
    {
        var read = ChannelShareText.Read("\r\n\r\nSPCH1 https://example.test/live\r\nsent from my phone");

        Assert.Equal(ChannelShareStatus.Ok, read.Status);
        Assert.Equal("https://example.test/live", read.Url);
    }

    [Fact]
    public void MarkerIsCaseInsensitiveAndTheAddressIsReturnedAsWritten()
    {
        var read = ChannelShareText.Read("spch1 HTTPS://EXAMPLE.TEST/live");

        Assert.Equal(ChannelShareStatus.Ok, read.Status);
        Assert.Equal("HTTPS://EXAMPLE.TEST/live", read.Url);
    }

    [Theory]
    [InlineData("https://EXAMPLE.test/live")]
    [InlineData("https://example.test:443/live")]
    public void FindExistingMatchesAcrossHostCaseAndDefaultPort(string pasted)
    {
        var match = ChannelShareText.FindExisting([Channel("https://example.test/live")], [], pasted);

        Assert.NotNull(match.Existing);
        Assert.False(match.Hidden);
    }

    [Theory]
    [InlineData("http://example.test/live")]
    [InlineData("https://example.test/other")]
    [InlineData("https://example.test:8443/live")]
    public void FindExistingDoesNotFoldSchemePathOrExplicitPort(string pasted)
    {
        var match = ChannelShareText.FindExisting([Channel("https://example.test/live")], [], pasted);

        Assert.Null(match.Existing);
        Assert.False(match.Hidden);
    }

    [Fact]
    public void FindExistingReportsAHiddenMatch()
    {
        var match = ChannelShareText.FindExisting(
            [Channel("https://example.test/live")],
            ["https://EXAMPLE.test/live"],
            "https://example.test/live");

        Assert.NotNull(match.Existing);
        Assert.True(match.Hidden);
    }

    [Fact]
    public void FindExistingOnAnEmptyListMatchesNothing()
    {
        var match = ChannelShareText.FindExisting([], [], "https://example.test/live");

        Assert.Null(match.Existing);
        Assert.False(match.Hidden);
    }

    private static StreamChannel Channel(string url) => new()
    {
        Id = Guid.NewGuid(),
        Url = url,
        Title = "Example",
        MediaKind = MediaKind.Audio,
        SourceOrigin = SourceOrigin.Catalog,
        AddedAt = DateTimeOffset.UnixEpoch
    };
}
