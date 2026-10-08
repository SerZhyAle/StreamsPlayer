using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>
/// SP-0096 criterion 7: the open budget, driven as a sequence of observations with no window, no
/// network and no media backend. The situation it exists for - a host that accepts the open and then
/// says nothing at all, raising neither an error nor an end-of-stream - is the one the player could
/// not observe before this type existed, and it cannot be conjured on demand against a live source.
/// </summary>
public sealed class PlaybackOpenBudgetTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>Observes every two seconds - the player's stats cadence - up to and including <paramref name="until"/>.</summary>
    private static PlaybackOpenVerdict Drive(PlaybackOpenBudget budget, double until, Func<double, long?> bytesAt)
    {
        var verdict = PlaybackOpenVerdict.None;
        for (var second = 2d; second <= until; second += 2)
        {
            verdict = budget.Observe(At(second), bytesAt(second));
            if (verdict != PlaybackOpenVerdict.None)
            {
                return verdict;
            }
        }

        return verdict;
    }

    [Fact]
    public void SilentSource_IsDeadAtTheDeadSourceThreshold()
    {
        var budget = new PlaybackOpenBudget();
        Assert.Equal(PlaybackOpenVerdict.DeadSource, Drive(budget, 20, _ => 0L));
    }

    [Fact]
    public void SilentSource_IsStillGivenItsFullDeadSourceWindow()
    {
        // The threshold is a floor, not a hint: a source at 7.9 s has not yet earned a verdict, and an
        // off-by-one here would fail channels on a slow first playlist fetch.
        var budget = new PlaybackOpenBudget();
        Assert.Equal(PlaybackOpenVerdict.None, budget.Observe(At(7.9), 0L));
    }

    [Fact]
    public void SourceThatAnswered_NeverTakesTheDeadBranch()
    {
        // The whole point of the two branches: bytes by the fourth second prove the host exists, so the
        // eight second verdict is off the table and only the lenient deadline remains. The first
        // observation deliberately still reads zero - a source is allowed a slow first playlist fetch.
        var budget = new PlaybackOpenBudget();
        Assert.Equal(PlaybackOpenVerdict.Deadline, Drive(budget, 30, second => second < 4 ? 0L : 3046L));
    }

    [Fact]
    public void NoCounter_LosesTheDeadBranchAndKeepsTheDeadline()
    {
        // FlyleafLib and MediaElement report no bytes. Null is "no evidence", never "nothing arrived" -
        // an engine must not be condemned by its own lack of telemetry.
        var budget = new PlaybackOpenBudget();
        Assert.Equal(PlaybackOpenVerdict.Deadline, Drive(budget, 30, _ => null));
    }

    [Fact]
    public void VerdictIsReportedExactlyOnce()
    {
        // What stops the next tick raising a second failure dialog over the first.
        var budget = new PlaybackOpenBudget();
        Assert.Equal(PlaybackOpenVerdict.DeadSource, budget.Observe(At(8), 0L));
        Assert.Equal(PlaybackOpenVerdict.None, budget.Observe(At(10), 0L));
        Assert.Equal(PlaybackOpenVerdict.None, budget.Observe(At(30), 0L));
    }

    [Fact]
    public void Reset_ArmsTheNextLeg()
    {
        var budget = new PlaybackOpenBudget();
        Assert.Equal(PlaybackOpenVerdict.DeadSource, budget.Observe(At(8), 0L));
        budget.Reset();
        Assert.Equal(PlaybackOpenVerdict.None, budget.Observe(At(2), 0L));
        Assert.Equal(PlaybackOpenVerdict.DeadSource, budget.Observe(At(8), 0L));
    }

    [Fact]
    public void GoingLiveOnTheLastSecond_IsNotKilledByTheNextObservation()
    {
        // The regression this guards against is the cruel one: a channel that did play, taken off the
        // screen by a rule meant for channels that did not.
        var budget = new PlaybackOpenBudget();
        Assert.Equal(PlaybackOpenVerdict.None, budget.Observe(At(18), 400_000L));
        budget.NotifyLive();
        Assert.Equal(PlaybackOpenVerdict.None, budget.Observe(At(20), 400_000L));
        Assert.Equal(PlaybackOpenVerdict.None, budget.Observe(At(60), 400_000L));
    }

    [Fact]
    public void TheDeadlineOutranksTheDeadBranchWhenBothAreDue()
    {
        // A caller that only observes once, late, must get the verdict that describes the whole budget
        // rather than the earlier one it happened to skip past. The deadline is the one that re-opens.
        var budget = new PlaybackOpenBudget();
        Assert.Equal(PlaybackOpenVerdict.Deadline, budget.Observe(At(25), 0L));
    }

    [Fact]
    public void ThresholdsKeepTheirMeasuredMargins()
    {
        // Both numbers were chosen against 311 timed openings (p90 5.3 s, p99 11.2 s) and a logged
        // healthy open that read 3046 bytes at two seconds. Anyone tidying them into rounder numbers
        // should have to change this test and read why first.
        Assert.Equal(TimeSpan.FromSeconds(8), PlaybackOpenBudget.DeadSourceAfter);
        Assert.Equal(TimeSpan.FromSeconds(20), PlaybackOpenBudget.OpenDeadline);
        Assert.True(PlaybackOpenBudget.DeadSourceAfter < PlaybackOpenBudget.OpenDeadline);
    }

    /// <summary>
    /// SP-0203: the dead-source branch judges the attempt's own clock, while the deadline stays
    /// leg-scoped - a silent LAN endpoint moves the attempt list on early, and a list of silent
    /// endpoints may not stretch one leg without end.
    /// </summary>
    [Fact]
    public void AShortAttemptSliceDiesTheAttemptEarlyAndKeepsTheLegDeadline()
    {
        var budget = new PlaybackOpenBudget(TimeSpan.FromSeconds(4));
        Assert.Equal(PlaybackOpenVerdict.None, budget.Observe(At(3), At(3), 0L));
        Assert.Equal(PlaybackOpenVerdict.DeadSource, budget.Observe(At(4.5), At(4.5), 0L));
    }

    [Fact]
    public void BytesAnsweredOnTheAttemptRetireItsDeadBranchButNotTheLegDeadline()
    {
        var budget = new PlaybackOpenBudget(TimeSpan.FromSeconds(4));
        Assert.Equal(PlaybackOpenVerdict.None, budget.Observe(At(2), At(2), 100L));
        Assert.Equal(PlaybackOpenVerdict.None, budget.Observe(At(8), At(6), 0L)); // answering slowly: the deadline's business
        Assert.Equal(PlaybackOpenVerdict.Deadline, budget.Observe(At(21), At(19), 0L));
    }

    [Fact]
    public void TheDefaultBudgetBehavesExactlyAsTheSingleClockForm()
    {
        var twoClock = new PlaybackOpenBudget();
        var single = new PlaybackOpenBudget();
        Assert.Equal(single.Observe(At(6), 0L), twoClock.Observe(At(6), At(6), 0L));
        Assert.Equal(single.Observe(At(9), 0L), twoClock.Observe(At(9), At(9), 0L));
    }
}
