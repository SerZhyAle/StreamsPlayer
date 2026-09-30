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

    private const string Burst = "libdvbpsi error (TDT/TOT decoder): Already a decoder";

    [Fact]
    public void FlushAll_ABurstFollowedByTeardown_WritesItsCount()
    {
        var filter = new EngineLogNoiseFilter();
        Assert.True(filter.Observe("ts", Burst, 0, Freq, "Error").ShouldLog);
        for (var i = 1; i <= 1000; i++)
        {
            Assert.False(filter.Observe("ts", Burst, i, Freq, "Error").ShouldLog);
        }

        // The engine closes inside the window: before SP-0134 these thousand records left no trace.
        var owed = Assert.Single(filter.FlushAll());

        Assert.Equal(1000, owed.Repeats);
        Assert.Equal("ts", owed.Module);
        Assert.Equal("Error", owed.Level);
        Assert.Equal(Burst, owed.Message);
        Assert.Equal(EngineLogRepeatReason.Closed, owed.Reason);
        Assert.Empty(filter.FlushAll());
    }

    [Fact]
    public void FlushAll_ShapesThatOweNothing_WriteNothing()
    {
        var filter = new EngineLogNoiseFilter();
        Assert.True(filter.Observe("ts", Burst, 0, Freq).ShouldLog);

        Assert.Empty(filter.FlushAll());
    }

    [Fact]
    public void Flush_ABurstThatEnded_WritesItsCountOnceTheWindowRunsOut()
    {
        var filter = new EngineLogNoiseFilter();
        Assert.True(filter.Observe("ts", Burst, 0, Freq).ShouldLog);
        Assert.False(filter.Observe("ts", Burst, Freq, Freq).ShouldLog);
        Assert.False(filter.Observe("ts", Burst, 2 * Freq, Freq).ShouldLog);

        Assert.Empty(filter.Flush(29 * Freq, Freq));
        var owed = Assert.Single(filter.Flush(30 * Freq, Freq));

        Assert.Equal(2, owed.Repeats);
        Assert.Equal(EngineLogRepeatReason.WindowClosed, owed.Reason);

        // Reported once: nothing is owed until the shape speaks again.
        Assert.Empty(filter.Flush(90 * Freq, Freq));
        Assert.Empty(filter.FlushAll());
    }

    [Fact]
    public void Flush_ARunningBurst_CostsOneCountPerWindowAndNoExtraRecord()
    {
        var filter = new EngineLogNoiseFilter();
        Assert.True(filter.Observe("ts", Burst, 0, Freq).ShouldLog);
        Assert.False(filter.Observe("ts", Burst, 10 * Freq, Freq).ShouldLog);
        Assert.Equal(1, Assert.Single(filter.Flush(30 * Freq, Freq)).Repeats);

        // The count restarted the window, so the burst's next record is suppressed, not written afresh.
        Assert.False(filter.Observe("ts", Burst, 31 * Freq, Freq).ShouldLog);
        Assert.Equal(1, Assert.Single(filter.Flush(60 * Freq, Freq)).Repeats);
    }

    [Fact]
    public void Flush_AnEvictedShapeThatOwedACount_WritesIt()
    {
        var filter = new EngineLogNoiseFilter();
        Assert.True(filter.Observe("ts", Burst, 0, Freq, "Warning").ShouldLog);
        Assert.False(filter.Observe("ts", Burst, 1, Freq, "Warning").ShouldLog);
        Assert.False(filter.Observe("ts", Burst, 2, Freq, "Warning").ShouldLog);

        // Fill the map past its ceiling: the burst above is the oldest shape and is evicted first.
        for (var i = 0; i < EngineLogNoiseFilter.MaximumTrackedShapes; i++)
        {
            Assert.True(filter.Observe("http", "unique shape " + Letters(i), 10 + i, Freq).ShouldLog);
        }

        var owed = Assert.Single(filter.Flush(20 + EngineLogNoiseFilter.MaximumTrackedShapes, Freq));

        Assert.Equal(2, owed.Repeats);
        Assert.Equal(Burst, owed.Message);
        Assert.Equal(EngineLogRepeatReason.Evicted, owed.Reason);
    }

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
