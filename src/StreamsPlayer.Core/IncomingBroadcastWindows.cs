namespace StreamsPlayer.Core;

/// <summary>
/// SP-0222 requirement 3: how many incoming broadcasts this PC holds open at once from the account's list and
/// by link. SP-0215 already bounds the local listeners that relay and tunnel playback hold open
/// (<see cref="ExchangeBroadcastResources.MaximumForwarders"/>), but an audio broadcast over the relay holds a
/// connection and no listener, so that number alone would not bound the windows. The windows are bounded by the
/// same number, so the product has one figure to state, and the list says so when it is reached.
/// </summary>
public sealed class IncomingBroadcastWindows(int maximum)
{
    private readonly object _gate = new();
    private int _count;

    /// <summary>The product's bound: the one figure SP-0215 states for connections held open.</summary>
    public static IncomingBroadcastWindows Default { get; } = new(ExchangeBroadcastResources.MaximumForwarders);

    public int Maximum { get; } = maximum > 0 ? maximum : throw new ArgumentOutOfRangeException(nameof(maximum));

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Takes one place, or <c>false</c> when the bound is reached. The lease returns the place when disposed;
    /// disposing it twice returns it once.
    /// </summary>
    public bool TryAcquire(out IDisposable? lease)
    {
        lock (_gate)
        {
            if (_count >= Maximum)
            {
                lease = null;
                return false;
            }

            _count++;
        }

        lease = new Lease(this);
        return true;
    }

    private void Release()
    {
        lock (_gate)
        {
            _count--;
        }
    }

    private sealed class Lease(IncomingBroadcastWindows owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
