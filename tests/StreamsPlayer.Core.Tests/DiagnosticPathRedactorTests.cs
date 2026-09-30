using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0137: DIAGNOSTIC-REPORT rule 3, path half - the account name never leaves in a log.</summary>
public sealed class DiagnosticPathRedactorTests
{
    private const string Profile = @"C:\Users\Jane Doe";
    private const string AppData = @"C:\Users\Jane Doe\AppData\Local\StreamsPlayer";

    private static readonly DiagnosticPathRedactor Redactor = new(AppData, Profile);

    [Theory]
    [InlineData(@"file=C:\Users\Jane Doe\Music\a.mp3 | ok=true", @"file=<USER>\Music\a.mp3 | ok=true")]
    [InlineData(@"log=C:\Users\Jane Doe\AppData\Local\StreamsPlayer\Current.log", @"log=<APP_DATA>\Current.log")]
    [InlineData(@"dir=C:\Users\Jane Doe\AppData\Local\StreamsPlayer", @"dir=<APP_DATA>")]
    [InlineData(@"dir=c:\users\jane doe\videos", @"dir=<USER>\videos")]
    [InlineData("url=file:///C:/Users/Jane Doe/Music/a.mp3", "url=file:///<USER>/Music/a.mp3")]
    [InlineData(@"'C:\Users\Jane Doe\Pictures\StreamsPlayer'", @"'<USER>\Pictures\StreamsPlayer'")]
    [InlineData(@"at X() in C:\Users\Jane Doe\src\a.cs:line 4", @"at X() in <USER>\src\a.cs:line 4")]
    public void Redact_ReplacesTheKnownRootsAndKeepsThePathTail(string line, string expected)
    {
        Assert.Equal(expected, Redactor.Redact(line));
    }

    [Theory]
    // 8.3 short name, as GetTempPath often reports the profile.
    [InlineData(@"tmp=C:\Users\JANEDO~1\AppData\Local\Temp\x.tmp", @"tmp=<USER>\AppData\Local\Temp\x.tmp")]
    // Another account, or a log kept from before the profile was renamed.
    [InlineData(@"file=D:\Users\bob\Music\a.mp3", @"file=<USER>\Music\a.mp3")]
    public void Redact_ReplacesAnyOtherProfileRoot(string line, string expected)
    {
        Assert.Equal(expected, Redactor.Redact(line));
    }

    [Fact]
    public void Redact_ARootMatchesOnlyAWholeSegment()
    {
        var redactor = new DiagnosticPathRedactor(@"D:\Data\StreamsPlayer", @"C:\Users\ann");

        Assert.Equal(@"<APP_DATA>\Current.log", redactor.Redact(@"D:\Data\StreamsPlayer\Current.log"));
        Assert.Equal(@"D:\Data\StreamsPlayerBackup\x", redactor.Redact(@"D:\Data\StreamsPlayerBackup\x"));
        // Not the running user's root, but still a profile: the fallback takes it whole, never "<USER>a".
        Assert.Equal(@"<USER>\x", redactor.Redact(@"C:\Users\anna\x"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\StreamsPlayer\StreamsPlayer.exe")]
    [InlineData("url=https://host/Users/bob/stream.m3u8")]
    [InlineData("[Diag] PLAYER OPEN | url=rtsp://camera.local/stream1 | backend=libvlc")]
    [InlineData("")]
    public void Redact_LeavesTextWithoutAProfilePathUnchanged(string line)
    {
        Assert.Equal(line, Redactor.Redact(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\")]
    [InlineData(@"\\server")]
    public void Redact_ARootThatWouldClaimAWholeDriveIsIgnored(string? root)
    {
        var redactor = new DiagnosticPathRedactor(root, root);

        Assert.Equal(@"C:\Program Files\x", redactor.Redact(@"C:\Program Files\x"));
        Assert.Equal(@"<USER>\x", redactor.Redact(@"C:\Users\bob\x"));
    }

    [Fact]
    public void Redact_ARelocatedDataDirectoryOutsideTheProfileIsStillTheDataDirectory()
    {
        var redactor = new DiagnosticPathRedactor(@"\\nas\share\sp-data", Profile);

        Assert.Equal(@"<APP_DATA>\Current.log", redactor.Redact(@"\\nas\share\sp-data\Current.log"));
    }

    // SP-0174 (G-01): a redaction that cannot finish in time must cost only the line it strikes, never
    // the whole chunk - the archive's reason to exist is the log body a whole-chunk replacement dropped.
    // A 1 ms budget splits the chunk deterministically: a 16 MB line of profile-shaped text (the log
    // ceiling) cannot be scanned that fast, while the short lines around it finish in microseconds.
    [Fact]
    public void Redact_ATimeoutReplacesOnlyTheLineItStruckAndSaysSo()
    {
        var redactor = new DiagnosticPathRedactor(AppData, Profile, TimeSpan.FromMilliseconds(1));
        var hugeLine = string.Concat(Enumerable.Repeat(@"C:\Users\zz ", 2 * 1024 * 1024));
        var chunk =
            "first line survived\r\n" +
            hugeLine + "\r\n" +
            @"third holds C:\Users\Jane Doe\Music\a.mp3" + "\r\n";

        var redacted = redactor.Redact(chunk);

        var lines = redacted.Split("\r\n");
        Assert.Equal(4, lines.Length);
        Assert.Equal("first line survived", lines[0]);
        Assert.StartsWith(DiagnosticPathRedactor.TimeoutMarker, lines[1], StringComparison.Ordinal);
        Assert.Equal(
            hugeLine.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            lines[1][DiagnosticPathRedactor.TimeoutMarker.Length..]);
        Assert.Equal(@"third holds <USER>\Music\a.mp3", lines[2]);
    }

    // Several lines can strike the budget in one chunk; each falls alone, and a clean line between
    // two struck ones comes through untouched. One tick would be too small to force: the engine
    // checks the clock only between matches, so a short line always finishes first - which is the
    // point: only genuinely unfinishable lines fall.
    [Fact]
    public void Redact_EachStruckLineFallsAlone()
    {
        var redactor = new DiagnosticPathRedactor(AppData, Profile, TimeSpan.FromMilliseconds(1));
        var huge = string.Concat(Enumerable.Repeat(@"C:\Users\zz ", 1024 * 1024));
        var redacted = redactor.Redact(huge + "\r\nbetween\r\n" + huge + "\r\n");

        var lines = redacted.Split("\r\n");
        Assert.Equal(4, lines.Length);
        Assert.StartsWith(DiagnosticPathRedactor.TimeoutMarker, lines[0], StringComparison.Ordinal);
        Assert.Equal("between", lines[1]);
        Assert.StartsWith(DiagnosticPathRedactor.TimeoutMarker, lines[2], StringComparison.Ordinal);
        Assert.Equal(string.Empty, lines[3]);
    }

    // The default redactor must not be one that times out on contact.
    [Fact]
    public void Redact_TheDefaultTimeoutStillRedacts()
    {
        Assert.Equal(
            @"<USER>\x",
            new DiagnosticPathRedactor(null, Profile).Redact(@"C:\Users\Jane Doe\x"));
    }
}
