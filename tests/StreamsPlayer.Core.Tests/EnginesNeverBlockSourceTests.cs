using System.Text.RegularExpressions;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0165, enforced over the App's own sources (read as text - see <see cref="AppSourceFile"/>): no engine
/// call that can block on the network or a native thread is reachable from the UI thread, and every off-thread
/// engine stop carries a deadline with abandonment counted. The App has no test project of its own, so these
/// gates are what stop the next edit from quietly undoing the rule.
/// </summary>
public sealed class EnginesNeverBlockSourceTests
{
    private static AppSourceFile Source(string name) =>
        AppSourceFile.LoadAll(name).Single();

    /// <summary>The masked text of the first block-bodied member called <paramref name="name"/>.</summary>
    private static string Body(AppSourceFile source, string name)
    {
        var declaration = Regex.Match(
            source.Masked,
            $@"\b(?:private\s+)?(?:unsafe\s+)?(?:void|Task(?:<[^>]+>)?|string\??)\s+{Regex.Escape(name)}\s*\(");
        Assert.True(declaration.Success, $"{source.Name}: no block-bodied member named {name}.");
        return BlockFrom(source.Masked, declaration.Index);
    }

    /// <summary>The masked text after the first <c>=></c> following a member declaration, to its closing semicolon.</summary>
    private static string ExpressionBody(AppSourceFile source, string declarationPattern)
    {
        var declaration = Regex.Match(source.Masked, declarationPattern);
        Assert.True(declaration.Success, $"{source.Name}: no member matching {declarationPattern}.");
        var arrow = source.Masked.IndexOf("=>", declaration.Index);
        Assert.True(arrow >= 0, $"{source.Name}: the member is not expression-bodied.");
        var end = source.Masked.IndexOf(';', arrow);
        Assert.True(end >= 0, $"{source.Name}: no statement end after the arrow.");
        return source.Masked[arrow..(end + 1)];
    }

    private static string BlockFrom(string masked, int from)
    {
        var open = masked.IndexOf('{', from);
        Assert.True(open >= 0, $"{masked[..from][(from - 60)..]}: no block body.");
        var depth = 0;
        for (var position = open; position < masked.Length; position++)
        {
            depth += masked[position] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return masked[open..(position + 1)];
            }
        }

        throw new InvalidOperationException("Unbalanced braces in the masked source.");
    }

    [Fact]
    public void TheFlyleafNowPlayingReadNeverTouchesTheDemuxerFromTheUiThread()
    {
        var source = Source("FlyleafVideoBackend.cs");

        // The UI thread's stats tick calls this: it may only return the value the sampler published.
        var read = ExpressionBody(source, @"public\s+string\?\s+ReadNowPlaying\(\)");
        Assert.Contains("_publishedNowPlaying", read);
        Assert.DoesNotContain("lockFmtCtx", read);

        // The demuxer lock lives in exactly one place: the sampler's private read, called from the sampler
        // loop only. Two mentions in code - its declaration and its one call site.
        var calls = Regex.Matches(source.Masked, @"ReadNowPlayingUnderLock").ToList();
        Assert.Equal(2, calls.Count);

        var underLock = Body(source, "ReadNowPlayingUnderLock");
        Assert.Contains("lockFmtCtx", underLock);
    }

    [Fact]
    public void TheFlyleafSamplerContainsItsOwnFaults()
    {
        var body = Body(Source("FlyleafVideoBackend.cs"), "RunNowPlayingSamplerAsync");

        Assert.Contains("catch (Exception", body);
        Assert.Contains("Task.Delay", body);
    }

    [Fact]
    public void TheRadioStopNeverCallsTheEngineFromTheCallingThread()
    {
        var source = Source("StandardAudioPlayback.cs");

        // Every stop, switch, failure, end, open timeout and close funnels through StopPlayback; Dispose
        // rides it too. Neither may reach the native stop or release directly.
        Assert.DoesNotContain("Player.Stop", Body(source, "StopPlayback"));
        Assert.DoesNotContain("Player.Dispose", Body(source, "StopPlayback"));
        Assert.DoesNotContain("Player.Stop", Body(source, "Dispose"));
        Assert.DoesNotContain("Player.Dispose", Body(source, "Dispose"));

        // The retirement worker is where the blocking stop runs, under a deadline, with abandonment counted.
        var retire = Body(source, "RetireAsync");
        Assert.Contains("WaitAsync", retire);
        Assert.Contains("RecordAbandoned", retire);

        // And the native release exists where the retirement path expects it: the leg's own Stop, then the disposal.
        var release = Body(source, "Release");
        Assert.Contains("Stop()", release);
        Assert.Contains("Player.Dispose", release);
    }

    [Fact]
    public void TheAboutProbeStopsUnderADeadlineAndPausesPastTheCap()
    {
        var source = Source("StreamTransmissionProbe.cs");

        var release = Body(source, "ReleaseAsync");
        Assert.Contains("WaitAsync", release);
        Assert.Contains("RecordAbandoned", release);

        var measure = Body(source, "MeasureCoreAsync");
        Assert.Contains("IsPaused", measure);
    }
}
