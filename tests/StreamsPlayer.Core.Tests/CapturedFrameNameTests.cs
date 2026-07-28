using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0038: the frame file name. The catalog is full of titles carrying slashes, colons and quotes, and
/// every one of them has to land as a file the user can open.
/// </summary>
public sealed class CapturedFrameNameTests
{
    private static readonly DateTimeOffset Captured =
        new(2026, 7, 28, 14, 53, 42, TimeSpan.FromHours(3));

    [Fact]
    public void For_JoinsTheTitleAndTheCaptureTime() =>
        Assert.Equal("BBC News_20260728-145342.jpg", CapturedFrameName.For("BBC News", Captured));

    [Theory]
    [InlineData("News/Sport", "News Sport_20260728-145342.jpg")]
    [InlineData("Radio: live", "Radio live_20260728-145342.jpg")]
    [InlineData("A<B>C|D?E*F\"G", "A B C D E F G_20260728-145342.jpg")]
    [InlineData("Tabs\tand\nnewlines", "Tabs and newlines_20260728-145342.jpg")]
    public void For_ReplacesEveryCharacterAFileNameCannotHold(string title, string expected) =>
        Assert.Equal(expected, CapturedFrameName.For(title, Captured));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    public void For_FallsBackWhenNothingUsableIsLeft(string? title) =>
        Assert.Equal("Stream_20260728-145342.jpg", CapturedFrameName.For(title, Captured));

    [Theory]
    [InlineData("Channel.")]
    [InlineData("Channel ")]
    [InlineData("Channel .. ")]
    public void For_NeverEndsTheTitleInASpaceOrDot(string title) =>
        Assert.Equal("Channel_20260728-145342.jpg", CapturedFrameName.For(title, Captured));

    [Fact]
    public void For_BoundsTheTitleSoTheWholePathStaysOpenable()
    {
        var name = CapturedFrameName.For(new string('x', 300), Captured);

        Assert.Equal(80 + "_20260728-145342.jpg".Length, name.Length);
        Assert.EndsWith("_20260728-145342.jpg", name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two captures a second apart must not collide; within the same second the caller is expected to
    /// deduplicate, which is what the file-writing side does.
    /// </summary>
    [Fact]
    public void For_DistinguishesCapturesBySecond() =>
        Assert.NotEqual(
            CapturedFrameName.For("Channel", Captured),
            CapturedFrameName.For("Channel", Captured.AddSeconds(1)));
}
