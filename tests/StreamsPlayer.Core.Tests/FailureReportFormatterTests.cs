using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

public sealed class FailureReportFormatterTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 20, 8, 30, 15, TimeSpan.Zero);

    [Fact]
    public void Format_ContainsAllContractFields()
    {
        var report = new FailureReport(
            "26.0720.0830",
            Timestamp,
            "Canal+ Foot",
            "https://cdn.example/live.m3u8",
            MediaKind.Video,
            PlaybackErrorCategory.Unsupported);

        var text = FailureReportFormatter.Format(report);

        Assert.Contains("26.0720.0830", text);
        Assert.Contains("2026-07-20 08:30:15Z", text);
        Assert.Contains("Canal+ Foot", text);
        Assert.Contains("Video", text);
        Assert.Contains("Unsupported", text);
        Assert.Contains("https://cdn.example/live.m3u8", text);
    }

    [Fact]
    public void Format_RedactsCredentialsInUrl()
    {
        var report = new FailureReport(
            "26.0720.0830",
            Timestamp,
            "Secret Feed",
            "https://alice:s3cr3t@cdn.example/live?token=ABC123",
            MediaKind.Video,
            PlaybackErrorCategory.MediaError);

        var text = FailureReportFormatter.Format(report);

        Assert.DoesNotContain("s3cr3t", text);
        Assert.DoesNotContain("alice", text);
        Assert.DoesNotContain("ABC123", text);
        Assert.Contains("token=***", text);
    }

    [Theory]
    [InlineData("play_rejected", PlaybackErrorCategory.Rejected)]
    [InlineData("encountered_error", PlaybackErrorCategory.MediaError)]
    [InlineData("NotSupportedException", PlaybackErrorCategory.Unsupported)]
    [InlineData("HttpRequestException: connection timeout", PlaybackErrorCategory.Network)]
    [InlineData("COMException", PlaybackErrorCategory.MediaError)]
    [InlineData("", PlaybackErrorCategory.Unknown)]
    [InlineData("something weird", PlaybackErrorCategory.Unknown)]
    public void Classify_MapsKnownReasonsStably(string reason, PlaybackErrorCategory expected)
    {
        Assert.Equal(expected, PlaybackErrorClassifier.Classify(reason));
    }
}
