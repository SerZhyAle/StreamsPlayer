using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0121: the length a recording notice and the badge state.</summary>
public sealed class RecordingLengthTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59, "00:59")]
    [InlineData(61, "01:01")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(4505, "1:15:05")]
    [InlineData(-5, "00:00")]
    public void FormatsMinutesThenHours(int seconds, string expected) =>
        Assert.Equal(expected, RecordingLength.Format(TimeSpan.FromSeconds(seconds)));
}
