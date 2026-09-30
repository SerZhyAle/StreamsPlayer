using System.Globalization;
using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0179: CAPTURE-OUTPUT rules 3-5 for every kind this product writes - rung 1 of the contract's
/// conformance ladder: each kind at a fixed instant, in locales whose calendar or digits are not the
/// invariant ones, with the rule 5 ordinal.
/// </summary>
public sealed class CaptureFileNameTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 26, 14, 30, 15, TimeSpan.FromHours(2));

    public static TheoryData<CaptureKind, string?, string?, string> EveryKind => new()
    {
        { CaptureKind.VideoFrame, "BBC News", null, "video_frame_260926_143015_BBC News.jpg" },
        { CaptureKind.StreamVideo, "BBC News", ".ts", "stream_video_260926_143015_BBC News.ts" },
        { CaptureKind.StreamVideo, "BBC News", null, "stream_video_260926_143015_BBC News.mp4" },
        { CaptureKind.StreamAudio, "Radio Paradise", ".aac", "stream_audio_260926_143015_Radio Paradise.aac" },
        { CaptureKind.VideoFrame, null, null, "video_frame_260926_143015.jpg" }
    };

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void For_FollowsTheContractGrammar(CaptureKind kind, string? source, string? extension, string expected) =>
        Assert.Equal(expected, CaptureFileName.For(kind, StartedAt, source, extension));

    /// <summary>
    /// ar-SA counts years in the Umm al-Qura calendar and fa-IR in the Persian one, and both have native
    /// digits: a stamp formatted in the interface culture would name 2026 as 1448 or 1405.
    /// </summary>
    [Theory]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("hi-IN")]
    [InlineData("th-TH")]
    public void For_IsTheSameInEveryInterfaceCulture(string culture)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal("video_frame_260926_143015_BBC News.jpg", CaptureFileName.For(CaptureKind.VideoFrame, StartedAt, "BBC News"));
            Assert.Equal("stream_audio_260926_143015 (2).mp3",
                CaptureFileName.WithOrdinal(CaptureFileName.For(CaptureKind.StreamAudio, StartedAt, null, ".mp3"), 2));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void For_StampsTheLocalWallClockOfTheStart() =>
        Assert.Equal("stream_video_260926_233015.mp4",
            CaptureFileName.For(CaptureKind.StreamVideo, new DateTimeOffset(2026, 9, 26, 23, 30, 15, TimeSpan.FromHours(-7)), null));

    [Theory]
    [InlineData("News/Sport", "News_Sport")]
    [InlineData("Radio: live", "Radio_ live")]
    [InlineData("A<B>C|D?E*F\"G\\H", "A_B_C_D_E_F_G_H")]
    [InlineData("Tabs\tand\nnewlines", "Tabs_and_newlines")]
    [InlineData("  Channel  ", "Channel")]
    [InlineData("Channel.", "Channel")]
    [InlineData("Channel .. ", "Channel")]
    [InlineData(".hidden", ".hidden")]
    public void Label_ReplacesWhatNoFileSystemAccepts(string source, string expected) =>
        Assert.Equal(expected, CaptureFileName.Label(source));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData(" . ")]
    public void Label_IsOmittedWhenNothingIsLeft(string? source)
    {
        Assert.Null(CaptureFileName.Label(source));
        Assert.Equal("video_frame_260926_143015.jpg", CaptureFileName.For(CaptureKind.VideoFrame, StartedAt, source));
    }

    [Fact]
    public void Label_IsBoundedSoThePathStaysOpenable() =>
        Assert.Equal(CaptureFileName.MaxLabelLength, CaptureFileName.Label(new string('x', 300))!.Length);

    [Fact]
    public void Label_IsCutBeforeACharacterThatWouldNotFit()
    {
        const string Emoji = "\U0001F4FB";
        Assert.Equal(new string('a', 79), CaptureFileName.Label(new string('a', 79) + Emoji + "tail"));
    }

    [Fact]
    public void Label_CutThatEndsOnASpaceIsTrimmed() =>
        Assert.Equal(new string('a', 78), CaptureFileName.Label(new string('a', 78) + "  tail"));

    [Theory]
    [InlineData("TS", ".ts")]
    [InlineData(".MP4", ".mp4")]
    [InlineData(" .ts ", ".ts")]
    public void For_WritesTheExtensionInLowercase(string extension, string expected) =>
        Assert.EndsWith(expected, CaptureFileName.For(CaptureKind.StreamVideo, StartedAt, "X", extension), StringComparison.Ordinal);

    [Fact]
    public void For_AFrameIsAlwaysJpg() =>
        Assert.EndsWith(".jpg", CaptureFileName.For(CaptureKind.VideoFrame, StartedAt, "X", ".JPEG"), StringComparison.Ordinal);

    [Fact]
    public void For_AnAudioRecordingWithoutItsFormatIsRefused() =>
        Assert.Throws<ArgumentException>(() => CaptureFileName.For(CaptureKind.StreamAudio, StartedAt, "X"));

    [Fact]
    public void WithOrdinal_PutsASpaceAndTheOrdinalBeforeTheExtension() =>
        Assert.Equal("video_frame_260926_143015_BBC News (2).jpg", CaptureFileName.WithOrdinal("video_frame_260926_143015_BBC News.jpg", 2));

    [Fact]
    public void FirstFree_TakesTheNameWhenItIsFree() =>
        Assert.Equal("a.jpg", CaptureFileName.FirstFree("a.jpg", _ => false));

    [Fact]
    public void FirstFree_TakesTheFirstOrdinalTheFolderDoesNotHold()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.jpg", "a (2).jpg", "A (3).JPG" };
        Assert.Equal("a (4).jpg", CaptureFileName.FirstFree("a.jpg", existing.Contains));
    }

    [Fact]
    public void FirstFree_GivesUpAfterTheLastOrdinal() =>
        Assert.Null(CaptureFileName.FirstFree("a.jpg", _ => true));
}
