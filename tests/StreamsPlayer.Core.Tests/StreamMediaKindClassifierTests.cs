using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class StreamMediaKindClassifierTests
{
    [Theory]
    [InlineData("rtsp://camera/live", MediaKind.Rtsp)]
    [InlineData("https://example.test/live.m3u8?token=abc", MediaKind.Video)]
    [InlineData("https://example.test/MOVIE.MP4#position", MediaKind.Video)]
    [InlineData("https://example.test/radio.mp3", MediaKind.Audio)]
    [InlineData("https://example.test/no-extension", MediaKind.Audio)]
    public void Classify_FollowsContract(string url, MediaKind expected) =>
        Assert.Equal(expected, StreamMediaKindClassifier.Classify(url));

    [Fact]
    public void DeclaredCatalogKindWinsOverExtension() =>
        Assert.Equal(
            MediaKind.Audio,
            StreamMediaKindClassifier.FromCatalogValue("AUDIO", "https://example.test/live.m3u8"));

    [Theory]
    [InlineData("http://example.test/live")]
    [InlineData("https://example.test/live")]
    [InlineData("rtsp://example.test/live")]
    public void OnlySupportedSchemesAreLaunchable(string url) =>
        Assert.True(StreamMediaKindClassifier.IsLaunchable(url));

    [Theory]
    [InlineData("file:///c:/movie.mp4")]
    [InlineData("ftp://example.test/live")]
    [InlineData("not a URL")]
    public void UnsupportedSchemesAreRejected(string url) =>
        Assert.False(StreamMediaKindClassifier.IsLaunchable(url));
}
