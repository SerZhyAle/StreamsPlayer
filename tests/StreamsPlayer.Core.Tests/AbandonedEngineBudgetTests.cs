namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0166 requirement 3: abandoned engines are counted, and past the cap the feature pauses.</summary>
public sealed class AbandonedEngineBudgetTests
{
    [Fact]
    public void ANewBudgetIsNotPausedAndHoldsNothing()
    {
        var budget = new AbandonedEngineBudget();

        Assert.False(budget.IsPaused);
        Assert.Equal(0, budget.Outstanding);
    }

    [Fact]
    public void TheCapPausesTheFeatureAndOnlyThatCallSaysSo()
    {
        var budget = new AbandonedEngineBudget(cap: 3);

        Assert.False(budget.RecordAbandoned());
        Assert.False(budget.RecordAbandoned());
        Assert.False(budget.IsPaused);

        Assert.True(budget.RecordAbandoned()); // the one the caller logs the pause for
        Assert.True(budget.IsPaused);
        Assert.Equal(3, budget.Outstanding);

        Assert.False(budget.RecordAbandoned()); // a late extra engine is counted, not announced again
        Assert.Equal(4, budget.Outstanding);
    }

    [Fact]
    public void AReleaseLowersTheCountButTheFeatureStaysPaused()
    {
        var budget = new AbandonedEngineBudget(cap: 2);
        budget.RecordAbandoned();
        budget.RecordAbandoned();

        budget.RecordReleased();
        budget.RecordReleased();

        Assert.Equal(0, budget.Outstanding);
        Assert.True(budget.IsPaused);
    }

    [Fact]
    public void AReleaseBeforeTheCapKeepsTheFeatureRunning()
    {
        var budget = new AbandonedEngineBudget(cap: 2);
        budget.RecordAbandoned();
        budget.RecordReleased();

        Assert.False(budget.RecordAbandoned());
        Assert.False(budget.IsPaused);
    }

    [Fact]
    public void AReleaseWithNothingOutstandingDoesNotGoNegative()
    {
        var budget = new AbandonedEngineBudget();

        budget.RecordReleased();

        Assert.Equal(0, budget.Outstanding);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ACapBelowOneIsRejected(int cap) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AbandonedEngineBudget(cap));

    [Fact]
    public async Task ConcurrentAbandonsPauseExactlyOnce()
    {
        var budget = new AbandonedEngineBudget(cap: 8);
        var announced = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            if (budget.RecordAbandoned())
            {
                Interlocked.Increment(ref announced);
            }
        })));

        Assert.Equal(1, announced);
        Assert.Equal(64, budget.Outstanding);
        Assert.True(budget.IsPaused);
    }
}
