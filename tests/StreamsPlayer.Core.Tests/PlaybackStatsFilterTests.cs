namespace StreamsPlayer.Core.Tests;

public sealed class PlaybackStatsFilterTests
{
    private const long Freq = 10_000_000; // 10 MHz ticks -> 1 sec = 10,000,000 ticks

    [Fact]
    public void ShouldLog_FirstSampleOfLeg_AlwaysReturnsTrue()
    {
        var filter = new PlaybackStatsFilter();
        var shouldLog = filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 30.0, 1 * Freq, Freq);
        Assert.True(shouldLog);
    }

    [Fact]
    public void ShouldLog_SteadyStateWithinHeartbeat_ReturnsFalse()
    {
        var filter = new PlaybackStatsFilter();
        // First sample
        Assert.True(filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 30.0, 0, Freq));

        // Sample 2s later
        Assert.False(filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 30.0, 2 * Freq, Freq));

        // Sample 20s later
        Assert.False(filter.ShouldLog("STATS", 0, 0, 0, 2400.0, 29.9, 20 * Freq, Freq));
    }

    [Fact]
    public void ShouldLog_HeartbeatAfter30Seconds_ReturnsTrue()
    {
        var filter = new PlaybackStatsFilter();
        Assert.True(filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 30.0, 0, Freq));

        // 28s later -> false
        Assert.False(filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 30.0, 28 * Freq, Freq));

        // 30s later -> true
        Assert.True(filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 30.0, 30 * Freq, Freq));

        // 32s (2s after heartbeat) -> false
        Assert.False(filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 30.0, 32 * Freq, Freq));
    }

    [Fact]
    public void ShouldLog_LossCountersIncrease_ReturnsTrueImmediately()
    {
        var filter = new PlaybackStatsFilter();
        Assert.True(filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 30.0, 0, Freq));

        // 4s later, lostPictures increases
        Assert.True(filter.ShouldLog("STATS", 1, 0, 0, 2500.0, 30.0, 4 * Freq, Freq));

        // 6s later, same lostPictures -> false
        Assert.False(filter.ShouldLog("STATS", 1, 0, 0, 2500.0, 30.0, 6 * Freq, Freq));

        // 8s later, corrupted increases
        Assert.True(filter.ShouldLog("STATS", 1, 2, 0, 2500.0, 30.0, 8 * Freq, Freq));

        // 10s later, discontinuity increases
        Assert.True(filter.ShouldLog("STATS", 1, 2, 1, 2500.0, 30.0, 10 * Freq, Freq));
    }

    [Fact]
    public void ShouldLog_StarvationRate_ReturnsTrueImmediately()
    {
        var filter = new PlaybackStatsFilter();
        Assert.True(filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 30.0, 0, Freq));

        // 4s later, in_kbps drops to 0.0
        Assert.True(filter.ShouldLog("STATS", 0, 0, 0, 0.0, 30.0, 4 * Freq, Freq));

        // 6s later, disp_fps drops to 0.0
        Assert.True(filter.ShouldLog("STATS", 0, 0, 0, 1500.0, 0.0, 6 * Freq, Freq));
    }

    [Fact]
    public void ShouldLog_ExplicitTag_AlwaysReturnsTrue()
    {
        var filter = new PlaybackStatsFilter();
        Assert.True(filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 30.0, 0, Freq));

        // 2s later with STALL STATS
        Assert.True(filter.ShouldLog("STALL STATS", 0, 0, 0, 0.0, 0.0, 2 * Freq, Freq));

        // 4s later with RESUME STATS
        Assert.True(filter.ShouldLog("RESUME STATS", 0, 0, 0, 2000.0, 25.0, 4 * Freq, Freq));
    }

    [Fact]
    public void Reset_AllowsFirstSampleOfNewLeg()
    {
        var filter = new PlaybackStatsFilter();
        Assert.True(filter.ShouldLog("STATS", 5, 2, 1, 2500.0, 30.0, 0, Freq));
        Assert.False(filter.ShouldLog("STATS", 5, 2, 1, 2500.0, 30.0, 2 * Freq, Freq));

        filter.Reset();

        // After reset, first sample is logged again
        Assert.True(filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 30.0, 4 * Freq, Freq));
    }

    /// <summary>
    /// SP-0134: starvation was detected by comparing text with "0.0" while the rate was formatted in the
    /// user's regional format - "0,0" under these cultures - so a starved sample waited for the heartbeat.
    /// </summary>
    [Theory]
    [InlineData("uk-UA")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void ShouldLog_StarvationIsDetectedWhateverTheRegionalFormat(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
        try
        {
            var filter = new PlaybackStatsFilter();
            Assert.True(filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 30.0, 0, Freq));
            Assert.False(filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 30.0, 2 * Freq, Freq));

            // 0.04 is what the log prints as 0.0: starved, and written at full resolution.
            Assert.True(filter.ShouldLog("STATS", 0, 0, 0, 0.04, 30.0, 4 * Freq, Freq));
            Assert.True(filter.ShouldLog("STATS", 0, 0, 0, 2500.0, 0.0, 6 * Freq, Freq));

            // And the line that records it reads the same on every machine.
            Assert.Equal("0.0", PlaybackStatsFilter.FormatRate(0.04));
            Assert.Equal("2500.5", PlaybackStatsFilter.FormatRate(2500.5));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ShouldLog_UnmeasuredRateIsNotStarvation()
    {
        var filter = new PlaybackStatsFilter();
        Assert.True(filter.ShouldLog("STATS", 0, 0, 0, null, null, 0, Freq));

        // The first sample of a leg has no interval to difference over; that is not a stalled stream.
        Assert.False(filter.ShouldLog("STATS", 0, 0, 0, null, null, 2 * Freq, Freq));
        Assert.Equal("n/a", PlaybackStatsFilter.FormatRate(null));
    }
}
