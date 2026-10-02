using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0189: the engine's own words about a failed open, as LibVLC 3.0.23 logged them in the research probe
/// (<c>temp/SP-0189/probe-quiet.txt</c>), reduced to the lines that explain the failure.
/// </summary>
public sealed class EngineFailureTrailTests
{
    private static EngineFailureTrail Fed(params (string Module, string? Message)[] lines)
    {
        var trail = new EngineFailureTrail();
        foreach (var (module, message) in lines)
        {
            trail.Observe(module, message);
        }

        return trail;
    }

    [Fact]
    public void ARefusedConnection_IsDescribedByItsCauseNotByTheGenericTrailer()
    {
        var trail = Fed(
            ("access", "HTTP connection failure"),
            ("main", "connection failed: Connection refused by peer"),
            ("http", "cannot connect to 127.0.0.1:54458"),
            ("main", "Your input can't be opened"),
            ("main", "VLC is unable to open the MRL 'http://127.0.0.1:54458/stream'. Check the log for details."));

        Assert.Equal(
            "HTTP connection failure; connection failed: Connection refused by peer; cannot connect to 127.0.0.1:54458",
            trail.Describe());
    }

    [Fact]
    public void TheTrailerCarryingTheWholeAddress_NeverReachesTheCause()
    {
        var trail = Fed(("main", "VLC is unable to open the MRL 'http://user:secret@radio.example/live'. Check the log for details."));

        Assert.Null(trail.Describe());
    }

    [Fact]
    public void AudioOutputLines_SayNothingAboutAFailedOpen()
    {
        var trail = Fed(
            ("mmdevice", "cannot get default device (error 0x80070490)"),
            ("main", "module not functional"),
            ("main", "failed to create audio output"),
            ("access", "HTTP 404 error"),
            ("http", "error: HTTP/1.0 404 Not Found"),
            ("WASAPI", "cannot activate client"));

        Assert.Equal("HTTP 404 error; error: HTTP/1.0 404 Not Found", trail.Describe());
    }

    [Fact]
    public void ANameThatDoesNotResolve_KeepsBothOfItsLines()
    {
        var trail = Fed(
            ("http", "cannot resolve sp0189-no-such-host.invalid: No such host is known. "),
            ("access", "HTTP connection failure"),
            ("main", "cannot resolve sp0189-no-such-host.invalid port 80 : No such host is known. "),
            ("http", "cannot connect to sp0189-no-such-host.invalid:80"));

        Assert.Equal(
            "cannot resolve sp0189-no-such-host.invalid: No such host is known.; HTTP connection failure; " +
            "cannot resolve sp0189-no-such-host.invalid port 80 : No such host is known.; cannot connect to sp0189-no-such-host.invalid:80",
            trail.Describe());
    }

    [Fact]
    public void OnlyTheMostRecentDistinctLinesAreKept_AndARepeatMovesToTheEnd()
    {
        var trail = Fed(
            ("access", "one"),
            ("access", "two"),
            ("access", "one"),
            ("access", "three"),
            ("access", "four"),
            ("access", "five"));

        // MaximumLines is four.
        Assert.Equal("one; three; four; five", trail.Describe());
    }

    [Fact]
    public void Clear_ForgetsThePreviousConnection()
    {
        var trail = Fed(("main", "connection failed: Connection refused by peer"));

        trail.Clear();

        Assert.Null(trail.Describe());
    }

    [Fact]
    public void Describe_IsBounded()
    {
        var trail = Fed(("access", new string('a', 250)), ("access", new string('b', 250)));

        Assert.Equal(EngineFailureTrail.MaximumLength, trail.Describe()!.Length);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("first\r\nsecond\nthird", "first second third")]
    [InlineData("  padded  ", "padded")]
    public void EachLine_IsOneTrimmedLineOrNothing(string? message, string? expected)
    {
        Assert.Equal(expected, Fed(("access", message)).Describe());
    }

    [Fact]
    public void ARunawayLine_IsBounded()
    {
        Assert.Equal(EngineFailureTrail.MaximumLength, Fed(("access", new string('x', 5000))).Describe()!.Length);
    }
}
