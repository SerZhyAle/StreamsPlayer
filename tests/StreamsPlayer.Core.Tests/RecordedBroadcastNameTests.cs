using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0101: the recorded broadcast file name. Checks sanitization, timestamp formatting, extension
/// normalisation, title bounds, and second-level precision.
/// </summary>
public sealed class RecordedBroadcastNameTests
{
    private static readonly DateTimeOffset RecordedAt =
        new(2026, 9, 16, 14, 30, 15, TimeSpan.FromHours(2));

    [Fact]
    public void For_JoinsTitleAndTimestampWithDefaultExtension() =>
        Assert.Equal("BBC News_20260916-143015.mp4", RecordedBroadcastName.For("BBC News", RecordedAt));

    [Fact]
    public void For_UsesExplicitAudioExtension() =>
        Assert.Equal("Radio Paradise_20260916-143015.mp3", RecordedBroadcastName.For("Radio Paradise", RecordedAt, ".mp3"));

    [Fact]
    public void For_NormalizesExtensionWithoutLeadingDot() =>
        Assert.Equal("Live Cam_20260916-143015.ts", RecordedBroadcastName.For("Live Cam", RecordedAt, "ts"));

    [Theory]
    [InlineData("News/Sport", "News Sport_20260916-143015.mp4")]
    [InlineData("Radio: live", "Radio live_20260916-143015.mp4")]
    [InlineData("A<B>C|D?E*F\"G", "A B C D E F G_20260916-143015.mp4")]
    [InlineData("Tabs\tand\nnewlines", "Tabs and newlines_20260916-143015.mp4")]
    public void For_ReplacesEveryCharacterAFileNameCannotHold(string title, string expected) =>
        Assert.Equal(expected, RecordedBroadcastName.For(title, RecordedAt));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    public void For_FallsBackWhenNothingUsableIsLeft(string? title) =>
        Assert.Equal("Stream_20260916-143015.mp4", RecordedBroadcastName.For(title, RecordedAt));

    [Theory]
    [InlineData("Channel.")]
    [InlineData("Channel ")]
    [InlineData("Channel .. ")]
    public void For_NeverEndsTitleInSpaceOrDot(string title) =>
        Assert.Equal("Channel_20260916-143015.mp4", RecordedBroadcastName.For(title, RecordedAt));

    [Fact]
    public void For_BoundsTheTitleSoThePathStaysOpenable()
    {
        var name = RecordedBroadcastName.For(new string('x', 300), RecordedAt);

        Assert.Equal(80 + "_20260916-143015.mp4".Length, name.Length);
        Assert.EndsWith("_20260916-143015.mp4", name, StringComparison.Ordinal);
    }

    [Fact]
    public void For_DistinguishesRecordingsBySecond() =>
        Assert.NotEqual(
            RecordedBroadcastName.For("Channel", RecordedAt),
            RecordedBroadcastName.For("Channel", RecordedAt.AddSeconds(1)));
}
