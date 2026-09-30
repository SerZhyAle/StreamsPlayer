namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0134 requirement 4: every length cap cuts on a whole character, and the mail link's cap is measured
/// on what the mail client receives.
/// </summary>
public sealed class TextBoundaryTests
{
    // U+1F600 GRINNING FACE: two UTF-16 units, the case a plain text[..n] splits.
    private const string Emoji = "\U0001F600";

    // U+1F44D THUMBS UP + U+1F3FD skin tone: one character on screen, four UTF-16 units.
    private const string ThumbsUpMediumSkin = "\U0001F44D\U0001F3FD";

    private static readonly DateTimeOffset At = new(2026, 9, 26, 14, 30, 15, TimeSpan.Zero);

    [Fact]
    public void Truncate_ReturnsTextThatFitsUnchanged() =>
        Assert.Equal("abc", TextBoundary.Truncate("abc", 3));

    [Fact]
    public void Truncate_StopsBeforeAnEmojiThatStraddlesTheCap()
    {
        var text = new string('a', 9) + Emoji + "b";

        Assert.Equal(new string('a', 9), TextBoundary.Truncate(text, 10));
        Assert.Equal(new string('a', 9) + Emoji, TextBoundary.Truncate(text, 11));
    }

    [Fact]
    public void Truncate_KeepsAModifierWithTheCharacterItModifies()
    {
        var text = "ab" + ThumbsUpMediumSkin + "c";

        // A cap that would keep the thumb and drop its skin tone keeps neither.
        Assert.Equal("ab", TextBoundary.Truncate(text, 4));
        Assert.Equal("ab", TextBoundary.Truncate(text, 5));
        Assert.Equal("ab" + ThumbsUpMediumSkin, TextBoundary.Truncate(text, 6));
    }

    [Fact]
    public void Truncate_KeepsACombiningMarkWithItsLetter() =>
        // "e" + COMBINING ACUTE ACCENT is one character; a cap between them keeps neither.
        Assert.Equal("ab", TextBoundary.Truncate("abé", 3));

    [Fact]
    public void BroadcastText_TitleWithAnEmojiAtTheCap_IsCutBeforeTheEmoji()
    {
        var title = new string('a', 79) + Emoji + " and more";

        var sanitized = BroadcastText.Sanitize(title, 80);

        Assert.Equal(new string('a', 79), sanitized);
    }

    [Fact]
    public void BroadcastText_TrailingSpaceLeftByTheCut_IsTrimmed() =>
        Assert.Equal("abc", BroadcastText.Sanitize("abc " + Emoji, 5));

    [Fact]
    public void CaptureFileName_RecordingTitleWithAnEmojiAtTheCap_IsCutBeforeTheEmoji()
    {
        var name = CaptureFileName.For(CaptureKind.StreamVideo, At, new string('a', 79) + Emoji + "tail", ".mp4");

        Assert.Equal("stream_video_260926_143015_" + new string('a', 79) + ".mp4", name);
    }

    [Fact]
    public void CaptureFileName_FrameTitleWithAnEmojiAtTheCap_IsCutBeforeTheEmoji()
    {
        var name = CaptureFileName.For(CaptureKind.VideoFrame, At, new string('a', 79) + Emoji + "tail");

        Assert.Equal("video_frame_260926_143015_" + new string('a', 79) + ".jpg", name);
    }

    [Fact]
    public void MailLink_CyrillicBody_StaysUnderTheEscapedCap()
    {
        // Each Cyrillic letter escapes to six characters (%D0%B6), so 1500 of them - inside the old
        // raw-length cap - made a 9000-character body.
        var body = new string('ж', 1500);

        var link = DiagnosticMailLink.Build("a@b.invalid", "s", body);

        var escapedBody = link[(link.IndexOf("&body=", StringComparison.Ordinal) + "&body=".Length)..];
        Assert.InRange(escapedBody.Length, DiagnosticMailLink.MaxEscapedBodyCharacters - 5, DiagnosticMailLink.MaxEscapedBodyCharacters);
        Assert.Equal(body[..(escapedBody.Length / 6)], Uri.UnescapeDataString(escapedBody));
    }

    [Fact]
    public void MailLink_EmojiAtTheCap_IsNeverHalved()
    {
        // 12 escaped characters per emoji (four UTF-8 bytes): whatever the cap, a cut through one would
        // escape its lone half as the replacement character %EF%BF%BD.
        var body = string.Concat(Enumerable.Repeat("a" + Emoji, 400));

        var link = DiagnosticMailLink.Build("a@b.invalid", "s", body);

        Assert.DoesNotContain("%EF%BF%BD", link, StringComparison.Ordinal);
        var escapedBody = link[(link.IndexOf("&body=", StringComparison.Ordinal) + "&body=".Length)..];
        Assert.True(escapedBody.Length <= DiagnosticMailLink.MaxEscapedBodyCharacters);
    }
}
