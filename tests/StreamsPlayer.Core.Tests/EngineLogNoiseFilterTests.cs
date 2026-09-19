namespace StreamsPlayer.Core.Tests;

public sealed class EngineLogNoiseFilterTests
{
    private const long Freq = 10_000_000; // 10 MHz ticks -> 1 s = 10,000,000 ticks

    [Fact]
    public void Observe_FirstRecordOfAShape_IsWritten()
    {
        var filter = new EngineLogNoiseFilter();

        var sample = filter.Observe("http", "local stream 3 error: Cancellation (0x8)", 0, Freq);

        Assert.True(sample.ShouldLog);
        Assert.Equal(0, sample.SuppressedRepeats);
    }

    [Fact]
    public void Observe_RepeatsWithinTheWindow_AreSuppressedWhateverTheDigits()
    {
        var filter = new EngineLogNoiseFilter();
        Assert.True(filter.Observe("http", "local stream 3 error: Cancellation (0x8)", 0, Freq).ShouldLog);

        // Same complaint, different stream numbers: the shape is what repeats, not the text.
        Assert.False(filter.Observe("http", "local stream 5 error: Cancellation (0x8)", 2 * Freq, Freq).ShouldLog);
        Assert.False(filter.Observe("http", "local stream 41 error: Cancellation (0x8)", 20 * Freq, Freq).ShouldLog);
    }

    [Fact]
    public void Observe_AfterTheWindow_WritesOnceAndCarriesTheSuppressedCount()
    {
        var filter = new EngineLogNoiseFilter();
        Assert.True(filter.Observe("ts", "libdvbpsi error (TDT/TOT decoder): Already a decoder", 0, Freq).ShouldLog);
        for (var i = 1; i <= 4645; i++)
        {
            Assert.False(filter.Observe("ts", "libdvbpsi error (TDT/TOT decoder): Already a decoder", i, Freq).ShouldLog);
        }

        var sample = filter.Observe("ts", "libdvbpsi error (TDT/TOT decoder): Already a decoder", 30 * Freq, Freq);

        Assert.True(sample.ShouldLog);
        Assert.Equal(4645, sample.SuppressedRepeats);

        // The counter restarts with the record that was just written.
        Assert.False(filter.Observe("ts", "libdvbpsi error (TDT/TOT decoder): Already a decoder", 31 * Freq, Freq).ShouldLog);
        Assert.Equal(1, filter.Observe("ts", "libdvbpsi error (TDT/TOT decoder): Already a decoder", 60 * Freq, Freq).SuppressedRepeats);
    }

    [Fact]
    public void Observe_DifferentShapes_DoNotSuppressEachOther()
    {
        var filter = new EngineLogNoiseFilter();
        Assert.True(filter.Observe("http", "local stream 3 error: Cancellation (0x8)", 0, Freq).ShouldLog);

        // A different complaint from the same module, and the same complaint from a different module:
        // merging either of these would hide a real failure behind a noisy neighbour.
        Assert.True(filter.Observe("http", "local stream 3 error: Stream closed (0x5)", Freq, Freq).ShouldLog);
        Assert.True(filter.Observe("main", "local stream 3 error: Cancellation (0x8)", Freq, Freq).ShouldLog);
    }

    [Fact]
    public void Observe_TracksNoMoreShapesThanItsCeiling()
    {
        var filter = new EngineLogNoiseFilter();
        for (var i = 0; i < EngineLogNoiseFilter.MaximumTrackedShapes * 4; i++)
        {
            // Letters, not digits: a counter in the text would be normalized away and every "unique"
            // shape here would be the same one - which is the filter working, not the ceiling.
            Assert.True(filter.Observe("http", "unique shape " + Letters(i), i, Freq).ShouldLog);
        }

        // Nothing observable but the absence of unbounded growth: a shape evicted under pressure is
        // simply written again, which is the cheap failure this ceiling is allowed to have.
        Assert.True(filter.Observe("http", "a shape never seen before", 0, Freq).ShouldLog);
    }

    [Fact]
    public void Reset_ForgetsEveryShape()
    {
        var filter = new EngineLogNoiseFilter();
        Assert.True(filter.Observe("http", "peer error: Excessive load (0xb)", 0, Freq).ShouldLog);
        Assert.False(filter.Observe("http", "peer error: Excessive load (0xb)", Freq, Freq).ShouldLog);

        filter.Reset();

        Assert.True(filter.Observe("http", "peer error: Excessive load (0xb)", 2 * Freq, Freq).ShouldLog);
    }

    [Theory]
    [InlineData("local stream 3 error: Cancellation (0x8)", "local stream # error: Cancellation (#x#)")]
    [InlineData("buffer too late (-40771 us): dropped", "buffer too late (-# us): dropped")]
    [InlineData("More than 5 late frames, dropping frame", "More than # late frames, dropping frame")]
    [InlineData("no digits here", "no digits here")]
    [InlineData(null, "")]
    public void NormalizeShape_ReplacesDigitRunsAndNothingElse(string? message, string expected) =>
        Assert.Equal(expected, EngineLogNoiseFilter.NormalizeShape(message));

    private static string Letters(int value)
    {
        var text = string.Empty;
        do
        {
            text = (char)('a' + (value % 26)) + text;
            value /= 26;
        }
        while (value > 0);

        return text + "z";
    }
}
