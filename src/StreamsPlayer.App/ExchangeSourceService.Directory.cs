namespace StreamsPlayer.App;

/// <summary>
/// SP-0222 requirement 1: the account's directory is fetched because a person looked, never because the
/// session came up. Two kinds of lease hold it. A <em>follow</em> lease is the open broadcast list: the session
/// lists, subscribes once, and applies what is pushed. An <em>ask</em> lease is one use of a menu that needs
/// the receivers once: the session lists, and nothing follows. The wire has no <c>unsubscribe</c> (the control
/// stream also carries casts and this PC's own broadcasting, so it cannot be dropped to end a subscription),
/// so the last lease released ends the following on this side only: pushes still arrive, are read and
/// discarded, and nothing is sent for them.
/// </summary>
internal sealed partial class ExchangeSourceService
{
    private readonly object _leaseGate = new();
    private int _followLeases;
    private int _askLeases;

    // Whether the held snapshot answers a list of the lease epoch now running. A push may be applied only onto
    // such a snapshot: after the last lease went, the loop's state is the old directory, and a push applied to
    // it would present a stale directory as the current one (the revision gap rule cannot see the difference
    // when nothing changed while it was closed).
    private volatile bool _directoryFresh;
    private int _refreshing;

    internal bool IsDirectoryFresh => _directoryFresh;

    private bool HoldsDirectory
    {
        get
        {
            lock (_leaseGate)
            {
                return _followLeases + _askLeases > 0;
            }
        }
    }

    private bool HoldsFollow
    {
        get
        {
            lock (_leaseGate)
            {
                return _followLeases > 0;
            }
        }
    }

    /// <summary>
    /// Takes the directory. The list is requested at once when a session is up and otherwise at the start of the
    /// next one; a lease taken while another is held asks again, because a new follower needs an answer to
    /// subscribe after. Dispose the lease to give it back.
    /// </summary>
    internal IDisposable AcquireDirectory(bool follow)
    {
        bool firstFollower;
        lock (_leaseGate)
        {
            firstFollower = follow && _followLeases == 0;
            if (follow)
            {
                _followLeases++;
            }
            else
            {
                _askLeases++;
            }
        }

        if (firstFollower || !follow)
        {
            // Following resumes from this lease's own list: what the loop holds from before is not a base for a push.
            _directoryFresh = false;
        }

        RequestList();
        return new DirectoryLease(this, follow);
    }

    /// <summary>
    /// Completes when the held directory has an answer, or when the time ends. False on the timeout; the caller
    /// shows what it has (no receivers) rather than blocking the person on a server that does not answer.
    /// </summary>
    internal async Task<bool> WaitForDirectoryAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Directory is not null && _directoryFresh)
        {
            return true;
        }

        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe()
        {
            if (Directory is not null && _directoryFresh)
            {
                answered.TrySetResult();
            }
        }

        DirectoryChanged += Observe;
        try
        {
            if (Directory is not null && _directoryFresh)
            {
                return true;
            }

            await answered.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            DirectoryChanged -= Observe;
        }
    }

    /// <summary>
    /// SP-0233 requirement 5: explicitly refreshes the account directory on demand.
    /// Re-requests the directory list and waits for the directory frame or timeout.
    /// Protects against overlapping refreshes using an atomic gate.
    /// </summary>
    internal async Task<bool> RefreshDirectoryAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return true;
        }

        try
        {
            _directoryFresh = false;
            RequestList();
            return await WaitForDirectoryAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>
    /// One started read-only session per guest entry this PC holds (SP-0222 requirement 5). The caller owns
    /// them: they live as long as its list is open and are disposed when it closes.
    /// </summary>
    internal IReadOnlyList<ExchangeGuestDirectoryViewer> OpenGuestViewers(Action<Exception>? reportFault = null)
    {
        var viewers = new List<ExchangeGuestDirectoryViewer>();
        foreach (var entry in Guests.Entries)
        {
            var viewer = new ExchangeGuestDirectoryViewer(entry, reportFault, _trustBook.For(entry.DeviceId));
            viewers.Add(viewer);
            viewer.Start();
        }

        return viewers;
    }

    private void RequestList()
    {
        // Fire and forget by design: the answer is the directory frame the loop reads, and a session that is not
        // up yet sends the list itself when it starts (HoldAsync). A write that did not land costs the person an
        // empty list, which the exchange's own status line explains.
        _ = PostControlEnvelopeAsync(new { schemaVersion = 2, type = "list" }, CancellationToken.None);
    }

    private void ReleaseDirectory(bool follow)
    {
        bool none;
        lock (_leaseGate)
        {
            if (follow)
            {
                _followLeases = Math.Max(0, _followLeases - 1);
            }
            else
            {
                _askLeases = Math.Max(0, _askLeases - 1);
            }

            none = _followLeases + _askLeases == 0;
        }

        if (none)
        {
            // Not ClearDirectory(): that is the end of a session and also resets the producer's endpoint facts.
            _directoryFresh = false;
            Directory = null;
            DirectoryChanged?.Invoke();
        }
    }

    private sealed class DirectoryLease(ExchangeSourceService owner, bool follow) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.ReleaseDirectory(follow);
            }
        }
    }
}
