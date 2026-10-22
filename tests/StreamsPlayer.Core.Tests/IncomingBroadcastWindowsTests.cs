using StreamsPlayer.Core;

namespace StreamsPlayer.Core.Tests;

/// <summary>SP-0222 requirement 3: the number of incoming broadcasts held open at once is bounded, by SP-0215's figure.</summary>
public sealed class IncomingBroadcastWindowsTests
{
    [Fact]
    public void TheProductBoundIsTheOneFigureSp0215States()
    {
        Assert.Equal(ExchangeBroadcastResources.MaximumForwarders, IncomingBroadcastWindows.Default.Maximum);
    }

    [Fact]
    public void ThePlaceAfterTheLastIsRefusedAndAReleasedPlaceOpensAgain()
    {
        var windows = new IncomingBroadcastWindows(2);

        Assert.True(windows.TryAcquire(out var first));
        Assert.True(windows.TryAcquire(out var second));
        Assert.False(windows.TryAcquire(out var refused));
        Assert.Null(refused);
        Assert.Equal(2, windows.Count);

        first!.Dispose();
        Assert.Equal(1, windows.Count);
        Assert.True(windows.TryAcquire(out var third));
        Assert.False(windows.TryAcquire(out _));

        second!.Dispose();
        third!.Dispose();
        Assert.Equal(0, windows.Count);
    }

    [Fact]
    public void DisposingALeaseTwiceReturnsOnePlace()
    {
        var windows = new IncomingBroadcastWindows(1);
        Assert.True(windows.TryAcquire(out var lease));

        lease!.Dispose();
        lease.Dispose();

        Assert.Equal(0, windows.Count);
        Assert.True(windows.TryAcquire(out _));
        Assert.False(windows.TryAcquire(out _));
    }

    [Fact]
    public void ConcurrentOpensNeverExceedTheBound()
    {
        var windows = new IncomingBroadcastWindows(5);
        var granted = 0;

        Parallel.For(0, 200, attempt =>
        {
            if (windows.TryAcquire(out _))
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(5, granted);
        Assert.Equal(5, windows.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveBoundIsRefused(int maximum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IncomingBroadcastWindows(maximum));
    }
}
