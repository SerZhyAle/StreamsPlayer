using System.Net;

namespace StreamsPlayer.Core.Tests;

/// <summary>The brute-force damper on its own: the schedule of delays, the reset, the forgetting and the bound.</summary>
public sealed class BroadcastLanServerDamperTests
{
    private static readonly IPAddress SourceA = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress SourceB = IPAddress.Parse("192.0.2.2");
    private static readonly IPAddress SourceC = IPAddress.Parse("192.0.2.3");

    [Fact]
    public void EachRefusedAttemptCostsALongerWaitUpToOneSecond()
    {
        var damper = new BroadcastAttemptDamper();

        var delays = Enumerable.Range(0, 6).Select(_ => damper.RegisterFailure(SourceA).TotalMilliseconds).ToArray();

        Assert.Equal(new[] { 250d, 500d, 750d, 1000d, 1000d, 1000d }, delays);
    }

    [Fact]
    public void ARightAnswerClearsTheSourceAndSourcesAreCountedApart()
    {
        var damper = new BroadcastAttemptDamper();
        damper.RegisterFailure(SourceA);
        damper.RegisterFailure(SourceA);

        Assert.Equal(250, damper.RegisterFailure(SourceB).TotalMilliseconds);
        damper.RegisterSuccess(SourceA);

        Assert.Equal(250, damper.RegisterFailure(SourceA).TotalMilliseconds);
    }

    [Fact]
    public void AQuietMinuteIsForgiven()
    {
        var clock = new BroadcastTestClock();
        var damper = new BroadcastAttemptDamper(clock);
        damper.RegisterFailure(SourceA);
        damper.RegisterFailure(SourceA);

        clock.Advance(BroadcastAttemptDamper.Forget + TimeSpan.FromSeconds(1));

        Assert.Equal(250, damper.RegisterFailure(SourceA).TotalMilliseconds);
    }

    [Fact]
    public void TheTableIsBoundedAndDropsTheLongestQuietSourceFirst()
    {
        var clock = new BroadcastTestClock();
        var damper = new BroadcastAttemptDamper(clock, capacity: 2);
        damper.RegisterFailure(SourceA);
        damper.RegisterFailure(SourceA);
        clock.Advance(TimeSpan.FromSeconds(1));
        damper.RegisterFailure(SourceB);
        clock.Advance(TimeSpan.FromSeconds(1));

        damper.RegisterFailure(SourceC);

        // A was the longest quiet of the two held and made room for C, so it starts again - and its return in turn
        // drops B, the next oldest; C, held throughout, carries on counting.
        Assert.Equal(250, damper.RegisterFailure(SourceA).TotalMilliseconds);
        Assert.Equal(500, damper.RegisterFailure(SourceC).TotalMilliseconds);
    }

    [Fact]
    public void AnUnknownSourceIsHeldBackTheFullSecond()
    {
        Assert.Equal(1000, new BroadcastAttemptDamper().RegisterFailure(null).TotalMilliseconds);
    }
}
